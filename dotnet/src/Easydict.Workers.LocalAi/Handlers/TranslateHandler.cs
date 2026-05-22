using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

/// <summary>
/// Non-streaming local AI translate. Routes to PhiSilica / Foundry Local / OpenVINO
/// based on providerMode. For Auto, tries PhiSilica → Foundry → OpenVINO with
/// fallback on TranslationException codes the host would fall back on.
/// </summary>
internal sealed class TranslateHandler
{
    private readonly WorkerState _state;

    public TranslateHandler(WorkerState state)
    {
        _state = state;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = ParseParams(parameters);
        var request = BuildRequest(p);
        var sw = Stopwatch.StartNew();

        var candidates = ResolveCandidates(p.ProviderMode);
        Trace.WriteLine(
            $"[LocalAiWorker] translate start. requestId={requestId}, providerMode={p.ProviderMode}, from={request.FromLanguage}, to={request.ToLanguage}, textLength={p.Text?.Length ?? 0}, candidates={string.Join(">", candidates.Select(c => c.DisplayName))}");
        TranslationException? lastError = null;
        foreach (var (svc, displayName) in candidates)
        {
            try
            {
                Trace.WriteLine($"[LocalAiWorker] translate provider enter. requestId={requestId}, provider={displayName}, serviceId={svc.ServiceId}");
                var result = await svc.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                Trace.WriteLine(
                    $"[LocalAiWorker] translate provider success. requestId={requestId}, provider={displayName}, elapsedMs={sw.ElapsedMilliseconds}, resultLength={result.TranslatedText?.Length ?? 0}");
                return new LocalAiTranslateResult
                {
                    TranslatedText = result.TranslatedText,
                    ServiceId = svc.ServiceId,
                    ServiceName = displayName,
                    DetectedLanguage = result.DetectedLanguage.ToString(),
                    TimingMs = sw.ElapsedMilliseconds,
                };
            }
            catch (TranslationException tex) when (CanFallback(p.ProviderMode, tex))
            {
                Trace.WriteLine(
                    $"[LocalAiWorker] translate provider fallback. requestId={requestId}, provider={displayName}, errorCode={tex.ErrorCode}, serviceId={tex.ServiceId}, message={tex.Message}");
                lastError = tex;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[LocalAiWorker] translate provider exception. requestId={requestId}, provider={displayName}, exception={ex.GetType().FullName}, message={ex.Message}");
                throw;
            }
        }

        if (lastError is not null)
        {
            Trace.WriteLine(
                $"[LocalAiWorker] translate failed after fallbacks. requestId={requestId}, errorCode={lastError.ErrorCode}, serviceId={lastError.ServiceId}, message={lastError.Message}");
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                lastError.Message,
                new { errorCode = lastError.ErrorCode.ToString(), serviceId = lastError.ServiceId });
        }
        Trace.WriteLine($"[LocalAiWorker] translate failed: no provider available. requestId={requestId}");
        throw new WorkerHandlerException(WorkerErrorCodes.ServiceError, "No local AI provider available");
    }

    internal static LocalAiTranslateParams ParseParams(JsonElement? parameters)
    {
        if (parameters is null)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "translate requires params");
        try
        {
            return parameters.Value.Deserialize<LocalAiTranslateParams>(
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "translate params was null");
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"translate params deserialization failed: {ex.Message}");
        }
    }

    internal static TranslationRequest BuildRequest(LocalAiTranslateParams p)
    {
        return new TranslationRequest
        {
            Text = p.Text,
            FromLanguage = Enum.TryParse<Language>(p.FromLanguage, out var from) ? from : Language.Auto,
            ToLanguage = Enum.TryParse<Language>(p.ToLanguage, out var to) ? to : Language.English,
        };
    }

    internal IEnumerable<(IStreamTranslationService Svc, string DisplayName)> ResolveCandidates(string providerMode)
    {
        return providerMode switch
        {
            LocalAiProviderModes.WindowsAI => new[] { (Cast(_state.GetPhiSilica()), "Phi Silica") },
            LocalAiProviderModes.FoundryLocal => new[] { (Cast(_state.GetFoundryLocal()), "Foundry Local") },
            LocalAiProviderModes.OpenVINO => new[] { (Cast(_state.GetOpenVino()), "OpenVINO") },
            _ => new[]
            {
                (Cast(_state.GetPhiSilica()), "Phi Silica"),
                (Cast(_state.GetFoundryLocal()), "Foundry Local"),
                (Cast(_state.GetOpenVino()), "OpenVINO"),
            }
        };

        static IStreamTranslationService Cast(IStreamTranslationService svc) => svc;
    }

    private static bool CanFallback(string mode, TranslationException ex)
    {
        if (mode != LocalAiProviderModes.Auto) return false;
        return ex.ErrorCode is TranslationErrorCode.ServiceUnavailable
            or TranslationErrorCode.NetworkError
            or TranslationErrorCode.Timeout
            or TranslationErrorCode.InvalidModel
            or TranslationErrorCode.Unknown;
    }
}
