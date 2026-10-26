using System.Globalization;
using Easydict.TranslationService;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;
using Easydict.TranslationService.Services.ModelCatalog;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>Creates an independently configured translation adapter for one AI profile.</summary>
internal static class AiProfileTranslationServiceFactory
{
    public static ITranslationService Create(
        AiServiceProfile profile,
        SettingsService settings,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!AiProviderCatalog.TryGet(profile.ProviderType, out var provider))
        {
            throw new InvalidOperationException($"Unknown AI provider type '{profile.ProviderType}'.");
        }

        return Create(profile, fieldId => settings.GetAiServiceProfileSecret(profile.ProfileId, fieldId), httpClient);
    }

    internal static ITranslationService Create(
        AiServiceProfile profile,
        Func<string, string?> getSecret,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(getSecret);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!AiProviderCatalog.TryGet(profile.ProviderType, out var provider))
        {
            throw new InvalidOperationException($"Unknown AI profile provider '{profile.ProviderType}'.");
        }

        var inner = CreateConfiguredProvider(provider, profile, getSecret, httpClient);
        if (provider.SupportsModelListing && inner is IModelCatalogProvider catalog)
        {
            return new ProfiledModelCatalogTranslationService(
                (IStreamTranslationService)inner,
                (IGrammarCorrectionService)inner,
                catalog,
                profile.ServiceId,
                profile.Name,
                profile.ProfileId,
                profile.ConfigurationRevision,
                profile.IsEnabled);
        }

        if (provider.SupportsCorrection)
        {
            return new ProfiledStreamingGrammarTranslationService(
                (IStreamTranslationService)inner,
                (IGrammarCorrectionService)inner,
                profile.ServiceId,
                profile.Name,
                profile.ProfileId,
                profile.ConfigurationRevision,
                profile.IsEnabled);
        }

        if (provider.SupportsStreaming)
        {
            return new ProfiledStreamingTranslationService(
                (IStreamTranslationService)inner,
                profile.ServiceId,
                profile.Name,
                profile.ProfileId,
                profile.ConfigurationRevision,
                profile.IsEnabled);
        }

        return new ProfiledTranslationService(
            inner,
            profile.ServiceId,
            profile.Name,
            profile.ProfileId,
            profile.ConfigurationRevision,
            profile.IsEnabled);
    }

    private static ITranslationService CreateConfiguredProvider(
        AiProviderDefinition provider,
        AiServiceProfile profile,
        Func<string, string?> getSecret,
        HttpClient httpClient)
    {
        string Value(string id)
        {
            var field = provider.Fields.FirstOrDefault(candidate => candidate.Id == id);
            return profile.Configuration.TryGetValue(id, out var value)
                ? value
                : field?.DefaultValue ?? string.Empty;
        }

        string? Secret(string id) => getSecret(id);

        switch (provider.Id)
        {
            case "openai":
            {
                var service = new OpenAIService(httpClient);
                service.Configure(
                    Secret("apiKey") ?? string.Empty,
                    Value("endpoint"),
                    Value("model"),
                    ParseDouble(Value("temperature"), 0.3),
                    ParseApiFormat(Value("apiFormat")));
                return service;
            }
            case "ollama":
            {
                var service = new OllamaService(httpClient);
                service.Configure(Value("endpoint"), Value("model"));
                service.ConfigurePrompt(
                    Easydict.TranslationService.Models.OllamaPromptStyleParser.Parse(Value("promptStyle")),
                    Value("customPrompt"));
                return service;
            }
            case "deepseek":
            {
                var service = new DeepSeekService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "groq":
            {
                var service = new GroqService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "zhipu":
            {
                var service = new ZhipuService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "kimi":
            {
                var service = new KimiService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "github":
            {
                var service = new GitHubModelsService(httpClient);
                service.Configure(Secret("token") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "custom-openai":
            {
                var service = new CustomOpenAIService(httpClient);
                service.Configure(Value("endpoint"), Secret("apiKey"), Value("model"));
                return service;
            }
            case "magpie":
            {
                var service = new MagpieService(httpClient);
                service.Configure(
                    Value("endpoint"),
                    Secret("apiKey"),
                    Value("model"),
                    string.Equals(Value("allowRemote"), "true", StringComparison.OrdinalIgnoreCase));
                return service;
            }
            case "openrouter":
            {
                var service = new OpenRouterService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "orcarouter":
            {
                var service = new OrcaRouterService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, model: Value("model"));
                return service;
            }
            case "gemini":
            {
                var service = new GeminiService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, Value("model"));
                return service;
            }
            case "doubao":
            {
                var service = new DoubaoService(httpClient);
                service.Configure(Secret("apiKey") ?? string.Empty, Value("endpoint"), Value("model"));
                return service;
            }
            case "claude-code":
            {
                var service = new ClaudeCodeService(httpClient);
                service.Configure(profile.IsEnabled, Value("model"), Value("executablePath"));
                return service;
            }
            case "codex":
            {
                var service = new CodexCliService(httpClient);
                service.Configure(profile.IsEnabled, Value("model"), Value("reasoningEffort"));
                return service;
            }
            default:
                throw new InvalidOperationException($"AI provider '{provider.Id}' has no runtime factory.");
        }
    }

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static OpenAIApiFormat ParseApiFormat(string value)
        => Enum.TryParse<OpenAIApiFormat>(value, ignoreCase: true, out var parsed)
            ? parsed
            : OpenAIApiFormat.Auto;
}
