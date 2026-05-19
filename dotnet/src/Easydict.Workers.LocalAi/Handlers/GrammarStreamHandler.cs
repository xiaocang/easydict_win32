using System.Text;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

/// <summary>
/// Streaming grammar correction. Mirrors TranslateStreamHandler shape but uses
/// IGrammarCorrectionService.CorrectGrammarStreamAsync instead.
/// </summary>
internal sealed class GrammarStreamHandler
{
    private readonly WorkerState _state;
    private readonly IpcEventWriter _writer;

    public GrammarStreamHandler(WorkerState state, IpcEventWriter writer)
    {
        _state = state;
        _writer = writer;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = TranslateHandler.ParseParams(parameters);
        var grammarRequest = new GrammarCorrectionRequest
        {
            Text = p.Text,
            Language = Enum.TryParse<Language>(p.FromLanguage, out var lang) ? lang : Language.Auto,
        };

        // Currently Phi Silica + Foundry Local + OpenVINO each implement (or don't)
        // IGrammarCorrectionService. Try them in the order requested, fall back
        // through the chain in Auto mode.
        var orderedCandidates = new TranslateHandler(_state).ResolveCandidates(p.ProviderMode).ToList();
        var aggregated = new StringBuilder();
        TranslationException? lastError = null;

        for (var i = 0; i < orderedCandidates.Count; i++)
        {
            var svc = orderedCandidates[i].Svc;
            if (svc is not IGrammarCorrectionService grammar) continue;

            aggregated.Clear();
            var emittedAny = false;
            try
            {
                await foreach (var chunk in grammar.CorrectGrammarStreamAsync(grammarRequest, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    aggregated.Append(chunk);
                    emittedAny = true;
                    await _writer.WriteEventAsync(LocalAiEvents.Chunk,
                        new ChunkEventData { Text = chunk }, requestId).ConfigureAwait(false);
                }

                return new TranslateStreamResult { Done = true, FullText = aggregated.ToString() };
            }
            catch (TranslationException tex) when (!emittedAny && CanFallback(p.ProviderMode, i, orderedCandidates.Count, tex))
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
            "No local AI provider supports grammar correction for this language");
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
