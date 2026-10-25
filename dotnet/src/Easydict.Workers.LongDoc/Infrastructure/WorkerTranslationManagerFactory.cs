using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;

namespace Easydict.Workers.LongDoc.Infrastructure;

/// <summary>
/// Builds a TranslationManager inside the worker process configured from the
/// SettingsSnapshot received via the "configure" request. The worker does NOT
/// share state with the host's TranslationManagerService — that singleton lives
/// in WinUI and reaches for SettingsService.Instance.
///
/// API keys arrive in the snapshot already decrypted (host owns DPAPI/AES). The
/// snapshot lives only in the worker's memory and is reclaimed when the worker
/// exits after translate completion.
/// </summary>
internal static class WorkerTranslationManagerFactory
{
    // A non-null profile snapshot is authoritative for all standalone AI services.
    // Keep this list aligned with the profile provider catalog. Local AI is a separate
    // worker engine and ordinary translation services must remain registered.
    private static readonly string[] ProfileManagedAiServiceIds =
    [
        "openai", "ollama", "deepseek", "groq", "zhipu", "kimi", "github",
        "custom-openai", "openrouter", "orcarouter", "gemini", "doubao",
        "claude-code", "codex"
    ];

    /// <summary>
    /// Build and configure a TranslationManager from the snapshot. Caller owns
    /// disposal — TranslationManager implements IDisposable.
    /// </summary>
    public static TranslationManager Build(SettingsSnapshot snapshot)
    {
        var options = new TranslationManagerOptions
        {
            ProxyEnabled = snapshot.ProxyEnabled ?? false,
            ProxyUri = snapshot.ProxyUri,
            ProxyBypassLocal = snapshot.ProxyBypassLocal ?? false,
            ProxyBypassChina = snapshot.ProxyBypassChina ?? false,
        };

        var manager = new TranslationManager(options);
        manager.RegisterService(new WorkerLocalAiTranslationService(snapshot));

        // Configure each cloud LLM service from the snapshot. Workers register only
        // the services they may need to call — we keep the full set so any serviceId
        // the host requests in translate_document resolves.
        ConfigureIfPresent(manager, "openai", svc =>
        {
            if (svc is OpenAIService openai)
            {
                openai.Configure(
                    snapshot.OpenAIApiKey ?? string.Empty,
                    snapshot.OpenAIEndpoint,
                    snapshot.OpenAIModel,
                    snapshot.OpenAITemperature.HasValue ? (double?)snapshot.OpenAITemperature.Value : null,
                    ParseOpenAIApiFormat(snapshot.OpenAIApiFormatOverride));
            }
        });

        ConfigureIfPresent(manager, "deepl", svc =>
        {
            if (svc is DeepLService deepl)
            {
                deepl.Configure(
                    snapshot.DeepLApiKey,
                    useWebFirst: snapshot.DeepLUseFreeApi ?? false,
                    useQualityOptimized: snapshot.DeepLUseQualityOptimized ?? false);
            }
        });

        ConfigureIfPresent(manager, "gemini", svc =>
        {
            if (svc is GeminiService gemini && snapshot.GeminiApiKey is not null)
            {
                gemini.Configure(snapshot.GeminiApiKey, snapshot.GeminiModel);
            }
        });

        ConfigureIfPresent(manager, "deepseek", svc =>
        {
            if (svc is DeepSeekService ds && snapshot.DeepSeekApiKey is not null)
            {
                ds.Configure(snapshot.DeepSeekApiKey, model: snapshot.DeepSeekModel);
            }
        });

        ConfigureIfPresent(manager, "groq", svc =>
        {
            if (svc is GroqService gs && snapshot.GroqApiKey is not null)
            {
                gs.Configure(snapshot.GroqApiKey, snapshot.GroqModel);
            }
        });

        ConfigureIfPresent(manager, "zhipu", svc =>
        {
            if (svc is ZhipuService zs && snapshot.ZhipuApiKey is not null)
            {
                zs.Configure(snapshot.ZhipuApiKey, snapshot.ZhipuModel);
            }
        });

        ConfigureIfPresent(manager, "kimi", svc =>
        {
            if (svc is KimiService ks && snapshot.KimiApiKey is not null)
            {
                ks.Configure(snapshot.KimiApiKey, model: snapshot.KimiModel);
            }
        });

        ConfigureIfPresent(manager, "ollama", svc =>
        {
            if (svc is OllamaService ollama)
            {
                ollama.Configure(snapshot.OllamaEndpoint, snapshot.OllamaModel);
                ollama.ConfigurePrompt(
                    Easydict.TranslationService.Models.OllamaPromptStyleParser.Parse(snapshot.OllamaPromptStyle),
                    snapshot.OllamaCustomPrompt);
            }
        });

        ConfigureIfPresent(manager, "custom-openai", svc =>
        {
            if (svc is CustomOpenAIService custom && snapshot.CustomOpenAIApiKey is not null)
            {
                custom.Configure(
                    snapshot.CustomOpenAIApiKey,
                    snapshot.CustomOpenAIEndpoint,
                    snapshot.CustomOpenAIModel);
            }
        });

        ConfigureIfPresent(manager, "openrouter", svc =>
        {
            if (svc is OpenRouterService openRouter && snapshot.OpenRouterApiKey is not null)
            {
                openRouter.Configure(snapshot.OpenRouterApiKey, model: snapshot.OpenRouterModel);
            }
        });

        ConfigureIfPresent(manager, "orcarouter", svc =>
        {
            if (svc is OrcaRouterService orcaRouter && snapshot.OrcaRouterApiKey is not null)
            {
                orcaRouter.Configure(snapshot.OrcaRouterApiKey, model: snapshot.OrcaRouterModel);
            }
        });

        ConfigureAiProfiles(manager, snapshot);

        return manager;
    }

