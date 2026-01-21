using System.Diagnostics;
using Easydict.TranslationService;
using Easydict.TranslationService.Services;

namespace Easydict.WinUI.Services;

/// <summary>
/// Singleton service that provides a shared TranslationManager instance.
/// Centralizes HttpClient, cache, and service configuration.
/// </summary>
public sealed class TranslationManagerService : IDisposable
{
    private static readonly Lazy<TranslationManagerService> _instance = new(() => new TranslationManagerService());

    private TranslationManager _translationManager;
    private readonly SettingsService _settings;
    private readonly object _lock = new object();

    public static TranslationManagerService Instance => _instance.Value;

    /// <summary>
    /// The shared TranslationManager instance.
    /// </summary>
    public TranslationManager Manager
    {
        get
        {
            lock (_lock)
            {
                return _translationManager;
            }
        }
    }

    private TranslationManagerService()
    {
        _settings = SettingsService.Instance;

        var options = new TranslationManagerOptions
        {
            ProxyEnabled = _settings.ProxyEnabled,
            ProxyUri = _settings.ProxyUri,
            ProxyBypassLocal = _settings.ProxyBypassLocal
        };

        _translationManager = new TranslationManager(options);
        ConfigureServices();
        UpdateDefaultService();

        Debug.WriteLine("[TranslationManagerService] Initialized");
    }

    /// <summary>
    /// Configure all LLM services from settings.
    /// </summary>
    private void ConfigureServices()
    {
        // Configure OpenAI
        _translationManager.ConfigureService("openai", service =>
        {
            if (service is OpenAIService openai)
            {
                openai.Configure(
                    _settings.OpenAIApiKey ?? "",
                    _settings.OpenAIEndpoint,
                    _settings.OpenAIModel,
                    _settings.OpenAITemperature);
            }
        });

        // Configure Ollama
        _translationManager.ConfigureService("ollama", service =>
        {
            if (service is OllamaService ollama)
            {
                ollama.Configure(
                    _settings.OllamaEndpoint,
                    _settings.OllamaModel);
            }
        });

        // Configure BuiltIn AI
        _translationManager.ConfigureService("builtin", service =>
        {
            if (service is BuiltInAIService builtin)
            {
                builtin.Configure(_settings.BuiltInAIModel);
            }
        });

        // Configure DeepSeek
        _translationManager.ConfigureService("deepseek", service =>
        {
            if (service is DeepSeekService deepseek)
            {
                deepseek.Configure(
                    _settings.DeepSeekApiKey ?? "",
                    model: _settings.DeepSeekModel);
            }
        });

        // Configure Groq
        _translationManager.ConfigureService("groq", service =>
        {
            if (service is GroqService groq)
            {
                groq.Configure(
                    _settings.GroqApiKey ?? "",
                    model: _settings.GroqModel);
            }
        });

        // Configure Zhipu
        _translationManager.ConfigureService("zhipu", service =>
        {
            if (service is ZhipuService zhipu)
            {
                zhipu.Configure(
                    _settings.ZhipuApiKey ?? "",
                    model: _settings.ZhipuModel);
            }
        });

        // Configure GitHub Models
        _translationManager.ConfigureService("github", service =>
        {
            if (service is GitHubModelsService github)
            {
                github.Configure(
                    _settings.GitHubModelsToken ?? "",
                    model: _settings.GitHubModelsModel);
            }
        });

        // Configure Custom OpenAI
        _translationManager.ConfigureService("custom-openai", service =>
        {
            if (service is CustomOpenAIService customOpenai)
            {
                customOpenai.Configure(
                    _settings.CustomOpenAIEndpoint,
                    _settings.CustomOpenAIApiKey,
                    _settings.CustomOpenAIModel);
            }
        });

        // Configure Gemini
        _translationManager.ConfigureService("gemini", service =>
        {
            if (service is GeminiService gemini)
            {
                gemini.Configure(
                    _settings.GeminiApiKey ?? "",
                    _settings.GeminiModel);
            }
        });

        Debug.WriteLine("[TranslationManagerService] Services configured");
    }

    /// <summary>
    /// Reconfigure all services after settings change.
    /// </summary>
    public void ReconfigureServices()
    {
        lock (_lock)
        {
            ConfigureServices();
            UpdateDefaultService();
        }
    }

    /// <summary>
    /// Recreate the TranslationManager with new proxy settings.
    /// The old manager is disposed after a delay to allow in-flight operations to complete.
    /// </summary>
    public void ReconfigureProxy()
    {
        TranslationManager? oldManager;
        lock (_lock)
        {
            var options = new TranslationManagerOptions
            {
                ProxyEnabled = _settings.ProxyEnabled,
                ProxyUri = _settings.ProxyUri,
                ProxyBypassLocal = _settings.ProxyBypassLocal
            };

            oldManager = _translationManager;
            _translationManager = new TranslationManager(options);
            ConfigureServices();
            UpdateDefaultService();

            Debug.WriteLine("[TranslationManagerService] Proxy reconfigured");
        }

        // Defer disposal to allow in-flight operations to complete.
        // Proxy changes are rare user actions, so a brief delay is acceptable.
        if (oldManager != null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                try
                {
                    oldManager.Dispose();
                    Debug.WriteLine("[TranslationManagerService] Old manager disposed");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TranslationManagerService] Error disposing old manager: {ex.Message}");
                }
            });
        }
    }

    /// <summary>
    /// Update the default service ID from settings.
    /// </summary>
    private void UpdateDefaultService()
    {
        var defaultService = _settings.DefaultService;
        if (_translationManager.Services.ContainsKey(defaultService))
        {
            _translationManager.DefaultServiceId = defaultService;
        }
        // else keeps default "google" from TranslationManager constructor
    }

    public void Dispose()
    {
        _translationManager.Dispose();
        Debug.WriteLine("[TranslationManagerService] Disposed");
    }
}
