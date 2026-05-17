using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LocalApi;

/// <summary>
/// Local OpenAI-compatible HTTP server. Exposes a small surface (<c>/healthz</c>,
/// <c>/v1/models</c>, <c>/v1/chat/completions</c>) bound to <c>127.0.0.1</c>.
///
/// Lifecycle: <see cref="StartAsync"/> opens a single <see cref="HttpListener"/>, spins an
/// accept loop on the thread pool, and dispatches each request to a per-request task.
/// <see cref="ReconfigureAsync"/> tears down and restarts; <see cref="StopAsync"/> closes
/// the listener and awaits the loop. Safe to call from any thread; never touches a UI thread.
/// </summary>
public sealed class LocalApiServer : IDisposable
{
    public const string ModelIdPrefix = "easydict-";

    private readonly Func<TranslationManager> _managerProvider;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private LocalApiOptions _opts = LocalApiOptions.Disabled;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public string? CurrentBaseUrl { get; private set; }

    /// <summary>
    /// Construct with a provider so the server reads the current <see cref="TranslationManager"/>
    /// per-request — this matters because higher layers may swap the manager (e.g. on proxy change).
    /// </summary>
    public LocalApiServer(Func<TranslationManager> managerProvider)
    {
        _managerProvider = managerProvider ?? throw new ArgumentNullException(nameof(managerProvider));
    }