    private static void ConfigureAiProfiles(TranslationManager manager, SettingsSnapshot snapshot)
    {
        if (snapshot.AiServiceProfiles is null)
        {
            // Older hosts and snapshots produced while profile settings are malformed continue
            // using the legacy settings contract above.
            return;
        }

        // A profile snapshot is a complete allowlist, not a partial override. Remove
        // every legacy AI registration first so deleted or absent profiles cannot be
        // reached through stale legacy IDs/configuration from the same snapshot.
        foreach (var serviceId in ProfileManagedAiServiceIds)
        {
            manager.UnregisterService(serviceId);
        }

        foreach (var profile in snapshot.AiServiceProfiles)
        {
            var inner = CreateProfileProvider(profile, manager.SharedHttpClient);
            ITranslationService service = inner;
            if (profile.SupportsStreaming && inner is IStreamTranslationService stream)
            {
                service = profile.SupportsCorrection && inner is IGrammarCorrectionService grammar
                    ? new ProfiledStreamingGrammarTranslationService(
                        stream,
                        grammar,
                        profile.ServiceId,
                        profile.Name,
                        profile.ProfileId,
                        profile.ConfigurationRevision,
                        profile.IsEnabled)
                    : new ProfiledStreamingTranslationService(
                        stream,
                        profile.ServiceId,
                        profile.Name,
                        profile.ProfileId,
                        profile.ConfigurationRevision,
                        profile.IsEnabled);
            }
            else
            {
                service = new ProfiledTranslationService(
                    inner,
                    profile.ServiceId,
                    profile.Name,
                    profile.ProfileId,
                    profile.ConfigurationRevision,
                    profile.IsEnabled);
            }

            manager.RegisterService(service);
        }
    }

