using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Easydict.TranslationService.Services.Auth;

/// <summary>
/// Outcome of one sign-in callback. Either <see cref="Code"/> is set (success) or
/// <see cref="Error"/> is set (the provider redirected back with an error).
/// </summary>
public sealed record LoopbackCallbackResult(string? Code, string? Error, string? ErrorDescription)
{
    public bool IsSuccess => !string.IsNullOrEmpty(Code) && string.IsNullOrEmpty(Error);
}

/// <summary>
/// Minimal HTTP/1.1 server bound to <c>127.0.0.1</c> on an OS-assigned port that waits for the
/// PKCE redirect (<c>GET /cb?code=…&amp;state=…</c>).
/// <para>
/// Security model: a callback is accepted only when its <c>state</c> equals the value generated
/// for this attempt. Anything else (wrong or missing state, other paths, other methods) gets an
/// error page and the listener keeps waiting, so a stray request cannot abort or hijack the
/// user's real sign-in. Query values are never echoed into the HTML.
/// </para>
/// <para>
/// Browsers open speculative "preconnect" sockets that never send a request, so every accepted
/// connection is handled on its own task with a read timeout and never blocks the accept loop.
/// </para>
/// </summary>
public sealed class LoopbackCallbackListener : IDisposable
{
    public const string CallbackPath = "/cb";

    private const int MaxRequestHeadBytes = 16 * 1024;
    private static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(10);

    private const string SuccessHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Easydict</title></head>" +
        "<body style=\"font-family:sans-serif;text-align:center;padding:3em\">" +
        "<h2>Sign-in complete</h2><p>You can close this tab and return to Easydict.</p></body></html>";

    private const string DeniedHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Easydict</title></head>" +
        "<body style=\"font-family:sans-serif;text-align:center;padding:3em\">" +
        "<h2>Sign-in was not completed</h2><p>You can close this tab and try again from Easydict.</p></body></html>";

    private const string StateMismatchHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Easydict</title></head>" +
        "<body style=\"font-family:sans-serif;text-align:center;padding:3em\">" +
        "<h2>Invalid sign-in response</h2><p>This response does not belong to the current sign-in attempt (state mismatch). " +
        "Return to Easydict and try again.</p></body></html>";

    private const string BadRequestHtml = "<!doctype html><html><body><h2>Bad request</h2></body></html>";
    private const string NotFoundHtml = "<!doctype html><html><body><h2>Not found</h2></body></html>";
    private const string MethodNotAllowedHtml = "<!doctype html><html><body><h2>Method not allowed</h2></body></html>";

    private readonly TcpListener _listener;
    private bool _disposed;

    /// <summary>Bind to 127.0.0.1 on a free port and start listening.</summary>
    /// <exception cref="SocketException">Loopback bind failed.</exception>
    public LoopbackCallbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>OS-assigned port the listener is bound to.</summary>
    public int Port { get; }

    /// <summary>Callback URL to register with the provider: <c>http://127.0.0.1:{Port}/cb</c>.</summary>
    public string CallbackUrl => $"http://127.0.0.1:{Port}{CallbackPath}";

