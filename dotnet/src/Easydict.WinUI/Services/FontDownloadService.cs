using System.Diagnostics;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Font asset metadata for download management.
/// </summary>
internal sealed record FontAsset(string FileName, string[] DownloadUrls);

/// <summary>
/// Manages downloading and caching of CJK/multilingual fonts for PDF overlay rendering.
/// Fonts are stored under <c>%LocalAppData%\Easydict\Fonts\</c>.
/// </summary>
public sealed class FontDownloadService : IDisposable
{
    private const string FontsSubDir = "Fonts";
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    // Google Noto Sans CJK fonts (OFL license, full CJK + Latin coverage)
    // Using individual weight files from GitHub mirror for reliable direct downloads
    private static readonly Dictionary<string, FontAsset> FontAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-Hans"] = new("NotoSansSC-Regular.ttf", [
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKsc-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKsc-VF.ttf",
        ]),
        ["zh-Hant"] = new("NotoSansTC-Regular.ttf", [
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKtc-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKtc-VF.ttf",
        ]),
        ["ja"] = new("NotoSansJP-Regular.ttf", [
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKjp-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKjp-VF.ttf",
        ]),
        ["ko"] = new("NotoSansKR-Regular.ttf", [
            "https://github.com/notofonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKkr-VF.ttf",
            "https://github.com/googlefonts/noto-cjk/raw/main/Sans/Variable/TTF/NotoSansCJKkr-VF.ttf",
        ]),
    };

    // Language code mapping: translation Language enum → font key
    private static readonly Dictionary<Language, string> LanguageToFontKey = new()
    {
        [Language.SimplifiedChinese] = "zh-Hans",
        [Language.TraditionalChinese] = "zh-Hant",
        [Language.Japanese] = "ja",
        [Language.Korean] = "ko",
    };

    private readonly string _fontsDir;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private bool _disposed;

    public FontDownloadService() : this(null) { }

    public FontDownloadService(HttpClient? httpClient)
    {
        _fontsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict", FontsSubDir);
        Directory.CreateDirectory(_fontsDir);
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    /// <summary>Whether font for the given language is downloaded and available.</summary>
    public bool IsFontDownloaded(Language targetLanguage)
    {
        var fontPath = GetCachedFontPath(targetLanguage);
        return fontPath != null;
    }

    /// <summary>Whether any CJK font is downloaded.</summary>
    public bool HasAnyCjkFont => FontAssets.Values.Any(a => File.Exists(Path.Combine(_fontsDir, a.FileName)));

    /// <summary>
    /// Gets the cached font file path for the given target language, or null if not downloaded.
    /// Falls back to any available CJK font if the exact language match is not found.
    /// </summary>
    public string? GetCachedFontPath(Language targetLanguage)
    {
        // Try exact match first
        if (LanguageToFontKey.TryGetValue(targetLanguage, out var key) &&
            FontAssets.TryGetValue(key, out var asset))
        {
            var path = Path.Combine(_fontsDir, asset.FileName);
            if (File.Exists(path)) return path;
        }

        // Fallback: try any available CJK font (better than Arial for CJK text)
        foreach (var fa in FontAssets.Values)
        {
            var path = Path.Combine(_fontsDir, fa.FileName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>
    /// Returns true if the given language requires a CJK font for proper rendering.
    /// </summary>
    public static bool RequiresCjkFont(Language targetLanguage)
    {
        return LanguageToFontKey.ContainsKey(targetLanguage);
    }

    /// <summary>
    /// Ensures the font for the given target language is downloaded.
    /// Downloads the font if not already present, with progress reporting and retry logic.
    /// </summary>
    public async Task<string> EnsureFontAsync(
        Language targetLanguage,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!LanguageToFontKey.TryGetValue(targetLanguage, out var key))
        {
            throw new NotSupportedException($"No CJK font configured for language: {targetLanguage}");
        }

        var asset = FontAssets[key];
        var fontPath = Path.Combine(_fontsDir, asset.FileName);

        if (File.Exists(fontPath))
        {
            return fontPath;
        }

        await _downloadLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (File.Exists(fontPath))
            {
                return fontPath;
            }

            Debug.WriteLine($"[FontDownload] Downloading font for {targetLanguage} ({asset.FileName})...");
            await DownloadWithRetryAsync(asset.DownloadUrls, fontPath, $"font-{key}", progress, ct);
            Debug.WriteLine($"[FontDownload] Font downloaded to {fontPath}");
            return fontPath;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>Deletes all downloaded fonts.</summary>
    public void DeleteAllFonts()
    {
        foreach (var asset in FontAssets.Values)
        {
            var path = Path.Combine(_fontsDir, asset.FileName);
            TryDeleteFile(path);
        }
    }

    /// <summary>Gets total size of all downloaded font files in bytes.</summary>
    public long GetTotalFontSizeBytes()
    {
        long total = 0;
        foreach (var asset in FontAssets.Values)
        {
            var path = Path.Combine(_fontsDir, asset.FileName);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }
        return total;
    }

    private async Task DownloadWithRetryAsync(
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
            for (var retry = 0; retry <= MaxRetries; retry++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    if (retry > 0)
                    {
                        var delay = RetryDelays[Math.Min(retry - 1, RetryDelays.Length - 1)];
                        Debug.WriteLine($"[FontDownload] Retry {retry}/{MaxRetries} after {delay.TotalSeconds}s...");
                        await Task.Delay(delay, ct);
                    }

                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = File.Create(tempPath);

                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;

                        if (totalBytes > 0)
                        {
                            progress?.Report(new ModelDownloadProgress(
                                stage, downloaded, totalBytes, (double)downloaded / totalBytes * 100));
                        }
                    }

                    await fileStream.FlushAsync(ct);
                    fileStream.Close();

                    // Move temp to final location
                    File.Move(tempPath, outputPath, overwrite: true);
                    return; // Success
                }
                catch (OperationCanceledException)
                {
                    TryDeleteFile(tempPath);
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.WriteLine($"[FontDownload] Download failed: {ex.Message}");
                    TryDeleteFile(tempPath);
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to download font after trying all URLs with retries.", lastException);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadLock.Dispose();
        _httpClient.Dispose();
    }
}
