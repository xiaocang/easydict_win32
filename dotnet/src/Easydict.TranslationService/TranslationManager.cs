using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;
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
    private const long TranslationCacheLimitKb = 8 * 1024;
    private const long PhoneticCacheLimitKb = 512;

    private const int DefaultMaxRetries = 2;

    // Without this the TCP handshake is bounded only by the OS (tens of seconds on Windows for a
    // black-holed address), so a host that swallows SYNs eats the whole request budget before
    // anything is reported.
    private static readonly TimeSpan DirectConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a connection to the user's configured proxy may take before it is called dead.
    /// </summary>
    /// <remarks>
    /// Shorter than <see cref="DirectConnectTimeout"/> on purpose. A proxy is one hop the user
    /// controls and usually runs nearby, and the fast path only engages if the connect gives up
    /// before the caller's own deadline — the shortest in the app is hover word lookup's 5 s, and
    /// a caller whose deadline fires first is indistinguishable from someone pressing Escape, so
    /// the failure would go back to being an unexplained timeout. Still above Windows' 3 s initial
    /// SYN retransmit, so a single lost packet does not fail the connect.
    /// </remarks>
    public static TimeSpan ProxiedConnectTimeout { get; } = TimeSpan.FromSeconds(4);

    // Copy-on-write registry: readers take the current snapshot without locking; writers replace
    // the whole dictionary under _servicesLock. Insertion order is preserved because a fresh copy
    // never contains holes, which the settings UI relies on for default display ordering.
    private volatile Dictionary<string, ITranslationService> _services = new();
    private readonly object _servicesLock = new();
    private readonly IMemoryCache _cache;
    private readonly IMemoryCache _phoneticCache;
    private readonly HttpMessageHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<Phonetic>?>>> _phoneticFlightTracker = new();

    private string _defaultServiceId = "google";

    public TranslationManager(TranslationManagerOptions? options = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = DirectConnectTimeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13
            }
        };

        // Configure proxy if enabled
        HttpMessageHandler pipeline = handler;
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
                handler.ConnectTimeout = ProxiedConnectTimeout;

                // Name the broken hop while the request URI is still in hand, so a proxy that is
                // down reads as a proxy problem instead of every service failing on its own.
                // SchemeAndServer rather than the authority: the latter keeps any "user:password@"
                // the user typed into the proxy URL, and this string is shown on a result card.
                pipeline = new ProxyFailureDetectingHandler(
                    handler,
                    proxy,
                    ProxyFailureClassifier.DescribeEndpoint(proxyUri));
                System.Diagnostics.Debug.WriteLine($"[TranslationManager] Proxy configured: {proxyUri.Host}:{proxyUri.Port}, BypassLocal={options.ProxyBypassLocal}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationManager] Invalid proxy URI: {options.ProxyUri}");
            }
        }

        _httpHandler = pipeline;
        _httpClient = new HttpClient(pipeline, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = TranslationCacheLimitKb
        });

        _phoneticCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = PhoneticCacheLimitKb
        });

        // Register default services
        RegisterService(new GoogleTranslateService(_httpClient));
        RegisterService(new GoogleWebTranslateService(_httpClient));
        RegisterService(new BingTranslateService(_httpClient));
        RegisterService(new DeepLService(_httpClient));
        RegisterService(new YoudaoService(_httpClient));

        // Register streaming LLM services
        RegisterService(new OpenAIService(_httpClient));
        RegisterService(new OllamaService(_httpClient));
        RegisterService(new OpenRouterService(_httpClient));
        RegisterService(new OrcaRouterService(_httpClient));

        // Register additional LLM services (Phase 2)
        RegisterService(new DeepSeekService(_httpClient));
        RegisterService(new GroqService(_httpClient));
        RegisterService(new ZhipuService(_httpClient));
        RegisterService(new KimiService(_httpClient));
        RegisterService(new GitHubModelsService(_httpClient));
        RegisterService(new CustomOpenAIService(_httpClient));
        RegisterService(new GeminiService(_httpClient));

        // Register new translation services (Phase 3)
        RegisterService(new DoubaoService(_httpClient));
        RegisterService(new CaiyunService(_httpClient));
        RegisterService(new NiuTransService(_httpClient));
        RegisterService(new VolcanoService(_httpClient));

        // Register local agent CLI services (subscription-based, no API key)
        RegisterService(new ClaudeCodeService(_httpClient));
        RegisterService(new CodexCliService(_httpClient));
