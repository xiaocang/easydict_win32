using System.Diagnostics;
using System.Net;

namespace Easydict.WinUI.Services;

/// <summary>
/// Progress report for model/font/runtime downloads.
/// </summary>
public sealed record ModelDownloadProgress(
    string Stage,
    long BytesDownloaded,
    long TotalBytes,
    double Percentage);

/// <summary>
/// Shared HTTP download client with retry logic, proxy support, and fastest-source selection.
/// Used by both <see cref="LayoutModelDownloadService"/> and <see cref="FontDownloadService"/>.
/// </summary>
public sealed class ModelDownloadClient : IDisposable
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    private static readonly TimeSpan SourceProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private bool _disposed;

    public ModelDownloadClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateProxyAwareHttpClient();
    }

    /// <summary>
    /// Downloads a file with retry logic across multiple mirror URLs.
    /// Uses temp file + atomic move for crash safety.
    /// </summary>
    public async Task DownloadWithRetryAsync(
        string[] urls,
        string outputPath,
        string stage,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var tempPath = outputPath + ".tmp";
        Exception? lastException = null;

        foreach (var url in urls)
        {
            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                        Debug.WriteLine($"[ModelDownload] Retry {attempt}/{MaxRetries} after {delay.TotalSeconds}s for {url}");
                        await Task.Delay(delay, ct);
                    }

                    await DownloadFileAsync(url, tempPath, stage, progress, ct);

                    // Move temp to final location atomically
                    File.Move(tempPath, outputPath, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDeleteFile(tempPath);
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.WriteLine($"[ModelDownload] Download failed: {ex.Message}");
                    TryDeleteFile(tempPath);
                }
            }

            Debug.WriteLine($"[ModelDownload] All retries exhausted for {url}, trying next source...");
        }

        throw new InvalidOperationException(
            $"Failed to download {stage} from all sources.", lastException);
    }

    /// <summary>
    /// Probes all URLs in parallel with HEAD requests and returns them ordered by response time.
    /// URLs that respond successfully are prioritized over those that fail.
    /// </summary>
    public async Task<string[]> GetOrderedUrlsAsync(string[] urls, CancellationToken ct)
    {
        if (urls.Length <= 1)
            return urls;

        var tasks = urls.Select(async url =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(SourceProbeTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                sw.Stop();

                var ok = response.IsSuccessStatusCode;
                Debug.WriteLine($"[ModelDownload] Probe {url}: {(ok ? "OK" : response.StatusCode)} in {sw.ElapsedMilliseconds}ms");
                return (url, time: sw.ElapsedMilliseconds, ok);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Outer token cancelled — propagate immediately
            }
            catch (Exception ex)
            {
                sw.Stop();
                Debug.WriteLine($"[ModelDownload] Probe {url}: failed ({ex.GetType().Name}) in {sw.ElapsedMilliseconds}ms");
                return (url, time: long.MaxValue, ok: false);
            }
        });

        var results = await Task.WhenAll(tasks);

        var ordered = results
            .OrderBy(r => r.ok ? 0 : 1)
            .ThenBy(r => r.time)
            .Select(r => r.url)
            .ToArray();

        Debug.WriteLine($"[ModelDownload] Source priority: {string.Join(" → ", ordered.Select(u => new Uri(u).Host))}");
        return ordered;
    }

    /// <summary>
    /// Checks whether a file exists and meets the minimum size requirement.
    /// </summary>
    public static bool IsFileValid(string path, long minSize)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length >= minSize;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tries to delete a file, ignoring errors.</summary>
    public static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* ignore cleanup errors */ }
    }

    private async Task DownloadFileAsync(
        string url,
        string outputPath,
        string stage,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        long bytesDownloaded = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(outputPath);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            bytesDownloaded += bytesRead;

            var percentage = totalBytes > 0 ? (double)bytesDownloaded / totalBytes * 100 : -1;
            progress?.Report(new ModelDownloadProgress(stage, bytesDownloaded, totalBytes, percentage));
        }
    }

    /// <summary>
    /// Creates an HttpClient that respects the user's proxy settings from SettingsService.
    /// </summary>
    private static HttpClient CreateProxyAwareHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };

        // Apply proxy settings if configured
        var settings = SettingsService.Instance;
        if (settings.ProxyEnabled && !string.IsNullOrWhiteSpace(settings.ProxyUri))
        {
            if (Uri.TryCreate(settings.ProxyUri, UriKind.Absolute, out var proxyUri))
            {
                handler.Proxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = settings.ProxyBypassLocal
                };
                handler.UseProxy = true;
                Debug.WriteLine($"[ModelDownload] Proxy configured: {proxyUri.Host}:{proxyUri.Port}, BypassLocal={settings.ProxyBypassLocal}");
            }
            else
            {
                Debug.WriteLine($"[ModelDownload] Invalid proxy URI: {settings.ProxyUri}");
            }
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Easydict-Win32/1.0");
        return client;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}
