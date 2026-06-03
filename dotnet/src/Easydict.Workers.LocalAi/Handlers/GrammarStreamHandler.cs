using System.Diagnostics;
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

        var p = TranslateRequestHelpers.ParseParams(parameters);
        var grammarRequest = new GrammarCorrectionRequest
        {
            Text = p.Text,
            Language = TranslateRequestHelpers.ParseLanguage(p.FromLanguage, Language.Auto),
            IncludeExplanations = p.IncludeExplanations ?? true,
        };

        // Currently Phi Silica + Foundry Local + OpenVINO each implement (or don't)
        // IGrammarCorrectionService. Try them in the order requested, fall back
        // through the chain in Auto mode.
        var orderedCandidates = TranslateRequestHelpers.ResolveCandidates(_state, p.ProviderMode).ToList();
        Trace.WriteLine(
            $"[LocalAiWorker] grammar_stream start. requestId={requestId}, providerMode={p.ProviderMode}, language={grammarRequest.Language}, textLength={p.Text?.Length ?? 0}, candidates={string.Join(">", orderedCandidates.Select(c => c.DisplayName))}");
        var aggregated = new StringBuilder();
        TranslationException? lastError = null;

        for (var i = 0; i < orderedCandidates.Count; i++)
        {
            var (svc, displayName) = orderedCandidates[i];
            if (svc is not IGrammarCorrectionService grammar)
            {
                Trace.WriteLine($"[LocalAiWorker] grammar_stream provider skipped. requestId={requestId}, provider={displayName}, reason=no grammar interface");
                continue;
            }

            aggregated.Clear();
            var emittedAny = false;
            var sw = Stopwatch.StartNew();
            try
            {
                Trace.WriteLine($"[LocalAiWorker] grammar_stream provider enter. requestId={requestId}, provider={displayName}, serviceId={svc.ServiceId}");
                await foreach (var chunk in grammar.CorrectGrammarStreamAsync(grammarRequest, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    aggregated.Append(chunk);
                    emittedAny = true;
                    await _writer.WriteEventAsync(LocalAiEvents.Chunk,
                        new ChunkEventData { Text = chunk }, requestId).ConfigureAwait(false);
                }

                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] grammar_stream provider success. requestId={requestId}, provider={displayName}, elapsedMs={sw.ElapsedMilliseconds}, resultLength={aggregated.Length}");
                return new TranslateStreamResult { Done = true, FullText = aggregated.ToString() };
            }
            catch (TranslationException tex) when (!emittedAny && CanFallback(p.ProviderMode, i, orderedCandidates.Count, tex))
            {
                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] grammar_stream provider fallback. requestId={requestId}, provider={displayName}, elapsedMs={sw.ElapsedMilliseconds}, errorCode={tex.ErrorCode}, serviceId={tex.ServiceId}, message={tex.Message}");
                lastError = tex;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] grammar_stream provider exception. requestId={requestId}, provider={displayName}, emittedAny={emittedAny}, elapsedMs={sw.ElapsedMilliseconds}, exception={ex.GetType().FullName}, message={ex.Message}");
                throw;
            }
        }

        if (lastError is not null)
        {
            Trace.WriteLine(
                $"[LocalAiWorker] grammar_stream failed after fallbacks. requestId={requestId}, errorCode={lastError.ErrorCode}, serviceId={lastError.ServiceId}, message={lastError.Message}");
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                lastError.Message,
                new { errorCode = lastError.ErrorCode.ToString(), serviceId = lastError.ServiceId });
        }

        Trace.WriteLine($"[LocalAiWorker] grammar_stream failed: no provider supports grammar. requestId={requestId}");
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
