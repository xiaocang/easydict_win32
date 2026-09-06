using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service for language detection and intelligent target language selection.
/// Implements macOS-style dual-language preference algorithm.
/// </summary>
public sealed class LanguageDetectionService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<string, CancellationToken, Action?, Task<Language>> _detectLanguage;
    private static readonly string[] DetectionServiceIds = ["google", "bing"];
    private static readonly TimeSpan DetectionAttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Memory cache for detection results.
    /// Uses IMemoryCache interface for consistency with TranslationManager.
    /// Cast to MemoryCache when calling Compact() in ClearCache().
    /// </summary>
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _cacheOptions;
    private int _disposed;

    public LanguageDetectionService(SettingsService settings)
        : this(settings, DetectWithSharedServicesAsync)
    {
    }

    internal LanguageDetectionService(
        SettingsService settings,
        Func<string, CancellationToken, Task<Language>> detectLanguage)
        : this(settings, (text, ct, _) => detectLanguage(text, ct))
    {
        ArgumentNullException.ThrowIfNull(detectLanguage);
    }

    internal LanguageDetectionService(
        SettingsService settings,
        Func<string, CancellationToken, Action?, Task<Language>> detectLanguage)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _detectLanguage = detectLanguage ?? throw new ArgumentNullException(nameof(detectLanguage));

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 500 // Max 500 detection results
        });

        _cacheOptions = new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetSlidingExpiration(TimeSpan.FromMinutes(5))
            .SetAbsoluteExpiration(TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Detect the language of the given text with caching.
    /// </summary>
    /// <param name="text">Text to detect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detected language, or Language.Auto if detection fails.</returns>
    public async Task<Language> DetectAsync(
        string text,
        CancellationToken cancellationToken = default,
        Action? onRateLimited = null)
    {
        if (IsDisposed())
        {
            return Language.Auto;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Language.Auto;
        }

        // Don't detect very short text (unreliable)
        // CJK characters carry more meaning per character, so use lower threshold
        var minLength = ContainsCjk(text) ? 2 : 4;
        if (text.Length < minLength)
        {
            Debug.WriteLine($"[Detection] Text too short ({text.Length} chars, min={minLength}), skipping detection");
            return Language.Auto;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Check cache (guard against disposed cache)
        var cacheKey = GetCacheKey(text);
        try
        {
            if (_cache.TryGetValue(cacheKey, out Language cached))
            {
                Debug.WriteLine($"[Detection] Cache hit: {cached}");
                return cached;
            }
        }
        catch (ObjectDisposedException)
        {
            return Language.Auto;
        }

        try
        {
            var detected = await _detectLanguage(text, cancellationToken, onRateLimited);
            cancellationToken.ThrowIfCancellationRequested();

            // Unknown results can be transient. Let the next query retry detection.
            if (detected == Language.Auto)
                return Language.Auto;

            // Cache the result (ignore if cache was disposed)
            try
            {
                _cache.Set(cacheKey, detected, _cacheOptions);
            }
            catch (ObjectDisposedException)
            {
                // Swallow; service is being disposed
            }

            Debug.WriteLine($"[Detection] Detected language: {detected.GetDisplayName()}");
            return detected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine("[Detection] Canceled");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Detection] Failed: {ex.Message}");
            return Language.Auto; // Graceful degradation
        }
    }

    private static async Task<Language> DetectWithSharedServicesAsync(
        string text, CancellationToken cancellationToken, Action? onRateLimited)
    {
#if WINUI_TEST
        // Deterministic UI regression coverage; excluded from production builds.
        var scenario = Environment.GetEnvironmentVariable("EASYDICT_TEST_DETECTION");
        if (scenario is "rate-limited-recovered" or "rate-limited-failed")
        {
            onRateLimited?.Invoke();
            await Task.Delay(50, cancellationToken);
            return scenario == "rate-limited-recovered" ? Language.English : Language.Auto;
        }
#endif
        // Keep both providers alive if proxy settings change during detection.
        using var handle = TranslationManagerService.Instance.AcquireHandle();
        return await DetectWithFallbackAsync(text, handle.Manager.Services, cancellationToken, onRateLimited);
    }

    internal static async Task<Language> DetectWithFallbackAsync(
        string text,
        IReadOnlyDictionary<string, ITranslationService> services,
        CancellationToken cancellationToken,
        Action? onRateLimited = null)
    {
        foreach (var serviceId in DetectionServiceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!services.TryGetValue(serviceId, out var service) || !service.IsConfigured)
                continue;

            // Bound each attempt so an unreachable primary can reach the fallback.
            // A window's shorter deadline or user cancellation still takes precedence.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(DetectionAttemptTimeout);
            try
            {
                var detected = await service.DetectLanguageAsync(text, attemptCts.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (detected != Language.Auto)
                    return detected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsRateLimited(ex))
                    onRateLimited?.Invoke();

                // Do not log exception messages: provider URLs can include the input text.
                var reason = ex is HttpRequestException http
                    ? $"HTTP {http.StatusCode?.ToString() ?? "network error"}"
                    : ex.GetType().Name;
                CrashDiagnostics.Log($"[Detection] {serviceId} failed ({reason}); trying next provider.");
            }
        }

        return Language.Auto;
    }

    private static bool IsRateLimited(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                or TranslationException { ErrorCode: TranslationErrorCode.RateLimited })
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get target language based on detected source language using macOS algorithm.
    /// </summary>
    /// <param name="detectedSource">The detected source language.</param>
    /// <returns>Recommended target language.</returns>
    public Language GetTargetLanguage(Language detectedSource)
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        Debug.WriteLine($"[Detection] GetTargetLanguage: detected={detectedSource}, first={firstLang}, second={secondLang}");

        // Default target is first language
        var targetLang = firstLang;

        // If detected source matches first language, use second language as target
        if (detectedSource == firstLang)
        {
            targetLang = secondLang;
            Debug.WriteLine($"[Detection] Detected matches first language, using second language: {targetLang}");
        }

        // Prevent translating to same language (fallback to English ↔ Chinese)
        if (targetLang == detectedSource)
        {
            targetLang = GetFallbackLanguage(detectedSource);
            Debug.WriteLine($"[Detection] Target equals source, using fallback: {targetLang}");
        }

        return targetLang;
    }

    /// <summary>
    /// Get fallback target language when source and target are the same.
    /// Implements macOS default: English ↔ Chinese toggle.
    /// </summary>
    private static Language GetFallbackLanguage(Language source)
    {
        return source == Language.English
            ? Language.SimplifiedChinese
            : Language.English;
    }

    /// <summary>
    /// Clear the detection cache.
    /// </summary>
    public void ClearCache()
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            (_cache as MemoryCache)?.Compact(1.0); // Remove all entries (100% compaction)
            Debug.WriteLine("[Detection] Cache cleared");
        }
        catch (ObjectDisposedException)
        {
            // Ignore if cache is already disposed
        }
    }

    /// <summary>
    /// Generate cache key for text (SHA256 hash).
    /// </summary>
    private static string GetCacheKey(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Check if text contains CJK (Chinese, Japanese, Korean) characters.
    /// CJK characters carry more meaning per character, allowing shorter detection thresholds.
    /// </summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            // CJK Unified Ideographs: U+4E00 - U+9FFF
            // CJK Extension A: U+3400 - U+4DBF
            // Hiragana: U+3040 - U+309F
            // Katakana: U+30A0 - U+30FF
            // Hangul Syllables: U+AC00 - U+D7AF
            if ((c >= '\u4E00' && c <= '\u9FFF') ||  // CJK Unified Ideographs
                (c >= '\u3400' && c <= '\u4DBF') ||  // CJK Extension A
                (c >= '\u3040' && c <= '\u309F') ||  // Hiragana
                (c >= '\u30A0' && c <= '\u30FF') ||  // Katakana
                (c >= '\uAC00' && c <= '\uD7AF'))    // Hangul
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cache.Dispose();
    }

    private bool IsDisposed() => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;
}