    /// <summary>
    /// Wait until a callback whose <c>state</c> equals <paramref name="expectedState"/> arrives.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired (timeout or user cancel).</exception>
    public async Task<LoopbackCallbackResult> WaitForCallbackAsync(
        string expectedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedState);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<LoopbackCallbackResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<LoopbackCallbackResult>)state!).TrySetCanceled(),
            completion);

        var acceptLoop = AcceptLoopAsync(expectedState, completion, cancellationToken);

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            // Unblocks the pending accept so the loop exits and the port is released.
            StopListener();
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OrcaRouterAuth] accept loop ended with: {ex.Message}");
            }
        }
    }

    private async Task AcceptLoopAsync(
        string expectedState,
        TaskCompletionSource<LoopbackCallbackResult> completion,
        CancellationToken cancellationToken)
    {
        while (!completion.Task.IsCompleted)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // Listener stopped (Dispose / StopListener) or transient accept failure.
                break;
            }

            _ = HandleConnectionAsync(client, expectedState, completion, cancellationToken);
        }
    }

    private static async Task HandleConnectionAsync(
        TcpClient client,
        string expectedState,
        TaskCompletionSource<LoopbackCallbackResult> completion,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectionReadTimeout);

                using var stream = client.GetStream();
                var head = await ReadRequestHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (head is null)
                {
                    // Idle preconnect / oversized request: just close.
                    return;
                }

                var firstLineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
                var requestLine = firstLineEnd >= 0 ? head[..firstLineEnd] : head;

                if (!TryParseRequestLine(requestLine, out var method, out var path, out var query))
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", BadRequestHtml, timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(method, "GET", StringComparison.Ordinal) &&
                    !string.Equals(method, "HEAD", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", MethodNotAllowedHtml, timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(path, CallbackPath, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", NotFoundHtml, timeout.Token).ConfigureAwait(false);
                    return;
                }

                var parameters = ParseQuery(query);

                if (!parameters.TryGetValue("state", out var state) ||
                    !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    Debug.WriteLine("[OrcaRouterAuth] Rejected callback: state mismatch");
                    await WriteResponseAsync(stream, 400, "Bad Request", StateMismatchHtml, timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (parameters.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
                {
                    parameters.TryGetValue("error_description", out var description);
                    await WriteResponseAsync(stream, 200, "OK", DeniedHtml, timeout.Token).ConfigureAwait(false);
                    completion.TrySetResult(new LoopbackCallbackResult(null, error, description));
                    return;
                }

                if (parameters.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code))
                {
                    await WriteResponseAsync(stream, 200, "OK", SuccessHtml, timeout.Token).ConfigureAwait(false);
                    completion.TrySetResult(new LoopbackCallbackResult(code, null, null));
                    return;
                }

                await WriteResponseAsync(stream, 400, "Bad Request", BadRequestHtml, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A misbehaving client must never fault the wait; the next connection may be the real one.
                Debug.WriteLine($"[OrcaRouterAuth] Connection handling failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Read the request head (through the blank line). Returns null when the peer closes
    /// without sending a complete head, or the head exceeds <see cref="MaxRequestHeadBytes"/>.
    /// </summary>
    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;

            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd >= 0)
            {
                return text[..headEnd];
            }
        }

        return null;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string html,
        CancellationToken cancellationToken)
    {
        var bytes = BuildResponse(statusCode, reasonPhrase, html);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Build a complete <c>Connection: close</c> HTTP/1.1 response.</summary>
    internal static byte[] BuildResponse(int statusCode, string reasonPhrase, string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reasonPhrase).Append("\r\n")
            .Append("Content-Type: text/html; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n")
            .ToString();
        var headerBytes = Encoding.ASCII.GetBytes(header);

        var response = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, response, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, response, headerBytes.Length, body.Length);
        return response;
    }

    /// <summary>Parse <c>METHOD /path?query HTTP/1.x</c>.</summary>
    internal static bool TryParseRequestLine(string requestLine, out string method, out string path, out string query)
    {
        method = string.Empty;
        path = string.Empty;
        query = string.Empty;

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return false;
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return false;
        }

        method = parts[0];
        var target = parts[1];

        // Tolerate absolute-form targets (http://127.0.0.1:port/cb?...).
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            target = absolute.PathAndQuery;
        }

        if (!target.StartsWith('/'))
        {
            return false;
        }

        var queryStart = target.IndexOf('?');
        if (queryStart >= 0)
        {
            path = target[..queryStart];
            query = target[(queryStart + 1)..];
        }
        else
        {
            path = target;
        }

        var fragment = query.IndexOf('#');
        if (fragment >= 0)
        {
            query = query[..fragment];
        }

        return true;
    }

    /// <summary>
    /// Parse an application/x-www-form-urlencoded query string. <c>+</c> decodes to a space,
    /// percent-escapes are decoded, and the first occurrence of a duplicate key wins.
    /// </summary>
    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var rawKey = eq >= 0 ? pair[..eq] : pair;
            var rawValue = eq >= 0 ? pair[(eq + 1)..] : string.Empty;

            var key = Decode(rawKey);
            if (key.Length == 0 || result.ContainsKey(key))
            {
                continue;
            }

            result[key] = Decode(rawValue);
        }

        return result;

        static string Decode(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return value;
            }
        }
    }

    private void StopListener()
    {
        try
        {
            _listener.Stop();
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"[OrcaRouterAuth] Listener stop failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListener();
    }
}
