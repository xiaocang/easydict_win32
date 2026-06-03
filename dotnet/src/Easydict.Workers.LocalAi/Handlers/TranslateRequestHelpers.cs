using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

/// <summary>
/// Shared request parsing and provider resolution for local AI streaming handlers.
/// </summary>
internal static class TranslateRequestHelpers
{
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
            FromLanguage = ParseLanguage(p.FromLanguage, Language.Auto),
            ToLanguage = ParseLanguage(p.ToLanguage, Language.English),
            CustomPrompt = p.CustomPrompt,
        };
    }

    internal static Language ParseLanguage(string? value, Language defaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultLanguage;
        }

        if (Enum.TryParse<Language>(value, ignoreCase: true, out var language))
        {
            return language;
        }

        if (string.Equals(value, "ChineseSimplified", StringComparison.OrdinalIgnoreCase))
        {
            return Language.SimplifiedChinese;
        }

        if (string.Equals(value, "ChineseTraditional", StringComparison.OrdinalIgnoreCase))
        {
            return Language.TraditionalChinese;
        }

        return LanguageCodes.FromIso639(value);
    }

    internal static IEnumerable<(IStreamTranslationService Svc, string DisplayName)> ResolveCandidates(
        WorkerState state,
        string providerMode)
    {
        return providerMode switch
        {
            LocalAiProviderModes.WindowsAI => new[] { (Cast(state.GetPhiSilica()), "Phi Silica") },
            LocalAiProviderModes.FoundryLocal => new[] { (Cast(state.GetFoundryLocal()), "Foundry Local") },
            LocalAiProviderModes.OpenVINO => new[] { (Cast(state.GetOpenVino()), "OpenVINO") },
            _ => new[]
            {
                (Cast(state.GetPhiSilica()), "Phi Silica"),
                (Cast(state.GetFoundryLocal()), "Foundry Local"),
                (Cast(state.GetOpenVino()), "OpenVINO"),
            }
        };

        static IStreamTranslationService Cast(IStreamTranslationService svc) => svc;
    }
}
