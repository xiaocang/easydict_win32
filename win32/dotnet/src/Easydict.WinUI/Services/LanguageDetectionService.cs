using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly TranslationManager _translationManager;
    private readonly SettingsService _settings;
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _cacheOptions;

    public LanguageDetectionService(TranslationManager translationManager, SettingsService settings)
    {
        _translationManager = translationManager ?? throw new ArgumentNullException(nameof(translationManager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

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
    public async Task<Language> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Language.Auto;
        }

        // Don't detect very short text (unreliable)
        if (text.Length < 4)
        {
            Debug.WriteLine($"[Detection] Text too short ({text.Length} chars), skipping detection");
            return Language.Auto;
        }

        // Check cache
        var cacheKey = GetCacheKey(text);
        if (_cache.TryGetValue(cacheKey, out Language cached))
        {
            Debug.WriteLine($"[Detection] Cache hit: {cached}");
            return cached;
        }

        try
        {
            // Use Google Translate service for detection (no API key required)
            var googleService = _translationManager.Services.TryGetValue("google", out var service)
                ? service
                : null;

            if (googleService == null)
            {
                Debug.WriteLine("[Detection] Google service not available");
                return Language.Auto;
            }

            var detected = await googleService.DetectLanguageAsync(text, cancellationToken);

            // Cache the result
            _cache.Set(cacheKey, detected, _cacheOptions);

            Debug.WriteLine($"[Detection] Detected language: {detected.GetDisplayName()}");
            return detected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Detection] Failed: {ex.Message}");
            return Language.Auto; // Graceful degradation
        }
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
        _cache.Dispose();
        Debug.WriteLine("[Detection] Cache cleared");
    }

    /// <summary>
    /// Generate cache key for text (SHA256 hash).
    /// </summary>
    private static string GetCacheKey(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
