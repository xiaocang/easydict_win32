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
    private readonly Func<string?> _lookupRequestId;

    public TranslateStreamHandler(WorkerState state, IpcEventWriter writer, Func<string?> lookupRequestId)
    {
        _state = state;
        _writer = writer;
        _lookupRequestId = lookupRequestId;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = TranslateHandler.ParseParams(parameters);
        var request = TranslateHandler.BuildRequest(p);

        var candidates = new TranslateHandler(_state).ResolveCandidates(p.ProviderMode).ToList();

        var aggregated = new StringBuilder();
        TranslationException? lastError = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var (svc, _) = candidates[i];
            var emittedAny = false;
            aggregated.Clear();
            try
            {
                await foreach (var chunk in svc.TranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    aggregated.Append(chunk);
                    emittedAny = true;
                    await _writer.WriteEventAsync(LocalAiEvents.Chunk,
                        new ChunkEventData { Text = chunk }, requestId).ConfigureAwait(false);
                }

                return new TranslateStreamResult
                {
                    Done = true,
                    FullText = aggregated.ToString(),
                };
            }
            catch (TranslationException tex) when (!emittedAny && CanFallback(p.ProviderMode, i, candidates.Count, tex))
            {
                lastError = tex;
            }
        }

        if (lastError is not null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                lastError.Message,
                new { errorCode = lastError.ErrorCode.ToString(), serviceId = lastError.ServiceId });
        }
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
