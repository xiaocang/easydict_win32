using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.SidecarClient;

/// <summary>
/// JSON Lines over stdio client.
///
/// - stdin: one request JSON object per line
/// - stdout: one response JSON object per line
/// - stderr: structured logs (passed through as raw lines)
/// </summary>
public sealed class SidecarClient : IAsyncDisposable
{
    private readonly SidecarClientOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json;

    private Process? _process;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private Task? _exitWatcher;
    private bool _disposed;

    public SidecarClient(SidecarClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// Called for each stderr line produced by the sidecar.
    /// </summary>
    public event Action<string>? StderrLine;

    public bool IsRunning
    {
        get
        {
            var p = _process;
            return p is not null && !p.HasExited;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            if (_process is not null)
            {
                if (!_process.HasExited)
                {
                    return Task.CompletedTask;
                }

                // For Milestone 0B, keep lifecycle simple: do not restart in-place.
                // Create a new SidecarClient instance instead.
                throw new SidecarProcessExitedException(_process.ExitCode, "Sidecar process already exited; create a new SidecarClient to restart.");
            }

            _lifetimeCts = new CancellationTokenSource();

            var psi = _options.ToStartInfo();
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            if (!proc.Start())
            {
                throw new InvalidOperationException("Failed to start sidecar process.");
            }

            _process = proc;
            _stdoutLoop = Task.Run(() => StdoutLoopAsync(proc, _lifetimeCts.Token), _lifetimeCts.Token);
            _stderrLoop = Task.Run(() => StderrLoopAsync(proc, _lifetimeCts.Token), _lifetimeCts.Token);
            _exitWatcher = Task.Run(() => ExitWatcherAsync(proc, _lifetimeCts.Token), _lifetimeCts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a request and returns the raw <c>result</c> JSON object.
    /// </summary>
    public async Task<JsonElement> CallAsync(
        string method,
        object? @params = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method must be a non-empty string.", nameof(method));

        var proc = _process;
        if (proc is null)
        {
            throw new InvalidOperationException("SidecarClient is not started. Call StartAsync first.");
        }
        if (proc.HasExited)
        {
            throw new SidecarProcessExitedException(proc.ExitCode, "Sidecar process has already exited.");
        }

        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, tcs))
        {
            // Extremely unlikely; requestId is a GUID.
            throw new InvalidOperationException("Failed to register pending request.");
        }

        try
        {
            await SendRequestLineAsync(proc, new SidecarRequest(requestId, method, @params), cancellationToken).ConfigureAwait(false);

            using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
            using var linked = timeoutCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new SidecarTimeoutException(method, requestId, timeout!.Value);
            }
        }
        finally
        {
            // If the TCS completed normally it won't be in the dictionary, but removing is safe.
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Sends a request and deserializes the <c>result</c> JSON object.
    /// </summary>
    public async Task<T> CallAsync<T>(
        string method,
        object? @params = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var elem = await CallAsync(method, @params, timeout, cancellationToken).ConfigureAwait(false);
        return elem.Deserialize<T>(_json) ?? throw new InvalidOperationException("Failed to deserialize sidecar result.");
    }

    private async Task SendRequestLineAsync(Process proc, SidecarRequest req, CancellationToken cancellationToken)
    {
        // Serialize request as a single JSON object line.
        var line = JsonSerializer.Serialize(req, _json);

        await _stdinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (proc.HasExited)
            {
                throw new SidecarProcessExitedException(proc.ExitCode, "Sidecar process exited before request could be written.");
            }
            await proc.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private async Task StdoutLoopAsync(Process proc, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    // Stdout closed. Treat as process exit / protocol termination.
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                }
                catch
                {
                    // Ignore malformed stdout lines; stdout should be protocol-only, but do not crash the host.
                    continue;
                }

                if (!TryGetStringProperty(root, "id", out var id) || string.IsNullOrEmpty(id))
                {
                    // Could be an event message; ignore for milestone 0.
                    continue;
                }

                if (!_pending.TryRemove(id, out var tcs))
                {
                    // Late response (e.g. caller timed out) or unknown id.
                    continue;
                }

                if (root.TryGetProperty("error", out var errElem) && errElem.ValueKind == JsonValueKind.Object)
                {
                    var code = errElem.TryGetProperty("code", out var codeElem) && codeElem.ValueKind == JsonValueKind.String
                        ? codeElem.GetString() ?? "unknown"
                        : "unknown";
                    var msg = errElem.TryGetProperty("message", out var msgElem) && msgElem.ValueKind == JsonValueKind.String
                        ? msgElem.GetString() ?? string.Empty
                        : string.Empty;

                    tcs.TrySetException(new SidecarRemoteException(code, msg));
                    continue;
                }

                if (root.TryGetProperty("result", out var resultElem))
                {
                    tcs.TrySetResult(resultElem.Clone());
                    continue;
                }

                tcs.TrySetException(new InvalidOperationException("Protocol error: response missing 'result' or 'error'."));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown/dispose.
        }
        catch (Exception ex)
        {
            FailAllPending(new SidecarProcessExitedException(SafeExitCode(proc), "Stdout reader failed.", ex));
        }

        // If the loop ended naturally, treat it as process termination.
        if (!cancellationToken.IsCancellationRequested)
        {
            FailAllPending(new SidecarProcessExitedException(SafeExitCode(proc), "Sidecar stdout closed."));
        }
    }

    private async Task StderrLoopAsync(Process proc, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                StderrLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown/dispose.
        }
        catch
        {
            // Ignore stderr reader failures; protocol is on stdout.
        }
    }

    private async Task ExitWatcherAsync(Process proc, CancellationToken cancellationToken)
    {
        try
        {
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        FailAllPending(new SidecarProcessExitedException(SafeExitCode(proc), "Sidecar process exited."));
    }

    private void FailAllPending(SidecarProcessExitedException ex)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    private static int? SafeExitCode(Process proc)
    {
        try
        {
            return proc.HasExited ? proc.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetStringProperty(JsonElement obj, string name, out string? value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return true;
        }

        value = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Process? proc;
        CancellationTokenSource? cts;
        Task? stdout;
        Task? stderr;
        Task? exit;

        lock (_gate)
        {
            proc = _process;
            cts = _lifetimeCts;
            stdout = _stdoutLoop;
            stderr = _stderrLoop;
            exit = _exitWatcher;

            CleanupProcess_NoLock();
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // Ignore.
        }

        // Try to stop gracefully (best-effort). Do not throw from Dispose.
        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.StandardInput.Close();
                }
            }
            catch
            {
                // Ignore.
            }

            try
            {
                if (_options.KillProcessOnDispose && !proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore.
            }
        }

        // Observe background tasks to avoid unobserved exceptions.
        try { if (stdout is not null) await stdout.ConfigureAwait(false); } catch { }
        try { if (stderr is not null) await stderr.ConfigureAwait(false); } catch { }
        try { if (exit is not null) await exit.ConfigureAwait(false); } catch { }

        try { proc?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private void CleanupProcess_NoLock()
    {
        _process = null;
        _lifetimeCts = null;
        _stdoutLoop = null;
        _stderrLoop = null;
        _exitWatcher = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SidecarClient));
    }

    private sealed record SidecarRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Params);
}
