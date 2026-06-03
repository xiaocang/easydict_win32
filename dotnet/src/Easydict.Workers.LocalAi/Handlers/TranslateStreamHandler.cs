using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

/// <summary>
/// Streaming translate. Emits one "chunk" event per yielded fragment from
/// IStreamTranslationService.TranslateStreamAsync; the final response carries
/// the aggregated full text so the host can verify completeness.
/// </summary>
internal sealed class TranslateStreamHandler
{
    private readonly WorkerState _state;
    private readonly IpcEventWriter _writer;

    public TranslateStreamHandler(WorkerState state, IpcEventWriter writer)
    {
        _state = state;
        _writer = writer;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = TranslateRequestHelpers.ParseParams(parameters);
        var request = TranslateRequestHelpers.BuildRequest(p);

        var candidates = TranslateRequestHelpers.ResolveCandidates(_state, p.ProviderMode).ToList();
        Trace.WriteLine(
            $"[LocalAiWorker] translate_stream start. requestId={requestId}, providerMode={p.ProviderMode}, from={request.FromLanguage}, to={request.ToLanguage}, textLength={p.Text?.Length ?? 0}, candidates={string.Join(">", candidates.Select(c => c.DisplayName))}");

        var aggregated = new StringBuilder();
        TranslationException? lastError = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var (svc, displayName) = candidates[i];
            var emittedAny = false;
            aggregated.Clear();
            var sw = Stopwatch.StartNew();
            try
            {
                Trace.WriteLine($"[LocalAiWorker] translate_stream provider enter. requestId={requestId}, provider={displayName}, serviceId={svc.ServiceId}");
                await foreach (var chunk in svc.TranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    aggregated.Append(chunk);
                    emittedAny = true;
                    await _writer.WriteEventAsync(LocalAiEvents.Chunk,
                        new ChunkEventData { Text = chunk }, requestId).ConfigureAwait(false);
                }

                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] translate_stream provider success. requestId={requestId}, provider={displayName}, elapsedMs={sw.ElapsedMilliseconds}, resultLength={aggregated.Length}");
                return new TranslateStreamResult
                {
                    Done = true,
                    FullText = aggregated.ToString(),
                };
            }
            catch (TranslationException tex) when (!emittedAny && CanFallback(p.ProviderMode, i, candidates.Count, tex))
            {
                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] translate_stream provider fallback. requestId={requestId}, provider={displayName}, elapsedMs={sw.ElapsedMilliseconds}, errorCode={tex.ErrorCode}, serviceId={tex.ServiceId}, message={tex.Message}");
                lastError = tex;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] translate_stream provider exception. requestId={requestId}, provider={displayName}, emittedAny={emittedAny}, elapsedMs={sw.ElapsedMilliseconds}, exception={ex.GetType().FullName}, message={ex.Message}");
                throw;
            }
        }

        if (lastError is not null)
        {
            Trace.WriteLine(
                $"[LocalAiWorker] translate_stream failed after fallbacks. requestId={requestId}, errorCode={lastError.ErrorCode}, serviceId={lastError.ServiceId}, message={lastError.Message}");
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                lastError.Message,
                new { errorCode = lastError.ErrorCode.ToString(), serviceId = lastError.ServiceId });
        }
        Trace.WriteLine($"[LocalAiWorker] translate_stream failed: no provider produced response. requestId={requestId}");
        throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
            "No local AI provider produced a streaming response");
    }

    private static bool CanFallback(string mode, int index, int total, TranslationException ex)
    {
        if (mode != LocalAiProviderModes.Auto) return false;
        if (index >= total - 1) return false;
        return ex.ErrorCode is TranslationErrorCode.ServiceUnavailable
            or TranslationErrorCode.NetworkError
            or TranslationErrorCode.Timeout
            or TranslationErrorCode.InvalidModel
            or TranslationErrorCode.Unknown;
    }
}
