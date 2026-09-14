using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jint.Native;

namespace Easydict.BobPlugin.Runtime.Bridges;

/// <summary>
/// Backs the plugin-facing <c>$http</c> object. The prelude normalizes the request (body already
/// serialized, headers resolved) so this bridge only has to send it, and hands back a flat JSON
/// envelope the prelude reshapes into Bob's response object.
/// Requests run on the thread pool; results are posted back onto the JS loop.
/// </summary>
internal sealed class HttpBridge
{
    /// <summary>Cap on a buffered response body, to bound memory for a hostile or broken endpoint.</summary>
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly Func<int, BobCallContext?> _resolveCall;
    private readonly Action<JsValue, string> _postCallback;
    private readonly IBobHostLogger _logger;

    public HttpBridge(
        HttpClient httpClient,
        Func<int, BobCallContext?> resolveCall,
        Action<JsValue, string> postCallback,
        IBobHostLogger logger)
    {
        _httpClient = httpClient;
        _resolveCall = resolveCall;
        _postCallback = postCallback;
        _logger = logger;
    }

    /// <summary>
    /// Start a request. Returns immediately; <paramref name="doneCallback"/> is invoked on the loop
    /// thread with the response envelope, and <paramref name="streamCallback"/> receives chunks
    /// when the plugin used <c>$http.streamRequest</c>.
    /// </summary>
    public void Send(double callId, string optionsJson, JsValue streamCallback, JsValue doneCallback)
    {
        BobHttpRequestDto? options;
        try
        {
            options = JsonSerializer.Deserialize<BobHttpRequestDto>(optionsJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            CompleteWithError(doneCallback, $"Invalid $http options: {ex.Message}");
            return;
        }

        if (options is null || string.IsNullOrWhiteSpace(options.Url))
        {
            CompleteWithError(doneCallback, "$http requires a url.");
            return;
        }

        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            CompleteWithError(doneCallback, $"$http only supports http(s) URLs: {options.Url}");
            return;
        }

        var context = _resolveCall((int)callId);
        var wantsStream = streamCallback is not null && !streamCallback.IsNull() && !streamCallback.IsUndefined();

        _ = Task.Run(() => SendCoreAsync(uri, options, context, wantsStream ? streamCallback : null, doneCallback));
    }

    private async Task SendCoreAsync(
        Uri uri,
        BobHttpRequestDto options,
        BobCallContext? context,
        JsValue? streamCallback,
        JsValue doneCallback)
    {
        // Linked so cancelling the plugin call (user cancel or timeout) aborts the request too.
        using var cts = context is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(context.LinkedCts.Token);

        if (options.TimeoutMs is > 0)
        {
            cts.CancelAfter(options.TimeoutMs.Value);
        }

        context?.RegisterHttp(cts);

        try
        {
            using var request = BuildRequest(uri, options);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var headers = CollectHeaders(response);
            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (streamCallback is not null)
            {
                await StreamResponseAsync(response, streamCallback, cts.Token).ConfigureAwait(false);
                PostEnvelope(doneCallback, new BobHttpResponseDto
                {
                    StatusCode = (int)response.StatusCode,
                    Headers = headers,
                    Url = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString(),
                    ContentType = contentType,
                    Text = string.Empty,
                    RawDataBase64 = string.Empty
                });
                return;
            }

            var bytes = await ReadBoundedAsync(response, cts.Token).ConfigureAwait(false);
            PostEnvelope(doneCallback, new BobHttpResponseDto
            {
                StatusCode = (int)response.StatusCode,
                Headers = headers,
                Url = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString(),
                ContentType = contentType,
                Text = SafeDecode(bytes),
                RawDataBase64 = Convert.ToBase64String(bytes)
            });
        }
        catch (OperationCanceledException)
        {
            // The plugin call itself reports cancellation; the callback just needs to stop waiting.
            PostEnvelope(doneCallback, new BobHttpResponseDto { Error = "The request was cancelled." });
        }
        catch (HttpRequestException ex)
        {
            _logger.Log("warn", $"$http request failed: {ex.Message}");
            PostEnvelope(doneCallback, new BobHttpResponseDto { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.Log("error", $"$http request error: {ex}");
            PostEnvelope(doneCallback, new BobHttpResponseDto { Error = ex.Message });
        }
        finally
        {
            context?.UnregisterHttp(cts);
        }
    }

    private static HttpRequestMessage BuildRequest(Uri uri, BobHttpRequestDto options)
    {
        var method = string.IsNullOrWhiteSpace(options.Method)
            ? HttpMethod.Get
            : new HttpMethod(options.Method.ToUpperInvariant());
        var request = new HttpRequestMessage(method, uri);

        string? contentType = null;
        foreach (var (name, value) in options.Header ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(name) || value is null)
            {
                continue;
            }

            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                // Content headers are attached below, once the body exists.
                contentType ??= null;
            }
        }

        if (options.BodyBase64 is not null)
        {
            request.Content = new ByteArrayContent(DataCodec.FromBase64(options.BodyBase64));
        }
        else if (options.Body is not null)
        {
            request.Content = new StringContent(options.Body, Encoding.UTF8);
        }

        if (request.Content is not null)
        {
            if (!string.IsNullOrWhiteSpace(contentType)
                && MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                request.Content.Headers.ContentType = parsed;
            }

            foreach (var (name, value) in options.Header ?? new Dictionary<string, string>())
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) || value is null)
                {
                    continue;
                }

                if (!request.Headers.Contains(name))
                {
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return request;
    }

    private async Task StreamResponseAsync(HttpResponseMessage response, JsValue streamCallback, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        var total = 0L;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > MaxResponseBytes)
            {
                throw new HttpRequestException($"Streamed response exceeded {MaxResponseBytes} bytes.");
            }

            var chunk = buffer.AsSpan(0, read).ToArray();
            var payload = JsonSerializer.Serialize(
                new BobHttpChunkDto
                {
                    Text = SafeDecode(chunk),
                    RawDataBase64 = Convert.ToBase64String(chunk)
                },
                JsonOptions);
            _postCallback(streamCallback, payload);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (memory.Length + read > MaxResponseBytes)
            {
                throw new HttpRequestException($"Response exceeded {MaxResponseBytes} bytes.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    /// <summary>Decode as UTF-8; binary payloads still reach the plugin through the base64 field.</summary>
    private static string SafeDecode(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        return headers;
    }

    private void CompleteWithError(JsValue doneCallback, string message)
    {
        _logger.Log("warn", message);
        PostEnvelope(doneCallback, new BobHttpResponseDto { Error = message });
    }

    private void PostEnvelope(JsValue doneCallback, BobHttpResponseDto envelope)
        => _postCallback(doneCallback, JsonSerializer.Serialize(envelope, JsonOptions));

    private sealed class BobHttpRequestDto
    {
        public string? Url { get; set; }
        public string? Method { get; set; }
        public Dictionary<string, string>? Header { get; set; }
        public string? Body { get; set; }
        public string? BodyBase64 { get; set; }
        public int? TimeoutMs { get; set; }
    }

    private sealed class BobHttpResponseDto
    {
        public int StatusCode { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? Url { get; set; }
        public string? ContentType { get; set; }
        public string? Text { get; set; }
        public string? RawDataBase64 { get; set; }
        public string? Error { get; set; }
    }

    private sealed class BobHttpChunkDto
    {
        public string? Text { get; set; }
        public string? RawDataBase64 { get; set; }
    }
}
