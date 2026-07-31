using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Locates agent CLI executables (claude / codex) via the system search command
/// (where.exe on Windows, which elsewhere) with well-known install locations as
/// fallback. Results are cached per CLI name for the process lifetime and
/// re-validated against the file system before reuse.
/// </summary>
internal static class AgentCliExecutableLocator
{
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SearchCommandTimeout = TimeSpan.FromSeconds(5);

    public static async Task<string?> LocateAsync(
        string cliName,
        IReadOnlyList<string> candidatePaths,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cliName, out var cached))
        {
            if (File.Exists(cached))
            {
                return cached;
            }

            _cache.TryRemove(cliName, out _);
        }

        var resolved = await SearchSystemPathAsync(cliName, cancellationToken).ConfigureAwait(false)
            ?? candidatePaths.FirstOrDefault(File.Exists);

        if (resolved != null)
        {
            _cache[cliName] = resolved;
            Debug.WriteLine($"[AgentCli] Located '{cliName}' at {resolved}");
        }
        else
        {
            Debug.WriteLine($"[AgentCli] '{cliName}' not found on PATH or in candidate locations");
        }

        return resolved;
    }

    internal static void InvalidateCache(string cliName) => _cache.TryRemove(cliName, out _);

    private static async Task<string?> SearchSystemPathAsync(string cliName, CancellationToken cancellationToken)
    {
        var searchCommand = OperatingSystem.IsWindows() ? "where.exe" : "which";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = searchCommand,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add(cliName);

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(SearchCommandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string output;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            output = await stdoutTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        // where.exe can return multiple matches (e.g. both a .cmd shim and an .exe);
        // take the first line that exists and is directly runnable.
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !File.Exists(line))
            {
                continue;
            }

            if (!OperatingSystem.IsWindows() || HasRunnableExtension(line))
            {
                return line;
            }
        }

        return null;
    }

    private static bool HasRunnableExtension(string path)
    {
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".com", StringComparison.OrdinalIgnoreCase);
    }
}
