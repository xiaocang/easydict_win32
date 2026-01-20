using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;

namespace Easydict.SidecarClient;

/// <summary>
/// Client for communicating with the Easydict sidecar process via JSON Lines over stdio.
/// Supports concurrent requests with id-based multiplexing, timeouts, and cancellation.
/// </summary>
public sealed class SidecarClient : IDisposable, IAsyncDisposable
{
    private readonly SidecarClientOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pendingRequests = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutReaderTask;
    private Task? _stderrReaderTask;
    private CancellationTokenSource? _cts;
    private int _requestIdCounter;
    private bool _disposed;

    /// <summary>
    /// Event raised when a log message is received from stderr.
    /// </summary>
    public event Action<string>? OnStderrLog;

    /// <summary>
    /// Event raised when an IPC event is received from the sidecar.
    /// </summary>
    public event Action<IpcEvent>? OnEvent;

    /// <summary>
    /// Event raised when the sidecar process exits.
    /// </summary>
    public event Action<int?>? OnProcessExited;

    /// <summary>
    /// Returns true if the sidecar process is currently running.
    /// </summary>
    public bool IsRunning => _process is not null && !_process.HasExited;

    public SidecarClient(SidecarClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Start the sidecar process.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SidecarClient));
            if (IsRunning) return;

            _cts = new CancellationTokenSource();

            var psi = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (_options.Arguments is not null)
            {
                foreach (var arg in _options.Arguments)
                    psi.ArgumentList.Add(arg);
            }

            if (_options.WorkingDirectory is not null)
                psi.WorkingDirectory = _options.WorkingDirectory;

            if (_options.EnvironmentVariables is not null)
            {
                foreach (var (key, value) in _options.EnvironmentVariables)
                    psi.Environment[key] = value;
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExitedHandler;
            _process.Start();

            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;

            _stdoutReaderTask = Task.Run(() => ReadStdoutLoop(_cts.Token));
            _stderrReaderTask = Task.Run(() => ReadStderrLoop(_cts.Token));
        }
    }

    /// <summary>
    /// Generate a unique request ID.
    /// </summary>
    private string GenerateRequestId()
    {
        return $"req-{Interlocked.Increment(ref _requestIdCounter)}";
    }

    /// <summary>
    /// Send a request and wait for the response.
    /// </summary>
    public async Task<IpcResponse> SendRequestAsync(
        string method,
        object? parameters = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SidecarClient));
        if (!IsRunning) throw new SidecarNotRunningException();

        var requestId = GenerateRequestId();
        var request = new IpcRequest
        {
            Id = requestId,
            Method = method,
            Params = parameters
        };

        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            var json = JsonLineSerializer.SerializeLine(request);

            // Serialize writes to stdin to prevent concurrent stream access
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _stdin!.WriteAsync(json);
            }
            finally
            {
                _writeLock.Release();
            }

            var timeout = timeoutMs ?? _options.DefaultTimeoutMs;
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
                {
                    return await tcs.Task;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new SidecarTimeoutException(requestId);
            }
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Send a request and return the result, throwing on error.
    /// </summary>
    public async Task<T?> SendRequestAsync<T>(
        string method,
        object? parameters = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(method, parameters, timeoutMs, cancellationToken);

        if (response.IsError)
            throw new SidecarErrorException(response.Error!);

        if (!response.Result.HasValue)
            return default;

        return response.Result.Value.Deserialize<T>();
    }

    /// <summary>
    /// Read stdout loop - parses JSON Lines and dispatches responses/events.
    /// </summary>
    private async Task ReadStdoutLoop(CancellationToken ct)
    {
        var reader = _process!.StandardOutput;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break; // EOF

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var message = JsonLineSerializer.Deserialize<IpcMessage>(line);
                    if (message is null) continue;

                    if (message.IsEvent)
                    {
                        OnEvent?.Invoke(message.ToEvent());
                    }
                    else if (message.IsResponse && message.Id is not null)
                    {
                        if (_pendingRequests.TryRemove(message.Id, out var tcs))
                        {
                            tcs.TrySetResult(message.ToResponse());
                        }
                    }
                }
                catch (JsonException)
                {
                    // Malformed JSON from sidecar - log and continue
                    OnStderrLog?.Invoke($"[SidecarClient] Malformed JSON from stdout: {line}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnStderrLog?.Invoke($"[SidecarClient] stdout reader error: {ex.Message}");
        }
    }

    /// <summary>
    /// Read stderr loop - captures log messages.
    /// </summary>
    private async Task ReadStderrLoop(CancellationToken ct)
    {
        var reader = _process!.StandardError;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break; // EOF

                if (!string.IsNullOrWhiteSpace(line))
                {
                    OnStderrLog?.Invoke(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnStderrLog?.Invoke($"[SidecarClient] stderr reader error: {ex.Message}");
        }
    }

    private void OnProcessExitedHandler(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode;
        OnProcessExited?.Invoke(exitCode);

        // Fail all pending requests
        foreach (var (id, tcs) in _pendingRequests)
        {
            tcs.TrySetException(new SidecarProcessExitedException(exitCode));
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Stop the sidecar process gracefully by sending shutdown command.
    /// </summary>
    public async Task StopAsync(int gracefulTimeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;

        try
        {
            // Try graceful shutdown
            await SendRequestAsync("shutdown", timeoutMs: gracefulTimeoutMs, cancellationToken: cancellationToken);
        }
        catch
        {
            // Ignore errors during shutdown
        }

        // Wait for process to exit
        try
        {
            using var cts = new CancellationTokenSource(gracefulTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            await _process!.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Force kill if graceful shutdown times out
            try { _process?.Kill(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        try { _process?.Kill(); } catch { }
        _process?.Dispose();
        _stdin?.Dispose();

        _cts?.Dispose();
        _writeLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await StopAsync(gracefulTimeoutMs: 2000);
        }
        catch { }

        Dispose();
    }
}

