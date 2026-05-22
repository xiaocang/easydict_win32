using System.Diagnostics;
using Easydict.OpenVINO.Inference;
using Easydict.OpenVINO.Services;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService.Services;
using Easydict.WindowsAI.Services;

namespace Easydict.Workers.LocalAi.Infrastructure;

/// <summary>
/// Process-local worker state: holds the SettingsSnapshot received via "configure"
/// and lazily builds the local AI provider instances on first use. Provider
/// instances are cached for the lifetime of the worker process (which is
/// scoped to a single translate / prepare call per the "exit on completion"
/// lifecycle).
/// </summary>
internal sealed class WorkerState
{
    private SettingsSnapshot? _settings;
    private PhiSilicaTranslationService? _phiSilica;
    private FoundryLocalService? _foundryLocal;
    private OpenVINOTranslationService? _openVino;
    private HttpClient? _httpClient;
    private readonly object _lock = new();

    public SettingsSnapshot? Settings
    {
        get
        {
            lock (_lock) return _settings;
        }
    }

    public bool IsConfigured
    {
        get
        {
            lock (_lock) return _settings is not null;
        }
    }

    public void ApplySettings(SettingsSnapshot snapshot)
    {
        lock (_lock)
        {
            _settings = snapshot;
            Trace.WriteLine(
                $"[LocalAiWorker] Settings applied. provider={snapshot.LocalAIProvider}, proxyEnabled={snapshot.ProxyEnabled}, openVinoDevice={snapshot.OpenVinoDevice}");
        }
    }

    public PhiSilicaTranslationService GetPhiSilica()
    {
        lock (_lock)
        {
            if (_phiSilica is null)
            {
                Trace.WriteLine("[LocalAiWorker] Creating PhiSilicaTranslationService.");
                _phiSilica = new PhiSilicaTranslationService();
                Trace.WriteLine("[LocalAiWorker] Created PhiSilicaTranslationService.");
            }
            return _phiSilica;
        }
    }

    public FoundryLocalService GetFoundryLocal()
    {
        lock (_lock)
        {
            if (_httpClient is null)
            {
                Trace.WriteLine("[LocalAiWorker] Creating HTTP client for Foundry Local.");
                _httpClient = CreateConfiguredHttpClient();
            }

            if (_foundryLocal is null)
            {
                Trace.WriteLine("[LocalAiWorker] Creating FoundryLocalService.");
                _foundryLocal = new FoundryLocalService(_httpClient);
                Trace.WriteLine("[LocalAiWorker] Created FoundryLocalService.");
            }

            _foundryLocal.Configure(
                _settings?.FoundryLocalEndpoint,
                _settings?.FoundryLocalModel);
            Trace.WriteLine(
                $"[LocalAiWorker] Configured FoundryLocalService. endpointConfigured={!string.IsNullOrWhiteSpace(_settings?.FoundryLocalEndpoint)}, model={_settings?.FoundryLocalModel}");
            return _foundryLocal;
        }
    }

    public OpenVINOTranslationService GetOpenVino()
    {
        lock (_lock)
        {
            if (_openVino is null)
            {
                Trace.WriteLine("[LocalAiWorker] Creating OpenVINOTranslationService.");
                _openVino = new OpenVINOTranslationService();
                Trace.WriteLine("[LocalAiWorker] Created OpenVINOTranslationService.");
            }

            var device = ParseOpenVinoDevice(_settings?.OpenVinoDevice);
            Trace.WriteLine($"[LocalAiWorker] Configuring OpenVINOTranslationService. device={device}");
            _openVino.Configure(device);
            Trace.WriteLine("[LocalAiWorker] Configured OpenVINOTranslationService.");
            return _openVino;
        }
    }

    private HttpClient CreateConfiguredHttpClient()
    {
        // Mirrors TranslationManager's default HttpClient setup, simplified: proxy
        // settings come from the snapshot. Workers don't share the host's pool.
        var handler = new HttpClientHandler();
        if (_settings?.ProxyEnabled == true && !string.IsNullOrWhiteSpace(_settings.ProxyUri))
        {
            try
            {
                handler.Proxy = new System.Net.WebProxy(_settings.ProxyUri);
                handler.UseProxy = true;
            }
            catch
            {
                // Invalid proxy URI — fall back to no proxy.
            }
        }
        return new HttpClient(handler);
    }

    private static OpenVINODevice ParseOpenVinoDevice(string? value)
    {
        return Enum.TryParse<OpenVINODevice>(value, ignoreCase: true, out var parsed)
            ? parsed
            : OpenVINODevice.Auto;
    }
}
