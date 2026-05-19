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
        TranslationException? lastError = null;
        foreach (var (svc, displayName) in candidates)
        {
            try
            {
                var result = await svc.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
                sw.Stop();
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
                lastError = tex;
            }
        }

        if (lastError is not null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                lastError.Message,
                new { errorCode = lastError.ErrorCode.ToString(), serviceId = lastError.ServiceId });
        }
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
