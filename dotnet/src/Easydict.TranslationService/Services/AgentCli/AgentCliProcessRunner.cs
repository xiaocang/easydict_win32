using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Raised by <see cref="AgentCliProcessRunner"/> when the CLI process exits with a
/// non-zero code. Carries the captured stderr so callers can classify the failure
/// together with the stdout control lines they collected while enumerating.
/// </summary>
internal sealed class AgentCliProcessException : Exception
{
    public AgentCliProcessException(int exitCode, string stdErr)
        : base($"CLI process exited with code {exitCode}")
    {
        ExitCode = exitCode;
        StdErr = stdErr;
    }

    public int ExitCode { get; }
    public string StdErr { get; }
}

/// <summary>
/// Spawns a local agent CLI (claude / codex) and streams its stdout line by line.
/// The prompt is always written to stdin — never passed on the command line — to
/// avoid Windows command-line length limits and cmd.exe escaping issues.
/// Mirrors the process-lifecycle handling of FoundryLocalCliEndpointResolver.
/// </summary>
internal sealed class AgentCliProcessRunner
{
    private const int StdErrCaptureLimitChars = 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // cmd.exe metacharacters that must never reach a .cmd shim invocation.
    // All argv content is fixed literals or whitelisted model names, so hitting
    // this guard indicates a programming error, not a user-input problem.
    private static readonly char[] CmdUnsafeCharacters = ['&', '|', '<', '>', '^', '%', '\r', '\n', '\0'];

    /// <summary>
    /// Run the CLI and yield stdout lines as they arrive. Throws
    /// <see cref="AgentCliProcessException"/> after the stream ends if the process
    /// exited non-zero, <see cref="TimeoutException"/> when <paramref name="timeout"/>
    /// elapses, and <see cref="OperationCanceledException"/> on caller cancellation.
    /// The process (and its child tree) is killed on cancellation, timeout, or
    /// early enumerator disposal.
    /// </summary>
    public async IAsyncEnumerable<string> RunLinesAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string stdinText,
        TimeSpan? timeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = new Process();
        process.StartInfo = CreateStartInfo(executablePath, arguments);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new AgentCliProcessException(-1, $"Failed to start '{executablePath}': {ex.Message}");
        }

        Debug.WriteLine($"[AgentCli] Started pid={process.Id}: {executablePath} {string.Join(' ', arguments)}");

        var stdErrBuffer = new StringBuilder();
        var stdErrTask = PumpStdErrAsync(process, stdErrBuffer);
        var stdInTask = WriteStdInAsync(process, stdinText, linkedCts.Token);

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ThrowCancellationOrTimeout(cancellationToken, timeout ?? DefaultTimeout);
                    throw; // unreachable
                }

                if (line is null)
                    break;

                yield return line;
            }

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ThrowCancellationOrTimeout(cancellationToken, timeout ?? DefaultTimeout);
                throw; // unreachable
            }

            await stdInTask.ConfigureAwait(false);
            await stdErrTask.ConfigureAwait(false);

            Debug.WriteLine($"[AgentCli] Exited pid={process.Id}, exitCode={process.ExitCode}, stderrChars={stdErrBuffer.Length}");

            if (process.ExitCode != 0)
            {
                throw new AgentCliProcessException(process.ExitCode, stdErrBuffer.ToString());
            }
        }
        finally
        {
            KillProcessTreeQuietly(process);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Neutral directory so the CLI never scans the app's install folder.
            WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        if (IsCmdShim(executablePath))
        {
            // npm global installs are .cmd batch shims; CreateProcess needs cmd.exe
            // for those, and cmd.exe does not honor CRT argument quoting for
            // metacharacters — hence the strict whitelist guard.
            foreach (var argument in arguments)
            {
                if (argument.IndexOfAny(CmdUnsafeCharacters) >= 0)
                {
                    throw new InvalidOperationException(
                        $"Argument contains characters unsafe for a .cmd shim: {argument}");
                }
            }

            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{BuildCommandLine(executablePath, arguments)}\"";
        }
        else
        {
            startInfo.FileName = executablePath;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    internal static bool IsCmdShim(string path)
    {
        return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteArgument(executablePath));
        foreach (var argument in arguments)
        {
            sb.Append(' ').Append(QuoteArgument(argument));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quote a single argument using MSVCRT rules (backslash-doubling before quotes).
    /// </summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }

        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static async Task WriteStdInAsync(Process process, string text, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Process exited before consuming stdin — exit-code handling reports the outcome.
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Process disposed after early enumerator disposal.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    private static async Task PumpStdErrAsync(Process process, StringBuilder buffer)
    {
        var chunk = new char[4096];
        try
        {
            while (true)
            {
                var read = await process.StandardError.ReadAsync(chunk).ConfigureAwait(false);
                if (read <= 0)
                    break;

                if (buffer.Length < StdErrCaptureLimitChars)
                {
                    buffer.Append(chunk, 0, Math.Min(read, StdErrCaptureLimitChars - buffer.Length));
                }
            }
        }
        catch (IOException)
        {
            // Pipe closed during process teardown.
        }
        catch (ObjectDisposedException)
        {
            // Process disposed after early enumerator disposal.
        }
    }

    private static void ThrowCancellationOrTimeout(CancellationToken userToken, TimeSpan timeout)
    {
        if (userToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(userToken);
        }

        throw new TimeoutException($"CLI did not finish within {timeout.TotalSeconds:F0}s.");
    }

    private static void KillProcessTreeQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
