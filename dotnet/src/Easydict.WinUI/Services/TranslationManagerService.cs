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
    // Use PublicationOnly to allow retry on initialization failure instead of caching exception forever
    private static readonly Lazy<TranslationManagerService> _instance =
        new(() => new TranslationManagerService(), LazyThreadSafetyMode.PublicationOnly);

    private TranslationManager _translationManager;
    private readonly SettingsService _settings;
    private readonly object _lock = new object();

    // Reference counting for safe disposal during streaming operations
    private readonly Dictionary<TranslationManager, int> _handleCounts = new();
    private readonly List<TranslationManager> _disposalQueue = new();

    public static TranslationManagerService Instance => _instance.Value;

    /// <summary>
    /// The shared TranslationManager instance.
    /// For streaming operations, prefer using AcquireHandle() to prevent disposal during use.
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

    /// <summary>
    /// Acquires a reference-counted handle to the current TranslationManager.
    /// The manager is guaranteed not to be disposed while any handle is held.
    /// Use this for streaming operations that may take longer than the disposal delay.
    /// </summary>
    /// <returns>A SafeManagerHandle that must be disposed when the operation completes.</returns>
    public SafeManagerHandle AcquireHandle()
    {
        lock (_lock)
        {
            var manager = _translationManager;

            if (!_handleCounts.ContainsKey(manager))
            {
                _handleCounts[manager] = 0;
            }
            _handleCounts[manager]++;

            Debug.WriteLine($"[TranslationManagerService] Handle acquired, count={_handleCounts[manager]}");

            return new SafeManagerHandle(manager, () => ReleaseHandle(manager));
        }
    }

    /// <summary>
    /// Releases a handle to a TranslationManager instance.
    /// If the manager is queued for disposal and all handles are released, it will be disposed.
    /// </summary>
    private void ReleaseHandle(TranslationManager manager)
    {
        lock (_lock)
        {
            if (_handleCounts.TryGetValue(manager, out var count))
            {
                count--;
                if (count <= 0)
                {
                    _handleCounts.Remove(manager);
                    Debug.WriteLine("[TranslationManagerService] Handle released, count=0");

                    // Check if this manager is queued for disposal
                    if (_disposalQueue.Contains(manager))
                    {
                        _disposalQueue.Remove(manager);
                        Debug.WriteLine("[TranslationManagerService] Disposing queued manager after last handle release");
                        DisposeManagerSafely(manager);
                    }
                }
                else
                {
                    _handleCounts[manager] = count;
                    Debug.WriteLine($"[TranslationManagerService] Handle released, count={count}");
                }
            }
        }
    }

    /// <summary>
    /// Safely disposes a manager, catching any exceptions.
    /// </summary>
    private static void DisposeManagerSafely(TranslationManager manager)
    {
        try
        {
            manager.Dispose();
            Debug.WriteLine("[TranslationManagerService] Manager disposed successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranslationManagerService] Error disposing manager: {ex.Message}");
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

        Debug.WriteLine("[TranslationManagerService] Initialized");
    }

    /// <summary>
    /// Configure all LLM services from settings.
    /// </summary>
    private void ConfigureServices()
    {
        // Configure Bing Translate (use China host if international services are disabled)
        _translationManager.ConfigureService("bing", service =>
        {
            if (service is BingTranslateService bing)
            {
                bing.Configure(useChinaHost: !_settings.EnableInternationalServices);
            }
        });

        // Configure DeepL
        _translationManager.ConfigureService("deepl", service =>
        {
            if (service is DeepLService deepl)
            {
                deepl.Configure(
                    _settings.DeepLApiKey,
                    useWebFirst: _settings.DeepLUseFreeApi);
            }
        });

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

        // Configure Doubao
        _translationManager.ConfigureService("doubao", service =>
        {
            if (service is DoubaoService doubao)
            {
                doubao.Configure(
                    _settings.DoubaoApiKey ?? "",
                    _settings.DoubaoEndpoint,
                    _settings.DoubaoModel);
            }
        });

        // Configure Caiyun
        _translationManager.ConfigureService("caiyun", service =>
        {
            if (service is CaiyunService caiyun)
            {
                caiyun.Configure(_settings.CaiyunApiKey ?? "");
            }
        });

        // Configure NiuTrans
        _translationManager.ConfigureService("niutrans", service =>
        {
            if (service is NiuTransService niutrans)
            {
                niutrans.Configure(_settings.NiuTransApiKey ?? "");
            }
        });

        // Linguee doesn't need configuration (no API key)

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
        }
    }

    /// <summary>
    /// Recreate the TranslationManager with new proxy settings.
    /// The old manager is disposed when all active handles are released.
    /// If no handles are held, disposal happens after a brief delay.
    /// </summary>
    public void ReconfigureProxy()
    {
        TranslationManager? oldManager;
        bool hasActiveHandles;

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

            // Check if the old manager has active handles
            hasActiveHandles = _handleCounts.TryGetValue(oldManager, out var count) && count > 0;

            if (hasActiveHandles)
            {
                // Queue for disposal when all handles are released
                _disposalQueue.Add(oldManager);
                Debug.WriteLine($"[TranslationManagerService] Old manager queued for disposal (active handles: {count})");
            }

            Debug.WriteLine("[TranslationManagerService] Proxy reconfigured");
        }

        // If no active handles, dispose after a brief delay for any non-streaming operations
        if (oldManager != null && !hasActiveHandles)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // Shorter delay since streaming ops use handles
                lock (_lock)
                {
                    // Re-check in case handles were acquired during the delay
                    if (_handleCounts.TryGetValue(oldManager, out var count) && count > 0)
                    {
                        // Handles were acquired, queue for disposal instead
                        if (!_disposalQueue.Contains(oldManager))
                        {
                            _disposalQueue.Add(oldManager);
                        }
                        Debug.WriteLine($"[TranslationManagerService] Old manager now has handles ({count}), queuing for disposal");
                        return;
                    }
                }
                DisposeManagerSafely(oldManager);
            });
        }
    }

    public void Dispose()
    {
        _translationManager.Dispose();
        Debug.WriteLine("[TranslationManagerService] Disposed");
    }
}