    private static ITranslationService CreateProfileProvider(AiServiceProfileSnapshot profile, HttpClient httpClient)
    {
        string Value(string id, string fallback = "")
            => profile.Configuration.TryGetValue(id, out var value) ? value : fallback;
        string Secret(string id)
            => profile.Secrets.TryGetValue(id, out var value) ? value : string.Empty;

        switch (profile.ProviderType)
        {
            case "openai":
            {
                var service = new OpenAIService(httpClient);
                service.Configure(Secret("apiKey"), Value("endpoint", OpenAIService.DefaultEndpoint),
                    Value("model", OpenAIService.DefaultModel), ParseDouble(Value("temperature", "0.3"), 0.3),
                    ParseOpenAIApiFormat(Value("apiFormat", "Auto")));
                return service;
            }
            case "ollama":
            {
                var service = new OllamaService(httpClient);
                service.Configure(Value("endpoint", "http://localhost:11434/v1/chat/completions"), Value("model", "llama3.2"));
                service.ConfigurePrompt(
                    Easydict.TranslationService.Models.OllamaPromptStyleParser.Parse(Value("promptStyle", "Standard")),
                    Value("customPrompt"));
                return service;
            }
            case "deepseek":
            {
                var service = new DeepSeekService(httpClient);
                service.Configure(Secret("apiKey"), model: Value("model", "deepseek-chat"));
                return service;
            }
            case "groq":
            {
                var service = new GroqService(httpClient);
                service.Configure(Secret("apiKey"), model: Value("model", "llama-3.3-70b-versatile"));
                return service;
            }
            case "zhipu":
            {
                var service = new ZhipuService(httpClient);
                service.Configure(Secret("apiKey"), model: Value("model", "glm-4.5-flash"));
                return service;
            }
            case "kimi":
            {
                var service = new KimiService(httpClient);
                service.Configure(Secret("apiKey"), model: Value("model", "kimi-k2-turbo-preview"));
                return service;
            }
            case "github":
            {
                var service = new GitHubModelsService(httpClient);
                service.Configure(Secret("token"), model: Value("model", "gpt-4.1"));
                return service;
            }
            case "custom-openai":
            {
                var service = new CustomOpenAIService(httpClient);
                service.Configure(Value("endpoint"), Secret("apiKey"), Value("model", "gpt-3.5-turbo"));
                return service;
            }
            case "openrouter":
            {
                var service = new OpenRouterService(httpClient);
                service.Configure(Secret("apiKey"), model: Value("model", "openrouter/free"));
                return service;
            }
            case "orcarouter":
            {
                var service = new OrcaRouterService(httpClient);
                service.Configure(Secret("apiKey"), model: Value("model", "orcarouter/free"));
                return service;
            }
            case "gemini":
            {
                var service = new GeminiService(httpClient);
                service.Configure(Secret("apiKey"), Value("model", "gemini-2.5-flash"));
                return service;
            }
            case "doubao":
            {
                var service = new DoubaoService(httpClient);
                service.Configure(Secret("apiKey"), Value("endpoint", "https://ark.cn-beijing.volces.com/api/v3/responses"),
                    Value("model", "doubao-seed-translation-250915"));
                return service;
            }
            case "claude-code":
            {
                var service = new ClaudeCodeService(httpClient);
                service.Configure(profile.IsEnabled, Value("model", ClaudeCodeService.DefaultModel), Value("executablePath"));
                return service;
            }
            case "codex":
            {
                var service = new CodexCliService(httpClient);
                service.Configure(profile.IsEnabled, Value("model", CodexCliService.DefaultModel),
                    Value("reasoningEffort", CodexCliService.DefaultReasoningEffort));
                return service;
            }
            default:
                throw new InvalidOperationException($"Unknown AI profile provider '{profile.ProviderType}'.");
        }
    }

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static void ConfigureIfPresent(TranslationManager manager, string serviceId, Action<ITranslationService> configure)
    {
        manager.ConfigureService(serviceId, configure);
    }

    private static OpenAIApiFormat ParseOpenAIApiFormat(string? value)
    {
        return Enum.TryParse<OpenAIApiFormat>(value, ignoreCase: true, out var parsed)
            ? parsed
            : OpenAIApiFormat.Auto;
    }
}