    public async Task StartAsync(LocalApiOptions opts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opts);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) throw new InvalidOperationException("Server is already running.");
            await StartCoreAsync(opts, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ReconfigureAsync(LocalApiOptions newOpts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newOpts);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync().ConfigureAwait(false);
            await StartCoreAsync(newOpts, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopCoreAsync().GetAwaiter().GetResult(); } catch { /* best-effort */ }
        _lifecycle.Dispose();
    }

    // ---------- internals ----------

    private async Task StartCoreAsync(LocalApiOptions opts, CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{opts.Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Caller surfaces this to the UI; keep state stopped.
            Debug.WriteLine($"[LocalApiServer] listener.Start failed: code={ex.ErrorCode} {ex.Message}");
            try { listener.Close(); } catch { }
            throw;
        }

        _listener = listener;
        _opts = opts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CurrentBaseUrl = $"http://127.0.0.1:{opts.Port}";
        IsRunning = true;

        var loopCt = _cts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, loopCt), loopCt);
        await Task.CompletedTask;
    }

    private async Task StopCoreAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }

        if (_acceptLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        _acceptLoop = null;
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        CurrentBaseUrl = null;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(ctx, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LocalApiServer] handler error: {ex}");
                    try { TryWriteError(ctx, HttpStatusCode.InternalServerError, "internal_error", "Internal server error."); }
                    catch { }
                }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // CORS preflight short-circuit (no auth required).
        if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = (int)HttpStatusCode.NoContent;
            HandleCors(req, res, preflight: true); // may downgrade to 403 if AllowList rejects
            return;
        }

        HandleCors(req, res, preflight: false);

        var path = req.Url?.AbsolutePath ?? "/";

        if (path == "/healthz" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(res, HttpStatusCode.OK,
                new HealthResponse(), LocalApiJsonContext.Default.HealthResponse, ct).ConfigureAwait(false);
            return;
        }

        if (!Authorize(req))
        {
            res.Headers["WWW-Authenticate"] = "Bearer realm=\"easydict\"";
            await WriteErrorAsync(res, HttpStatusCode.Unauthorized, "invalid_api_key", "Missing or invalid bearer token.", ct).ConfigureAwait(false);
            return;
        }

        if (path == "/v1/models" && req.HttpMethod == "GET")
        {
            await HandleModelsAsync(res, ct).ConfigureAwait(false);
            return;
        }
        if (path == "/v1/chat/completions" && req.HttpMethod == "POST")
        {
            await HandleChatCompletionsAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        await WriteErrorAsync(res, HttpStatusCode.NotFound, "not_found", $"Unknown route: {req.HttpMethod} {path}", ct).ConfigureAwait(false);
    }

    private async Task HandleModelsAsync(HttpListenerResponse res, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var data = new List<ModelInfo>();
        foreach (var svc in _managerProvider().Services.Values)
        {
            if (!svc.IsConfigured) continue;
            if (!_opts.ExposedServiceIds.Contains(svc.ServiceId)) continue;
            data.Add(new ModelInfo
            {
                Id = ModelIdPrefix + svc.ServiceId,
                Created = now,
                DisplayName = svc.DisplayName,
                SupportsStreaming = svc is IStreamTranslationService,
            });
        }
        await WriteJsonAsync(res, HttpStatusCode.OK,
            new ModelList { Data = data }, LocalApiJsonContext.Default.ModelList, ct).ConfigureAwait(false);
    }

    private async Task HandleChatCompletionsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        ChatRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(
                req.InputStream, LocalApiJsonContext.Default.ChatRequest, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(res, HttpStatusCode.BadRequest, "invalid_request_error", $"Invalid JSON body: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }
        if (body is null)
        {
            await WriteErrorAsync(res, HttpStatusCode.BadRequest, "invalid_request_error", "Empty body.", ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(body.Model))
        {
            await WriteErrorAsync(res, HttpStatusCode.BadRequest, "invalid_request_error", "Missing model.", ct).ConfigureAwait(false);
            return;
        }

        var serviceId = StripPrefix(body.Model);
        if (!_managerProvider().Services.TryGetValue(serviceId, out var service) ||
            !_opts.ExposedServiceIds.Contains(serviceId) ||
            !service.IsConfigured)
        {
            await WriteErrorAsync(res, HttpStatusCode.NotFound, "model_not_found", $"Model not available: {body.Model}", ct).ConfigureAwait(false);
            return;
        }

        var mapped = OpenAIMessageMapper.Map(body, _opts.DefaultTargetLanguage);
        if (mapped.Request is null)
        {
            await WriteErrorAsync(res, HttpStatusCode.BadRequest, "invalid_request_error", mapped.Error ?? "Invalid messages.", ct).ConfigureAwait(false);
            return;
        }

        var modelEcho = body.Model;
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (body.Stream)
        {
            res.StatusCode = (int)HttpStatusCode.OK;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers["Cache-Control"] = "no-cache, no-transform";
            res.Headers["X-Accel-Buffering"] = "no";
            res.SendChunked = true;
            res.KeepAlive = true;

            var output = res.OutputStream;
            try
            {
                await foreach (var chunk in _managerProvider().TranslateStreamAsync(mapped.Request, ct, serviceId).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    await SseWriter.WriteChunkAsync(output, BuildChunk(id, created, modelEcho, chunk, finishReason: null), ct).ConfigureAwait(false);
                }
                await SseWriter.WriteChunkAsync(output, BuildChunk(id, created, modelEcho, content: null, finishReason: "stop"), ct).ConfigureAwait(false);
                await SseWriter.WriteDoneAsync(output, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            catch (TranslationException ex)
            {
                // Best-effort: write an SSE error frame; cannot change status code after first flush.
                await TryWriteSseErrorAsync(output, ex.Message, ct).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                var result = await service.TranslateAsync(mapped.Request, ct).ConfigureAwait(false);
                var response = new ChatCompletionResponse
                {
                    Id = id,
                    Created = created,
                    Model = modelEcho,
                    Choices = new List<ChatChoice>
                    {
                        new() { Index = 0, Message = new ChatMessageOut { Content = result.TranslatedText }, FinishReason = "stop" },
                    },
                };
                await WriteJsonAsync(res, HttpStatusCode.OK,
                    response, LocalApiJsonContext.Default.ChatCompletionResponse, ct).ConfigureAwait(false);
            }
            catch (TranslationException ex)
            {
                await WriteErrorAsync(res, HttpStatusCode.BadGateway, "upstream_error", ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private static ChatCompletionChunk BuildChunk(string id, long created, string model, string? content, string? finishReason)
    {
        var delta = new ChatDelta();
        if (content is not null) delta.Content = content;
        return new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices = new List<ChatChoice>
            {
                new() { Index = 0, Delta = delta, FinishReason = finishReason },
            },
        };
    }

    // ---------- helpers ----------

    private static string StripPrefix(string model)
    {
        return model.StartsWith(ModelIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? model.Substring(ModelIdPrefix.Length)
            : model;
    }

    private bool Authorize(HttpListenerRequest req)
    {
        var header = req.Headers["Authorization"];
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var provided = header.AsSpan(scheme.Length).Trim().ToString();
        var stored = _opts.Token;
        if (string.IsNullOrEmpty(stored)) return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var storedBytes = Encoding.UTF8.GetBytes(stored);
        if (providedBytes.Length != storedBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(providedBytes, storedBytes);
    }

    private void HandleCors(HttpListenerRequest req, HttpListenerResponse res, bool preflight)
    {
        var origin = req.Headers["Origin"];
        string? allow = null;
        if (_opts.CorsMode == LocalApiCorsMode.Any)
        {
            allow = "*";
        }
        else if (!string.IsNullOrEmpty(origin) && _opts.AllowedOrigins.Contains(origin))
        {
            allow = origin;
        }

        if (allow != null)
        {
            res.Headers["Access-Control-Allow-Origin"] = allow;
            res.Headers["Vary"] = "Origin";
        }
        if (preflight)
        {
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
            res.Headers["Access-Control-Max-Age"] = "600";
            if (allow == null && _opts.CorsMode == LocalApiCorsMode.AllowList)
            {
                res.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse res,
        HttpStatusCode status,
        T body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        res.StatusCode = (int)status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse res,
        HttpStatusCode status,
        string type,
        string message,
        CancellationToken ct)
    {
        var env = new ErrorEnvelope { Error = new ErrorBody { Message = message, Type = type } };
        await WriteJsonAsync(res, status, env, LocalApiJsonContext.Default.ErrorEnvelope, ct).ConfigureAwait(false);
    }

    private static void TryWriteError(HttpListenerContext ctx, HttpStatusCode status, string type, string message)
    {
        try
        {
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var env = new ErrorEnvelope { Error = new ErrorBody { Message = message, Type = type } };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(env, LocalApiJsonContext.Default.ErrorEnvelope);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }

    private static async Task TryWriteSseErrorAsync(Stream output, string message, CancellationToken ct)
    {
        try
        {
            var payload = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(message)},\"type\":\"upstream_error\"}}}}\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalApiServer));
    }
}
