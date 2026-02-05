using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Easydict.TranslationService;

/// <summary>
/// Configuration options for TranslationManager.
/// </summary>
public class TranslationManagerOptions
{
    /// <summary>
    /// Enable HTTP proxy for outbound requests.
    /// </summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>
    /// Proxy URI (e.g., "http://127.0.0.1:7890").
    /// </summary>
    public string? ProxyUri { get; set; }

    /// <summary>
    /// Bypass proxy for localhost addresses (important for Ollama).
    /// </summary>
    public bool ProxyBypassLocal { get; set; } = true;
}

/// <summary>
/// Manages translation services with caching, fallback, and retry support.
/// </summary>
public sealed class TranslationManager : IDisposable
{
    private readonly Dictionary<string, ITranslationService> _services = new();
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _cacheOptions;
    private readonly IMemoryCache _phoneticCache;
    private readonly MemoryCacheEntryOptions _phoneticCacheOptions;
    private readonly HttpClient _httpClient;

    private string _defaultServiceId = "google";

    public TranslationManager(TranslationManagerOptions? options = null)
    {
        var handler = new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                           System.Security.Authentication.SslProtocols.Tls13
        };

        // Configure proxy if enabled
        if (options?.ProxyEnabled == true && !string.IsNullOrWhiteSpace(options.ProxyUri))
        {
            if (Uri.TryCreate(options.ProxyUri, UriKind.Absolute, out var proxyUri))
            {
                var proxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = options.ProxyBypassLocal
                };
                handler.Proxy = proxy;
                handler.UseProxy = true;
                System.Diagnostics.Debug.WriteLine($"[TranslationManager] Proxy configured: {proxyUri.Host}:{proxyUri.Port}, BypassLocal={options.ProxyBypassLocal}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationManager] Invalid proxy URI: {options.ProxyUri}");
            }
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 1000 // Max 1000 cached translations
        });

        _cacheOptions = new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetSlidingExpiration(TimeSpan.FromHours(1))
            .SetAbsoluteExpiration(TimeSpan.FromDays(1));

        _phoneticCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 500 // Max 500 cached phonetic lookups
        });

        _phoneticCacheOptions = new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetSlidingExpiration(TimeSpan.FromHours(2))
            .SetAbsoluteExpiration(TimeSpan.FromDays(7));

        // Register default services
        RegisterService(new GoogleTranslateService(_httpClient));
        RegisterService(new GoogleWebTranslateService(_httpClient));
        RegisterService(new BingTranslateService(_httpClient));
        RegisterService(new DeepLService(_httpClient));
        RegisterService(new YoudaoService(_httpClient));

        // Register streaming LLM services
        RegisterService(new OpenAIService(_httpClient));
        RegisterService(new OllamaService(_httpClient));
        RegisterService(new BuiltInAIService(_httpClient));

        // Register additional LLM services (Phase 2)
        RegisterService(new DeepSeekService(_httpClient));
        RegisterService(new GroqService(_httpClient));
        RegisterService(new ZhipuService(_httpClient));
        RegisterService(new GitHubModelsService(_httpClient));
        RegisterService(new CustomOpenAIService(_httpClient));
        RegisterService(new GeminiService(_httpClient));

        // Register new translation services (Phase 3)
        RegisterService(new DoubaoService(_httpClient));
        RegisterService(new CaiyunService(_httpClient));
        RegisterService(new NiuTransService(_httpClient));
        RegisterService(new VolcanoService(_httpClient));
        RegisterService(new LingueeService(_httpClient));
    }

    /// <summary>
    /// All registered translation services.
    /// </summary>
    public IReadOnlyDictionary<string, ITranslationService> Services => _services;

    /// <summary>
    /// The default service ID to use for translation.
    /// </summary>
    public string DefaultServiceId
    {
        get => _defaultServiceId;
        set
        {
            if (!_services.ContainsKey(value))
                throw new ArgumentException($"Unknown service: {value}");
            _defaultServiceId = value;
        }
    }

    /// <summary>
    /// Register a translation service.
    /// </summary>
    public void RegisterService(ITranslationService service)
    {
        _services[service.ServiceId] = service;
    }

    /// <summary>
    /// Configure a service (e.g., set API key).
    /// </summary>
    public void ConfigureService(string serviceId, Action<ITranslationService> configure)
    {
        if (_services.TryGetValue(serviceId, out var service))
        {
            configure(service);
        }
    }

    /// <summary>
    /// Translate text using the default service.
    /// </summary>
    public Task<TranslationResult> TranslateAsync(
        string text,
        Language toLanguage,
        Language fromLanguage = Language.Auto,
        CancellationToken cancellationToken = default)
    {
        return TranslateAsync(new TranslationRequest
        {
            Text = text,
            ToLanguage = toLanguage,
            FromLanguage = fromLanguage
        }, cancellationToken);
    }

    /// <summary>
    /// Translate text using the specified or default service.
    /// Automatically enriches phonetics from Youdao if the result lacks target phonetics for word queries.
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default,
        string? serviceId = null)
    {
        serviceId ??= _defaultServiceId;

        if (!_services.TryGetValue(serviceId, out var service))
        {
            throw new TranslationException($"Unknown service: {serviceId}")
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = serviceId
            };
        }

        // Check cache first
        if (!request.BypassCache)
        {
            var cacheKey = GetCacheKey(request, serviceId);
            if (_cache.TryGetValue(cacheKey, out TranslationResult? cached) && cached != null)
            {
                return cached with { FromCache = true };
            }
        }

        // Perform translation with retry
        var result = await TranslateWithRetryAsync(service, request, cancellationToken);

        // Enrich phonetics if missing (only for word queries targeting English)
        result = await EnrichPhoneticsIfMissingAsync(result, request, cancellationToken);

        // Cache the result
        if (!request.BypassCache)
        {
            var cacheKey = GetCacheKey(request, serviceId);
            _cache.Set(cacheKey, result, _cacheOptions);
        }

        return result;
    }

    /// <summary>
    /// Enrich a translation result with phonetics from Youdao if the result lacks target phonetics.
    /// This is useful for streaming services that don't return phonetics, or for any service result
    /// that needs phonetic data. Only triggers when target language is English and result is a word/phrase.
    /// </summary>
    /// <param name="result">The translation result to potentially enrich.</param>
    /// <param name="request">The original translation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The original result with phonetics added, or unchanged if enrichment not needed/failed.</returns>
    public async Task<TranslationResult> EnrichPhoneticsIfMissingAsync(
        TranslationResult result,
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        // Only enrich when target language is English
        // US/UK phonetics are only meaningful for English words
        if (request.ToLanguage != Language.English)
            return result;

        // Only enrich if the translated text looks like a word/phrase (not a sentence)
        var translatedText = result.TranslatedText?.Trim();
        if (string.IsNullOrEmpty(translatedText) || !YoudaoService.IsWordQuery(translatedText))
            return result;

        // Only enrich if there are no target phonetics (US/UK/dest)
        var targetPhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        if (targetPhonetics.Count > 0)
            return result;

        // Check phonetic cache first
        var phoneticCacheKey = GetPhoneticCacheKey(translatedText);
        if (_phoneticCache.TryGetValue(phoneticCacheKey, out IReadOnlyList<Phonetic>? cachedPhonetics)
            && cachedPhonetics != null && cachedPhonetics.Count > 0)
        {
            return MergePhoneticsIntoResult(result, cachedPhonetics);
        }

        // Try to get phonetics from Youdao by looking up the TRANSLATED English text
        try
        {
            if (_services.TryGetValue("youdao", out var youdaoService))
            {
                // Create a request to look up the English translation in Youdao
                var phoneticRequest = new TranslationRequest
                {
                    Text = translatedText,
                    FromLanguage = Language.English,
                    ToLanguage = Language.SimplifiedChinese
                };

                var youdaoResult = await youdaoService.TranslateAsync(phoneticRequest, cancellationToken);
                var youdaoPhonetics = youdaoResult?.WordResult?.Phonetics;

                if (youdaoPhonetics != null && youdaoPhonetics.Count > 0)
                {
                    // Cache the phonetics for future use
                    _phoneticCache.Set(phoneticCacheKey, youdaoPhonetics, _phoneticCacheOptions);
                    return MergePhoneticsIntoResult(result, youdaoPhonetics);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort: swallow errors and return original result
            System.Diagnostics.Debug.WriteLine($"[TranslationManager] Phonetic enrichment failed: {ex.Message}");
        }

        return result;
    }

    private static TranslationResult MergePhoneticsIntoResult(
        TranslationResult result,
        IReadOnlyList<Phonetic> phoneticsToAdd)
    {
        var existingPhonetics = result.WordResult?.Phonetics?.ToList() ?? [];
        var mergedPhonetics = existingPhonetics.Concat(phoneticsToAdd).ToList();

        var newWordResult = new WordResult
        {
            Phonetics = mergedPhonetics,
            Definitions = result.WordResult?.Definitions,
            Examples = result.WordResult?.Examples
        };

        return result with { WordResult = newWordResult };
    }

    private static string GetPhoneticCacheKey(string englishWord)
    {
        return $"phonetic:{englishWord.ToLowerInvariant().Trim()}";
    }

    /// <summary>
    /// Check if a service supports streaming.
    /// </summary>
    public bool IsStreamingService(string serviceId)
    {
        return _services.TryGetValue(serviceId, out var service) &&
               service is IStreamTranslationService;
    }

    /// <summary>
    /// Get a streaming service by ID.
    /// </summary>
    public IStreamTranslationService? GetStreamingService(string serviceId)
    {
        if (_services.TryGetValue(serviceId, out var service) &&
            service is IStreamTranslationService streamService)
        {
            return streamService;
        }
        return null;
    }

    /// <summary>
    /// Stream translate text using the specified or default service.
    /// Falls back to non-streaming if service doesn't support streaming.
    /// Note: Streaming bypasses cache for real-time output.
    /// </summary>
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? serviceId = null)
    {
        serviceId ??= _defaultServiceId;

        if (!_services.TryGetValue(serviceId, out var service))
        {
            throw new TranslationException($"Unknown service: {serviceId}")
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = serviceId
            };
        }

        if (service is IStreamTranslationService streamService)
        {
            // Use streaming path
            await foreach (var chunk in streamService.TranslateStreamAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }
        else
        {
            // Fallback to non-streaming - yield entire result at once
            var result = await service.TranslateAsync(request, cancellationToken);
            yield return result.TranslatedText;
        }
    }

    private static async Task<TranslationResult> TranslateWithRetryAsync(
        ITranslationService service,
        TranslationRequest request,
        CancellationToken cancellationToken,
        int maxRetries = 2)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(request.TimeoutMs);

                return await service.TranslateAsync(request, cts.Token);
            }
            catch (TranslationException ex) when (ex.ErrorCode == TranslationErrorCode.RateLimited)
            {
                // Don't retry rate limit errors
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                lastException = ex;
                await Task.Delay(500 * (attempt + 1), cancellationToken); // Exponential backoff
            }
        }

        throw lastException ?? new TranslationException("Translation failed after retries");
    }

    private static string GetCacheKey(TranslationRequest request, string serviceId)
    {
        var raw = $"{serviceId}|{request.FromLanguage}|{request.ToLanguage}|{request.Text}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _phoneticCache.Dispose();
        _httpClient.Dispose();
    }
}

