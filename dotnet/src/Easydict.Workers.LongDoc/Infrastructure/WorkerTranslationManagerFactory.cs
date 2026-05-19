using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Services;

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
        };

        var manager = new TranslationManager(options);

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

        ConfigureIfPresent(manager, "ollama", svc =>
        {
            if (svc is OllamaService ollama)
            {
                ollama.Configure(snapshot.OllamaEndpoint, snapshot.OllamaModel);
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

        return manager;
    }

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
