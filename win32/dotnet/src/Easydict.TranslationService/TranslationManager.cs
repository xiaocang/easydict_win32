using System.Security.Cryptography;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Easydict.TranslationService;

/// <summary>
/// Manages translation services with caching, fallback, and retry support.
/// </summary>
public sealed class TranslationManager : IDisposable
{
    private readonly Dictionary<string, ITranslationService> _services = new();
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _cacheOptions;
    private readonly HttpClient _httpClient;

    private string _defaultServiceId = "google";

    public TranslationManager()
    {
        _httpClient = new HttpClient
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

        // Register default services
        RegisterService(new GoogleTranslateService(_httpClient));
        RegisterService(new DeepLService(_httpClient));
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

        // Cache the result
        if (!request.BypassCache)
        {
            var cacheKey = GetCacheKey(request, serviceId);
            _cache.Set(cacheKey, result, _cacheOptions);
        }

        return result;
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
        _httpClient.Dispose();
    }
}