#if ENABLE_LINGUEE_SERVICE
        RegisterService(new LingueeService(_httpClient));
#endif
    }

    /// <summary>
    /// All registered translation services.
    /// </summary>
    public IReadOnlyDictionary<string, ITranslationService> Services => _services;

    /// <summary>
    /// The shared HttpClient configured with proxy settings.
    /// Used to create isolated service instances for testing.
    /// </summary>
    public HttpClient SharedHttpClient => _httpClient;

    /// <summary>
    /// Create an additional <see cref="HttpClient"/> that shares this manager's proxy-configured
    /// handler but has its own timeout. Intended for adapters (plugins, long-running streams)
    /// that must not inherit the 30 s timeout of <see cref="SharedHttpClient"/>. The caller owns
    /// the returned client; disposing it does not dispose the shared handler.
    /// </summary>
    public HttpClient CreateSharedHandlerClient(TimeSpan timeout)
    {
        return new HttpClient(_httpHandler, disposeHandler: false)
        {
            Timeout = timeout
        };
    }

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
        ArgumentNullException.ThrowIfNull(service);

        lock (_servicesLock)
        {
            var snapshot = new Dictionary<string, ITranslationService>(_services)
            {
                [service.ServiceId] = service
            };
            _services = snapshot;
        }
    }

    /// <summary>
    /// Unregister a translation service by its ID.
    /// </summary>
    public bool UnregisterService(string serviceId)
    {
        lock (_servicesLock)
        {
            if (!_services.ContainsKey(serviceId))
            {
                return false;
            }

            var snapshot = new Dictionary<string, ITranslationService>(_services);
            snapshot.Remove(serviceId);
            _services = snapshot;
            return true;
        }
    }

    /// <summary>
    /// The execution policy the host honors for a service. Services that do not implement
    /// <see cref="IServiceExecutionPolicyProvider"/> (and unknown ids) get <see cref="ServiceExecutionPolicy.Default"/>.
    /// </summary>
    public ServiceExecutionPolicy GetExecutionPolicy(string serviceId)
    {
        return _services.TryGetValue(serviceId, out var service)
            ? GetExecutionPolicy(service)
            : ServiceExecutionPolicy.Default;
    }

    private static ServiceExecutionPolicy GetExecutionPolicy(ITranslationService service)
    {
        return (service as IServiceExecutionPolicyProvider)?.ExecutionPolicy ?? ServiceExecutionPolicy.Default;
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
    /// Automatically enriches phonetics from Youdao if the result lacks an English pronunciation for word queries.
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

        var policy = GetExecutionPolicy(service);

        // Check cache first (only when both the request and the service's policy allow it)
        var cacheKey = !request.BypassCache && policy.AllowResultCache
            ? GetCacheKey(request, serviceId, service)
            : null;
        if (cacheKey is not null
            && _cache.TryGetValue(cacheKey, out TranslationResult? cached) && cached != null)
        {
            return cached with { FromCache = true };
        }

        // Perform translation with retry (a conservative policy gets exactly one attempt)
        var result = await TranslateWithRetryAsync(
            service, request, cancellationToken,
            maxRetries: policy.AllowHostRetry ? DefaultMaxRetries : 0);

        // Enrich phonetics if missing (word queries with an English side, either direction)
        if (policy.AllowPhoneticEnrichment)
        {
            result = await EnrichPhoneticsIfMissingAsync(result, request, cancellationToken);
        }

        // Cache the result
        if (cacheKey is not null)
        {
            _cache.Set(cacheKey, result, CreateTranslationCacheOptions(cacheKey, request, result));
        }

        return result;
    }

    /// <summary>
    /// Enrich a translation result with an English pronunciation from Youdao when it has none.
    /// This is useful for streaming services that don't return phonetics, or for any service result
    /// that needs phonetic data. Triggers for word queries in either direction: the English side is
    /// the translation when translating into English and the original text when translating out of it.
    /// </summary>
    /// <param name="result">The translation result to potentially enrich.</param>
    /// <param name="request">The original translation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="serviceId">
    /// Optional id of the service that produced <paramref name="result"/>. When given, the service's
    /// <see cref="ServiceExecutionPolicy.AllowPhoneticEnrichment"/> is honored and the result is
    /// returned unchanged if enrichment is not allowed.
    /// </param>
    /// <returns>The original result with phonetics added, or unchanged if enrichment not needed/failed.</returns>
    public async Task<TranslationResult> EnrichPhoneticsIfMissingAsync(
        TranslationResult result,
        TranslationRequest request,
        CancellationToken cancellationToken = default,
        string? serviceId = null)
    {
        // Respect the producing service's policy (plugins may opt out of host-initiated network calls)
        if (serviceId is not null && !GetExecutionPolicy(serviceId).AllowPhoneticEnrichment)
            return result;

        // Resolve which text the US/UK phonetics would describe. A pronunciation belongs to
        // the English word being looked up, whether the user typed it (en→zh) or it came back
        // as the translation (zh→en), so enrichment runs in both directions.
        var englishWord = ResolveEnglishWordForEnrichment(result, request);
        if (string.IsNullOrEmpty(englishWord))
            return result;

        // Only enrich if that text looks like a word/phrase (not a sentence)
        if (!WordQueryHeuristics.IsWordQuery(englishWord))
            return result;

        // Only enrich if there is no English pronunciation yet. A romanization such as
        // Google's pinyin ("src"/"dest") is not a pronunciation guide and must not suppress this.
        if (PhoneticDisplayHelper.GetEnglishPhonetics(result).Count > 0)
            return result;

        // Check phonetic cache first
        var phoneticCacheKey = GetPhoneticCacheKey(englishWord);
        if (_phoneticCache.TryGetValue(phoneticCacheKey, out IReadOnlyList<Phonetic>? cachedPhonetics)
            && cachedPhonetics != null && cachedPhonetics.Count > 0)
        {
            return MergePhoneticsIntoResult(result, cachedPhonetics);
        }

        // Deduplicate concurrent requests for the same word using flight tracker.
        // When multiple streaming services finish near-simultaneously for the same English word,
        // only one Youdao API call is made; the others await the same in-flight task.
        //
        // The shared task uses CancellationToken.None so that one caller's cancellation
        // doesn't kill the fetch for all waiters. Each caller applies its own token via WaitAsync.
        var lazyTask = _phoneticFlightTracker.GetOrAdd(
            phoneticCacheKey,
            _ => new Lazy<Task<IReadOnlyList<Phonetic>?>>(
                () => FetchPhoneticsAsync(englishWord, CancellationToken.None)));

        try
        {
            var phonetics = await lazyTask.Value.WaitAsync(cancellationToken);

            if (phonetics != null && phonetics.Count > 0)
            {
                _phoneticCache.Set(phoneticCacheKey, phonetics, CreatePhoneticCacheOptions(phoneticCacheKey, phonetics));
                return MergePhoneticsIntoResult(result, phonetics);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller was cancelled, but the shared fetch continues for other waiters
            return result;
        }
        catch (Exception ex)
        {
            // Best-effort: swallow errors and return original result
            System.Diagnostics.Debug.WriteLine($"[TranslationManager] Phonetic enrichment failed: {ex.Message}");
        }
        finally
        {
            // Only remove from tracker when the shared task has completed (not when a single caller cancels).
            // This prevents a cancelled caller from evicting the entry while others are still waiting.
            if (lazyTask.IsValueCreated && lazyTask.Value.IsCompleted)
            {
                _phoneticFlightTracker.TryRemove(phoneticCacheKey, out _);
            }
        }

        return result;
    }

    /// <summary>
    /// Picks the English text whose pronunciation should be looked up for this result,
    /// or null when neither side of the translation is English.
    /// </summary>
    private static string? ResolveEnglishWordForEnrichment(
        TranslationResult result, TranslationRequest request)
    {
        // Translating into English: the English word is the translation.
        if (request.ToLanguage == Language.English)
            return result.TranslatedText?.Trim();

        // Translating out of English: the English word is what the user asked about.
        var sourceLanguage = request.FromLanguage == Language.Auto
            ? result.DetectedLanguage
            : request.FromLanguage;

        if (sourceLanguage == Language.English)
            return request.Text?.Trim();

        // No script-based guess here. Latin letters are not evidence of English, and the
        // homographs are exactly the words a dictionary lookup would answer confidently and
        // wrongly: French "chat" or German "gift" would come back with the English
        // pronunciation and get merged into the result. An unresolved language means unknown,
        // so no pronunciation is fetched.
        return null;
    }

    /// <summary>
    /// Fetch phonetics for an English word from the Youdao service.
    /// </summary>
    private async Task<IReadOnlyList<Phonetic>?> FetchPhoneticsAsync(
        string englishWord, CancellationToken cancellationToken)
    {
        if (!_services.TryGetValue("youdao", out var youdaoService))
            return null;

        var request = new TranslationRequest
        {
            Text = englishWord,
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var youdaoResult = await youdaoService.TranslateAsync(request, cancellationToken);
        return youdaoResult?.WordResult?.Phonetics;
    }

    private static TranslationResult MergePhoneticsIntoResult(
        TranslationResult result,
        IReadOnlyList<Phonetic> phoneticsToAdd)
    {
        var existingPhonetics = result.WordResult?.Phonetics?.ToList() ?? [];
        var mergedPhonetics = existingPhonetics.Concat(phoneticsToAdd).ToList();

        // WithPhonetics preserves every other dictionary field (definitions, examples, word forms,
        // synonyms) so host post-processing never discards data a service returned.
        return result with { WordResult = result.WordResult.WithPhonetics(mergedPhonetics) };
    }

    private static string GetPhoneticCacheKey(string englishWord)
    {
        return $"phonetic:{englishWord.ToLowerInvariant().Trim()}";
    }

    private static MemoryCacheEntryOptions CreateTranslationCacheOptions(
        string cacheKey,
        TranslationRequest request,
        TranslationResult result)
    {
        var bytes = EstimateUtf16Bytes(cacheKey)
            + EstimateUtf16Bytes(request.Text)
            + EstimateUtf16Bytes(result.OriginalText)
            + EstimateUtf16Bytes(result.TranslatedText)
            + EstimateUtf16Bytes(result.ServiceName)
            + EstimateUtf16Bytes(result.InfoMessage)
            + EstimateUtf16Bytes(result.RawHtml);

        bytes += EstimateStringsUtf16Bytes(result.Alternatives);
        if (result.WordResult is not null)
        {
            bytes += EstimatePhoneticsUtf16Bytes(result.WordResult.Phonetics);
            bytes += EstimateDefinitionsUtf16Bytes(result.WordResult.Definitions);
            bytes += EstimateStringsUtf16Bytes(result.WordResult.Examples);
            bytes += EstimateWordFormsUtf16Bytes(result.WordResult.WordForms);
            bytes += EstimateSynonymsUtf16Bytes(result.WordResult.Synonyms);
        }

        return new MemoryCacheEntryOptions()
            .SetSize(ToCacheKilobytes(bytes))
            .SetSlidingExpiration(TimeSpan.FromHours(1))
            .SetAbsoluteExpiration(TimeSpan.FromDays(1));
    }

    private static MemoryCacheEntryOptions CreatePhoneticCacheOptions(
        string cacheKey,
        IReadOnlyList<Phonetic> phonetics)
    {
        var bytes = EstimateUtf16Bytes(cacheKey) + EstimatePhoneticsUtf16Bytes(phonetics);
        return new MemoryCacheEntryOptions()
            .SetSize(ToCacheKilobytes(bytes))
            .SetSlidingExpiration(TimeSpan.FromHours(2))
            .SetAbsoluteExpiration(TimeSpan.FromDays(7));
    }

    private static long ToCacheKilobytes(long bytes)
    {
        return Math.Max(1, (bytes + 1023) / 1024);
    }

    private static long EstimateUtf16Bytes(string? value)
    {
        return string.IsNullOrEmpty(value) ? 0 : value.Length * sizeof(char);
    }

    private static long EstimateStringsUtf16Bytes(IEnumerable<string>? values)
    {
        return values?.Sum(EstimateUtf16Bytes) ?? 0;
    }

    private static long EstimatePhoneticsUtf16Bytes(IEnumerable<Phonetic>? values)
    {
        if (values is null) return 0;
        long bytes = 0;
        foreach (var value in values)
        {
            bytes += EstimateUtf16Bytes(value.Text)
                + EstimateUtf16Bytes(value.AudioUrl)
                + EstimateUtf16Bytes(value.Accent);
        }

        return bytes;
    }

    private static long EstimateDefinitionsUtf16Bytes(IEnumerable<Definition>? values)
    {
        if (values is null) return 0;
        long bytes = 0;
        foreach (var value in values)
        {
            bytes += EstimateUtf16Bytes(value.PartOfSpeech)
                + EstimateStringsUtf16Bytes(value.Meanings);
        }

        return bytes;
    }

    private static long EstimateWordFormsUtf16Bytes(IEnumerable<WordForm>? values)
    {
        if (values is null) return 0;
        long bytes = 0;
        foreach (var value in values)
        {
            bytes += EstimateUtf16Bytes(value.Name)
                + EstimateUtf16Bytes(value.Value);
        }

        return bytes;
    }

    private static long EstimateSynonymsUtf16Bytes(IEnumerable<Synonym>? values)
    {
        if (values is null) return 0;
        long bytes = 0;
        foreach (var value in values)
        {
            bytes += EstimateUtf16Bytes(value.PartOfSpeech)
                + EstimateUtf16Bytes(value.Meaning)
                + EstimateStringsUtf16Bytes(value.Words);
        }

        return bytes;
    }

    /// <summary>
    /// Clear cached translation results without affecting service registration or phonetic caches.
    /// </summary>
    public void ClearTranslationCache()
    {
        try
        {
            (_cache as MemoryCache)?.Compact(1.0);
            System.Diagnostics.Debug.WriteLine("[TranslationManager] Translation cache cleared");
        }
        catch (ObjectDisposedException)
        {
            // Ignore if cache is already disposed
        }
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
    /// Note: the streaming branch bypasses the cache for real-time output; the non-streaming
    /// fallback goes through <see cref="TranslateAsync(TranslationRequest, CancellationToken, string?)"/>
    /// and is cached like any other non-streaming query.
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
            // Use streaming path. Enumerated by hand so a proxy failure is reported as one here
            // too: a caller of this API would otherwise see a generic network error and could not
            // show the proxy hint. A catch cannot wrap a loop body that yields.
            var enumerator = streamService.TranslateStreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        !cancellationToken.IsCancellationRequested
                        && ProxyFailureClassifier.FindProxyFailure(ex) is not null)
                    {
                        throw DescribeProxyFailure(ex, service.ServiceId)!;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            // Fallback to non-streaming - yield entire result at once. Routed through the manager,
            // not the service, so this path gets the same policy, retry and proxy relabelling as
            // TranslateStreamUpdatesAsync's equivalent branch; calling the service directly left a
            // dead proxy reported as a generic network error.
            var result = await TranslateAsync(request, cancellationToken, serviceId).ConfigureAwait(false);
            yield return result.TranslatedText;
        }
    }

    /// <summary>
    /// Stream translation updates using the specified or default service.
    /// Rich services (<see cref="IRichStreamTranslationService"/>) are passed through, so their
    /// structured final result arrives in the same execution; legacy streaming services are wrapped
    /// as <see cref="TranslationStreamUpdate.TextDelta"/>; non-streaming services yield a single
    /// <see cref="TranslationStreamUpdate.Completed"/>.
    /// Caching on this path is opt-in: only services that declare an explicit
    /// <see cref="ServiceExecutionPolicy"/> allowing it get cache reads and writes here; legacy
    /// streaming services keep the historical "streaming bypasses cache" behavior.
    /// </summary>
    public async IAsyncEnumerable<TranslationStreamUpdate> TranslateStreamUpdatesAsync(
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

        var cacheKey = !request.BypassCache
            && service is IServiceExecutionPolicyProvider provider
            && provider.ExecutionPolicy.AllowResultCache
            ? GetCacheKey(request, serviceId, service)
            : null;
        if (cacheKey is not null
            && _cache.TryGetValue(cacheKey, out TranslationResult? cached) && cached != null)
        {
            yield return new TranslationStreamUpdate.Completed(cached with { FromCache = true });
            yield break;
        }

        TranslationResult? completed = null;
        if (service is IRichStreamTranslationService richService)
        {
            var allowRetry = service is IServiceExecutionPolicyProvider richPolicyProvider
                && richPolicyProvider.ExecutionPolicy.AllowHostRetry;

            for (var attempt = 0; ; attempt++)
            {
                var retry = false;
                var enumerator = richService.TranslateStreamUpdatesAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (
                            !cancellationToken.IsCancellationRequested
                            && ProxyFailureClassifier.FindProxyFailure(ex) is not null)
                        {
                            // Same reasoning as the non-streaming path: a dead proxy is the
                            // user's to fix, and retrying it just prolongs an empty card.
                            throw DescribeProxyFailure(ex, service.ServiceId)!;
                        }
                        catch (TranslationException ex) when (
                            allowRetry && attempt < DefaultMaxRetries && !IsNonRetryable(ex.ErrorCode))
                        {
                            retry = true;
                            break;
                        }
                        catch (Exception) when (
                            allowRetry && attempt < DefaultMaxRetries && !cancellationToken.IsCancellationRequested)
                        {
                            retry = true;
                            break;
                        }

                        if (!moved)
                        {
                            break;
                        }

                        var update = enumerator.Current;
                        if (update is TranslationStreamUpdate.Completed done)
                        {
                            completed = done.Result;
                        }

                        yield return update;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (!retry)
                {
                    break;
                }

                // Reset any partially-streamed text before retrying, matching TranslateWithRetryAsync's backoff.
                await Task.Delay(500 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                yield return new TranslationStreamUpdate.TextSnapshot(string.Empty);
            }
        }
        else if (service is IStreamTranslationService streamService)
        {
            // Enumerated by hand rather than with await foreach so a proxy failure can be
            // re-labelled; a catch cannot wrap a loop body that yields.
            var enumerator = streamService.TranslateStreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        !cancellationToken.IsCancellationRequested
                        && ProxyFailureClassifier.FindProxyFailure(ex) is not null)
                    {
                        throw DescribeProxyFailure(ex, service.ServiceId)!;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    yield return new TranslationStreamUpdate.TextDelta(enumerator.Current);
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            // TranslateAsync already applies policy, retry and caching for non-streaming services.
            var result = await TranslateAsync(request, cancellationToken, serviceId).ConfigureAwait(false);
            cacheKey = null;
            yield return new TranslationStreamUpdate.Completed(result);
        }

        if (cacheKey is not null
            && completed is { ResultKind: TranslationResultKind.Success, FromCache: false })
        {
            _cache.Set(cacheKey, completed, CreateTranslationCacheOptions(cacheKey, request, completed));
        }
    }

    private static async Task<TranslationResult> TranslateWithRetryAsync(
        ITranslationService service,
        TranslationRequest request,
        CancellationToken cancellationToken,
        int maxRetries = DefaultMaxRetries)
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
            catch (TranslationException ex) when (IsNonRetryable(ex.ErrorCode))
            {
                // Rate limits and configuration errors do not get better by retrying
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User-initiated cancellation — propagate immediately without retry
                throw;
            }
            catch (Exception ex) when (ProxyFailureClassifier.FindProxyFailure(ex) is not null)
            {
                // The configured proxy never answered, so nothing reached the service. Retrying
                // multiplies the silence in front of a message only the user can act on.
                throw DescribeProxyFailure(ex, service.ServiceId)!;
            }
            catch (OperationCanceledException oce)
            {
                // Per-request timeout: the linked CTS fired but the user did not cancel.
                // Wrap as Timeout and retry (or surface on final attempt).
                lastException = new TranslationException("Request timed out", oce)
                {
                    ErrorCode = TranslationErrorCode.Timeout,
                    ServiceId = service.ServiceId
                };
                if (attempt >= maxRetries)
                    throw (TranslationException)lastException;
                await Task.Delay(500 * (attempt + 1), cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                lastException = ex;
                await Task.Delay(500 * (attempt + 1), cancellationToken); // Exponential backoff
            }
        }

        throw lastException ?? new TranslationException("Translation failed after retries");
    }

    /// <summary>
    /// Re-labels a failure that a <see cref="ProxyFailureDetectingHandler"/> already attributed to
    /// the configured proxy, so the UI can say which hop is broken instead of showing the same
    /// generic network error on every service. Returns null for every other failure.
    /// </summary>
    /// <remarks>
    /// Public because not every caller reaches a service through this manager: the grammar
    /// correction flows enumerate <c>IGrammarCorrectionService</c> directly, and without this they
    /// would report a dead proxy as an unexplained error.
    /// </remarks>
    public static TranslationException? DescribeProxyFailure(Exception exception, string serviceId)
    {
        var proxyFailure = ProxyFailureClassifier.FindProxyFailure(exception);
        if (proxyFailure is null)
        {
            return null;
        }

        if (exception is TranslationException { ErrorCode: TranslationErrorCode.ProxyError } alreadyDescribed)
        {
            return alreadyDescribed;
        }

        return new TranslationException(proxyFailure.Message, exception)
        {
            ErrorCode = TranslationErrorCode.ProxyError,
            ServiceId = serviceId
        };
    }

    /// <summary>
    /// Error codes that describe a stable condition (bad key, unsupported pair, oversized text,
    /// missing model) rather than a transient failure. Retrying them only delays the error.
    /// </summary>
    private static bool IsNonRetryable(TranslationErrorCode code)
    {
        return code is TranslationErrorCode.RateLimited
            or TranslationErrorCode.ProxyError
            or TranslationErrorCode.InvalidApiKey
            or TranslationErrorCode.UnsupportedLanguage
            or TranslationErrorCode.TextTooLong
            or TranslationErrorCode.InvalidModel
            or TranslationErrorCode.LocalModelNeedsPreparation;
    }

    private static string GetCacheKey(TranslationRequest request, string serviceId, ITranslationService? service)
    {
        // CustomPrompt changes LLM output; the discriminator captures service-side configuration
        // (plugin version, options) that is not part of the request. OriginalText and
        // DetectedFromLanguage are both passed straight through to a Bob plugin
        // (BobTranslationService.BuildQueryJson) and can change its output even when Text/From/To
        // are identical. All of it must separate cache entries.
        var discriminator = (service as ICacheKeyDiscriminatorProvider)?.CacheKeyDiscriminator;
        var raw = $"{serviceId}|{request.FromLanguage}|{request.ToLanguage}|{request.Text}|{request.CustomPrompt}|{request.OriginalText}|{request.DetectedFromLanguage}|{discriminator}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    public void Dispose()
    {
        _phoneticFlightTracker.Clear();
        _cache.Dispose();
        _phoneticCache.Dispose();
        _httpClient.Dispose();
        _httpHandler.Dispose();
    }
}

