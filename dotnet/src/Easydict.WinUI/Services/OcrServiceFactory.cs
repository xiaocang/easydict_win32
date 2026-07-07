using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Factory that creates the appropriate <see cref="IOcrService"/> implementation
/// for the provided (or currently persisted) OCR configuration.
/// </summary>
public static class OcrServiceFactory
{
    internal static readonly TimeSpan ApiOcrRequestTimeout = TimeSpan.FromMinutes(3);

    private static readonly object _httpClientLock = new();
    private static HttpClient? _sharedHttpClient;
    private static ProxySnapshot? _sharedProxySnapshot;

    /// <summary>
    /// Creates an <see cref="IOcrService"/> for the given options.
    /// When <paramref name="options"/> is null, a fresh snapshot of persisted OCR settings is used.
    /// </summary>
    /// <param name="options">OCR engine and request options to use for this service instance.</param>
    /// <param name="httpClient">
    /// Optional shared <see cref="HttpClient"/> for API-based engines.
    /// If null, a shared client with a 3-minute timeout is used.
    /// </param>
    /// <returns>An <see cref="IOcrService"/> ready to recognize text.</returns>
    public static IOcrService Create(OcrServiceOptions? options = null, HttpClient? httpClient = null)
    {
        var resolved = options ?? OcrServiceOptions.FromSettings(SettingsService.Instance);
        var client = httpClient ?? GetSharedHttpClient(SettingsService.Instance);

        if (SettingsService.Instance.UseOcrWorker && resolved.Engine == OcrEngineType.WindowsNative)
        {
            return new Workers.OcrWorkerClient(
                SettingsService.Instance,
                CreateInProc(resolved, client));
        }

        return CreateInProc(resolved, client);
    }

    internal static IOcrService CreateInProc(OcrServiceOptions resolved, HttpClient? httpClient = null)
    {
        var client = httpClient ?? GetSharedHttpClient(SettingsService.Instance);
        return resolved.Engine switch
        {
            OcrEngineType.Ollama => new OllamaOcrService(client, resolved),
            OcrEngineType.CustomApi => new CustomApiOcrService(client, resolved),
            _ => new WindowsOcrService()
        };
    }

    internal static HttpClient CreateProxyAwareHttpClient(
        bool proxyEnabled,
        string? proxyUri,
        bool proxyBypassLocal,
        TimeSpan? timeout = null)
    {
        return new HttpClient(CreateProxyAwareHandler(proxyEnabled, proxyUri, proxyBypassLocal))
        {
            Timeout = timeout ?? ApiOcrRequestTimeout
        };
    }

    internal static HttpClientHandler CreateProxyAwareHandler(
        bool proxyEnabled,
        string? proxyUri,
        bool proxyBypassLocal)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyUri))
        {
            if (Uri.TryCreate(proxyUri, UriKind.Absolute, out var parsedProxyUri))
            {
                handler.Proxy = new WebProxy(parsedProxyUri)
                {
                    BypassProxyOnLocal = proxyBypassLocal
                };
                handler.UseProxy = true;
                Debug.WriteLine($"[OcrServiceFactory] Proxy configured: {parsedProxyUri.Host}:{parsedProxyUri.Port}, BypassLocal={proxyBypassLocal}");
            }
            else
            {
                Debug.WriteLine($"[OcrServiceFactory] Invalid proxy URI: {proxyUri}");
            }
        }

        return handler;
    }

    private static HttpClient GetSharedHttpClient(SettingsService settings)
    {
        var snapshot = ProxySnapshot.From(settings);

        lock (_httpClientLock)
        {
            if (_sharedHttpClient is null || _sharedProxySnapshot != snapshot)
            {
                _sharedHttpClient = CreateProxyAwareHttpClient(
                    snapshot.Enabled,
                    snapshot.Uri,
                    snapshot.BypassLocal);
                _sharedProxySnapshot = snapshot;
            }

            return _sharedHttpClient;
        }
    }

    private sealed record ProxySnapshot(bool Enabled, string Uri, bool BypassLocal)
    {
        public static ProxySnapshot From(SettingsService settings)
        {
            return new ProxySnapshot(
                settings.ProxyEnabled,
                settings.ProxyUri?.Trim() ?? string.Empty,
                settings.ProxyBypassLocal);
        }
    }
}
