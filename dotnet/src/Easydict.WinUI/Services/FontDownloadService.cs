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
    private readonly ModelDownloadClient _client;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private bool _disposed;

    public FontDownloadService() : this(null) { }

    public FontDownloadService(HttpClient? httpClient)
    {
        _fontsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict", FontsSubDir);
        Directory.CreateDirectory(_fontsDir);
        _client = new ModelDownloadClient(httpClient);
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
            await _client.DownloadWithRetryAsync(asset.DownloadUrls, fontPath, $"font-{key}", progress, ct);
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
            ModelDownloadClient.TryDeleteFile(path);
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadLock.Dispose();
        _client.Dispose();
    }
}
