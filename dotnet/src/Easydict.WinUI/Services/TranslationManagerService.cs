using System.Diagnostics;
using Easydict.OpenVINO.Inference;
using Easydict.OpenVINO.Services;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Services;
using Easydict.WindowsAI.Services;

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

    // Local AI providers are stateful: Phi Silica readiness is expensive to
    // probe repeatedly, and OpenVINO lazy-loads ONNX sessions. Reuse instances
    // across ConfigureServices/ReconfigureProxy so a warmed model isn't
    // discarded when the user toggles unrelated settings.
    private PhiSilicaTranslationService? _phiSilicaService;
    private FoundryLocalService? _foundryLocalService;
    private OpenVINOTranslationService? _openVinoService;
    private LocalAITranslationService? _localAIService;
    // When SettingsService.UseLocalAiWorker is true at startup, _localAIWorkerClient
    // is registered instead of _localAIService. Toggling the setting at runtime
    // requires an app restart (not auto-swappable).
    private Workers.LocalAiWorkerClient? _localAIWorkerClient;

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

    internal OpenVINOTranslationService? OpenVinoService
    {
        get
        {
            lock (_lock)
            {
                return _openVinoService;
            }
        }
    }

    internal FoundryLocalService? FoundryLocalService
    {
        get
        {
            lock (_lock)
            {
                return _foundryLocalService;
            }
        }
    }

    public Task<LocalModelStatus> PrepareFoundryLocalAsync(CancellationToken cancellationToken)
    {
        FoundryLocalService? service;
        lock (_lock)
        {
            _foundryLocalService ??= new FoundryLocalService(_translationManager.SharedHttpClient);
            _foundryLocalService.Configure(_settings.FoundryLocalEndpoint, _settings.FoundryLocalModel);
            service = _foundryLocalService;
        }

        return service.PrepareAsync(cancellationToken);
    }

    public Task<LocalModelStatus> PrepareFoundryLocalAsync(
        string? endpoint,
        string? model,
        CancellationToken cancellationToken)
    {
        var service = CreateFoundryLocalProbeService(endpoint, model);
        return service.PrepareAsync(cancellationToken);
    }

    public Task<LocalModelStatus> GetFoundryLocalStatusAsync(CancellationToken cancellationToken)
    {
        FoundryLocalService? service;
        lock (_lock)
        {
            _foundryLocalService ??= new FoundryLocalService(_translationManager.SharedHttpClient);
            _foundryLocalService.Configure(_settings.FoundryLocalEndpoint, _settings.FoundryLocalModel);
            service = _foundryLocalService;
        }

        return service.CheckRuntimeStatusAsync(cancellationToken);
    }

    public Task<LocalModelStatus> GetFoundryLocalStatusAsync(
        string? endpoint,
        string? model,
        CancellationToken cancellationToken)
    {
        var service = CreateFoundryLocalProbeService(endpoint, model);
        return service.CheckRuntimeStatusAsync(cancellationToken);
    }

    private FoundryLocalService CreateFoundryLocalProbeService(string? endpoint, string? model)
    {
        HttpClient httpClient;
        lock (_lock)
        {
            httpClient = _translationManager.SharedHttpClient;
        }

        var service = new FoundryLocalService(httpClient);
        service.Configure(endpoint, model);
        return service;
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

        _settings.EnableInternationalServicesChanged += (_, _) => ReconfigureServices();

        // Register device token in the background (same fire-and-forget pattern as InitializeRegionDefaultsAsync)
        _ = Task.Run(() => EnsureDeviceRegisteredAsync());

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
                    useWebFirst: _settings.DeepLUseFreeApi,
                    useQualityOptimized: _settings.DeepLUseQualityOptimized);
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
                    _settings.OpenAITemperature,
                    ParseOpenAIApiFormat(_settings.OpenAIApiFormatOverride));
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

        // Local AI is exposed as one user-facing service. Auto mode tries
        // Phi Silica first, then Foundry Local, then OpenVINO/NLLB as the
        // hardware-accelerated translation fallback.
        //
        // The three providers are wrapped in Lazy<> so they don't materialize
        // until the user actually translates via local AI. PhiSilica's health
        // monitor, FoundryLocal's CLI endpoint resolver, and OpenVINO's
        // ModelDownloadService all sit idle when a user picks Google or DeepL
        // as their provider and never touches local AI.
        _localAIService ??= CreateLocalAITranslationService();
        if (_localAIWorkerClient == null && _settings.UseLocalAiWorker)
        {
            _localAIWorkerClient = new Workers.LocalAiWorkerClient(
                _settings,
                _localAIService,
                _localAIService,
                _localAIService);
        }

        _translationManager.UnregisterService(LocalAITranslationService.LegacyOpenVinoServiceId);
        ITranslationService localAiRegistration = _settings.UseLocalAiWorker && _localAIWorkerClient is not null
            ? _localAIWorkerClient
            : _localAIService;
        _translationManager.RegisterService(localAiRegistration);

        // Re-apply settings to already-materialized sub-services so a settings
        // change picks up new endpoint/device values without forcing materialization.
        if (_foundryLocalService != null)
        {
            _foundryLocalService.Configure(_settings.FoundryLocalEndpoint, _settings.FoundryLocalModel);
        }
        if (_openVinoService != null)
        {
            _openVinoService.Configure(ParseOpenVinoDevice(_settings.OpenVinoDevice));
        }
        // Configure the in-proc service even when the worker is active; it is the
        // fallback path if the worker exe is missing or cannot complete handshake.
        _localAIService?.Configure(LocalAIProviderModeExtensions.Parse(_settings.LocalAIProvider));

        // Configure BuiltIn AI
        _translationManager.ConfigureService("builtin", service =>
        {
            if (service is BuiltInAIService builtin)
            {
                builtin.Configure(_settings.BuiltInAIModel, _settings.BuiltInAIApiKey, _settings.DeviceId, _settings.DeviceToken);
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

        // Configure Youdao
        _translationManager.ConfigureService("youdao", service =>
        {
            if (service is YoudaoService youdao)
            {
                youdao.Configure(
                    _settings.YoudaoAppKey,
                    _settings.YoudaoAppSecret,
                    _settings.YoudaoUseOfficialApi);
            }
        });

        // Configure Volcano
        _translationManager.ConfigureService("volcano", service =>
        {
            if (service is VolcanoService volcano)
            {
                volcano.Configure(
                    _settings.VolcanoAccessKeyId ?? "",
                    _settings.VolcanoSecretAccessKey ?? "");
            }
        });

        // Linguee doesn't need configuration (no API key)

        RegisterImportedMdxServices();

        Debug.WriteLine("[TranslationManagerService] Services configured");
    }

    private LocalAITranslationService CreateLocalAITranslationService()
    {
        var phiSilicaLazy = new Lazy<PhiSilicaTranslationService>(
            () =>
            {
                lock (_lock)
                {
                    return _phiSilicaService ??= new PhiSilicaTranslationService();
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        var foundryLocalLazy = new Lazy<IStreamTranslationService>(
            () =>
            {
                lock (_lock)
                {
                    _foundryLocalService ??= new FoundryLocalService(_translationManager.SharedHttpClient);
                    _foundryLocalService.Configure(_settings.FoundryLocalEndpoint, _settings.FoundryLocalModel);
                    return _foundryLocalService;
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        var openVinoLazy = new Lazy<OpenVINOTranslationService>(
            () =>
            {
                lock (_lock)
                {
                    _openVinoService ??= new OpenVINOTranslationService();
                    _openVinoService.Configure(ParseOpenVinoDevice(_settings.OpenVinoDevice));
                    return _openVinoService;
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        return new LocalAITranslationService(phiSilicaLazy, foundryLocalLazy, openVinoLazy);
    }

    private void RegisterImportedMdxServices()
    {
        foreach (var dictionary in _settings.ImportedMdxDictionaries)
        {
            if (string.IsNullOrWhiteSpace(dictionary.ServiceId) || string.IsNullOrWhiteSpace(dictionary.FilePath))
            {
                continue;
            }

            try
            {
                var service = new MdxDictionaryTranslationService(
                    dictionary.ServiceId,
                    dictionary.DisplayName,
                    dictionary.FilePath,
                    dictionary.Regcode,
                    dictionary.Email,
                    deferLoad: true,
                    isEncryptedHint: dictionary.IsEncrypted);

                // Load MDD resource files (from stored paths, or re-discover for migration)
                var mddPaths = dictionary.MddFilePaths;
                if (mddPaths.Count == 0)
                {
                    mddPaths = MdxDictionaryTranslationService.DiscoverMddFiles(dictionary.FilePath);
                    if (mddPaths.Count > 0)
                    {
                        dictionary.MddFilePaths = mddPaths;
                        Debug.WriteLine($"[TranslationManagerService] Auto-discovered {mddPaths.Count} MDD file(s) for '{dictionary.FilePath}'");
                    }
                }

                if (mddPaths.Count > 0)
                {
                    service.LoadMddFiles(mddPaths);
                }

                service.DictionaryLoaded += loadedService => QueueMdxIndexBuild(dictionary, loadedService);
                _translationManager.RegisterService(service);
                LocalDictionaryIndexService.Instance.RegisterDescriptor(dictionary);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TranslationManagerService] Failed to load MDX dictionary '{dictionary.FilePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Unregister an MDX dictionary service by its ID.
    /// </summary>
    public void UnregisterMdxDictionary(string serviceId)
    {
        lock (_lock)
        {
            _translationManager.UnregisterService(serviceId);
        }

        LocalDictionaryIndexService.Instance.RemoveDictionary(serviceId);
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
    /// Clear translation result caches for all live manager instances.
    /// </summary>
    public void ClearTranslationCache()
    {
        List<TranslationManager> managersToClear;

        lock (_lock)
        {
            managersToClear = [_translationManager];

            foreach (var manager in _handleCounts.Keys)
            {
                if (!managersToClear.Contains(manager))
                {
                    managersToClear.Add(manager);
                }
            }

            foreach (var manager in _disposalQueue)
            {
                if (!managersToClear.Contains(manager))
                {
                    managersToClear.Add(manager);
                }
            }
        }

        foreach (var manager in managersToClear)
        {
            manager.ClearTranslationCache();
        }

        Debug.WriteLine($"[TranslationManagerService] Cleared translation cache for {managersToClear.Count} manager(s)");
    }

    public bool TryRegisterMdxDictionary(SettingsService.ImportedMdxDictionary dictionary, out string? error)
    {
        error = null;
        try
        {
            var service = new MdxDictionaryTranslationService(
                dictionary.ServiceId, dictionary.DisplayName, dictionary.FilePath,
                dictionary.Regcode, dictionary.Email,
                deferLoad: false,
                isEncryptedHint: dictionary.IsEncrypted);
            lock (_lock)
            {
                _translationManager.RegisterService(service);
            }

            service.DictionaryLoaded += loadedService => QueueMdxIndexBuild(dictionary, loadedService);
            QueueMdxIndexBuild(dictionary, service);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
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
            _foundryLocalService = null;
            // Unhook the wrapper's StatusChanged subscriptions before dropping
            // the reference, otherwise the inner providers keep the old wrapper
            // alive and double-forward events to the new instance.
            _localAIService?.Dispose();
            _localAIService = null;
            _localAIWorkerClient?.Dispose();
            _localAIWorkerClient = null;
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

    /// <summary>
    /// Ensures the device has a valid HMAC token from the proxy server.
    /// Short-circuits if a token is already persisted in settings.
    /// On success, saves the token and reconfigures the BuiltIn AI service.
    /// </summary>
    private async Task EnsureDeviceRegisteredAsync()
    {
        if (!string.IsNullOrEmpty(_settings.DeviceToken))
        {
            Debug.WriteLine("[TranslationManagerService] Device token already present, skipping registration");
            return;
        }

        Debug.WriteLine("[TranslationManagerService] Device token missing, attempting registration...");

        BuiltInAIService? builtin;
        lock (_lock)
        {
            builtin = _translationManager.Services.TryGetValue("builtin", out var svc)
                ? svc as BuiltInAIService
                : null;
        }

        if (builtin == null)
        {
            Debug.WriteLine("[TranslationManagerService] BuiltInAIService not found");
            return;
        }

        try
        {
            var token = await builtin.RegisterDeviceAsync();
            if (!string.IsNullOrEmpty(token))
            {
                _settings.DeviceToken = token;
                _settings.Save();

                // Reconfigure to include the new token
                lock (_lock)
                {
                    _translationManager.ConfigureService("builtin", service =>
                    {
                        if (service is BuiltInAIService b)
                        {
                            b.Configure(_settings.BuiltInAIModel, _settings.BuiltInAIApiKey, _settings.DeviceId, _settings.DeviceToken);
                        }
                    });
                }

                Debug.WriteLine("[TranslationManagerService] Device registered and configured successfully");
            }
            else
            {
                Debug.WriteLine("[TranslationManagerService] Device registration returned no token");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranslationManagerService] Device registration failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // TranslationManager.Dispose() doesn't dispose registered services, and
        // OpenVINOTranslationService owns native ORT sessions plus a SemaphoreSlim.
        // Dispose it explicitly so we don't leak the warmed model on app shutdown.
        _openVinoService?.Dispose();
        _openVinoService = null;
        // Unhook StatusChanged from the inner providers so the singleton
        // PhiSilica/OpenVINO instances don't retain the disposed wrapper.
        _localAIService?.Dispose();
        _localAIService = null;
        _localAIWorkerClient?.Dispose();
        _localAIWorkerClient = null;
        // FoundryLocalService isn't IDisposable but it caches a reference to
        // _translationManager.SharedHttpClient. Null it for parity with the
        // other provider fields so it can't be accidentally used after the
        // manager (and its HttpClient) are disposed.
        _foundryLocalService = null;
        _phiSilicaService = null;

        _translationManager.Dispose();
        Debug.WriteLine("[TranslationManagerService] Disposed");
    }

    private static OpenVINODevice ParseOpenVinoDevice(string? value)
    {
        return Enum.TryParse<OpenVINODevice>(value, ignoreCase: true, out var parsed)
            ? parsed
            : OpenVINODevice.Auto;
    }

    private static OpenAIApiFormat ParseOpenAIApiFormat(string? value)
    {
        return Enum.TryParse<OpenAIApiFormat>(value, ignoreCase: true, out var parsed)
            ? parsed
            : OpenAIApiFormat.Auto;
    }

    private static void QueueMdxIndexBuild(
        SettingsService.ImportedMdxDictionary dictionary,
        MdxDictionaryTranslationService service)
    {
        _ = LocalDictionaryIndexService.Instance
            .EnsureIndexAsync(dictionary, service)
            .ContinueWith(
                task =>
                {
                    if (task.Exception is not null)
                    {
                        Debug.WriteLine(
                            $"[TranslationManagerService] Failed to build MDX index for '{dictionary.ServiceId}': {task.Exception.GetBaseException().Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }
}
