using System.IO.Pipes;

namespace Easydict.WinUI.Services;

/// <summary>
/// Provides single-instance application behavior using a named pipe.
/// The first instance starts a pipe server; subsequent instances send commands and exit.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string PipeName = "Easydict_SingleInstance_Pipe";
    private const string MutexName = "Easydict_SingleInstance_Mutex";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _isDisposed;

    /// <summary>
    /// Fired on the first instance when a second instance sends text for translation.
    /// </summary>
    public event Action<string>? OnTranslateTextReceived;

    /// <summary>
    /// Attempts to acquire the single-instance lock.
    /// Returns true if this is the first instance, false otherwise.
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>
    /// Starts listening for commands from other instances on the named pipe.
    /// Call this only on the first (primary) instance.
    /// </summary>
    public void StartListening()
    {
        _cts = new CancellationTokenSource();
        _listenerTask = ListenAsync(_cts.Token);
    }

    /// <summary>
    /// Sends a translate command to the running primary instance.
    /// Call this from a secondary instance before exiting.
    /// </summary>
    public static async Task<bool> SendTranslateCommandAsync(string text)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(3000);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync($"TRANSLATE:{text}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SingleInstance] Failed to send command: {ex.Message}");
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct);

                if (line != null && line.StartsWith("TRANSLATE:", StringComparison.Ordinal))
                {
                    var text = line["TRANSLATE:".Length..];
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        OnTranslateTextReceived?.Invoke(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SingleInstance] Listener error: {ex.Message}");
                // Brief delay before retrying to avoid tight error loops
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        try { _listenerTask?.Wait(1000); } catch { }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
