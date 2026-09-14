using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Easydict.BobPlugin.Mapping;
using Easydict.BobPlugin.Package;
using Easydict.BobPlugin.Runtime;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Streaming;

namespace Easydict.BobPlugin.Adapter;

/// <summary>
/// Presents one installed Bob plugin as an Easydict translation service.
///
/// Always a rich streaming service: whether a plugin streams is only known at run time, and the
/// rich channel covers both shapes while delivering the structured result (dictionary data and
/// all) in the same execution, so the host never has to call the plugin twice.
///
/// The plugin's execution policy is enforced by <see cref="TranslationManager"/>, not here; this
/// class only declares it.
/// </summary>
public sealed class BobTranslationService
    : ITranslationService,
      IRichStreamTranslationService,
      IServiceExecutionPolicyProvider,
      ICacheKeyDiscriminatorProvider,
      IServiceOriginProvider,
      IDisposable
{
    private readonly BobPluginInstanceDescriptor _descriptor;
    private readonly HttpClient _httpClient;
    private readonly RingBufferLogger _logger;
    private readonly SemaphoreSlim _callGate = new(1, 1);

    private BobScriptHost? _host;
    private bool _disposed;

    public BobTranslationService(
        BobPluginInstanceDescriptor descriptor,
        HttpClient httpClient,
        IBobHostLogger? logger = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = new RingBufferLogger(logger ?? new DebugBobHostLogger(descriptor.ServiceId));

        Origin = new ServiceOrigin(
            ServiceOriginKind.Plugin,
            "Bob",
            $"{descriptor.PluginIdentifier} v{descriptor.Version}");
        SupportedLanguages = ResolveSupportedLanguages(descriptor.SupportedLanguageCodes);
    }

    public string ServiceId => _descriptor.ServiceId;

    /// <summary>
    /// The plugin's own name. The "third-party plugin" marking is carried by <see cref="Origin"/>
    /// rather than baked into the name, so every surface can present it consistently.
    /// </summary>
    public string DisplayName => _descriptor.DisplayName;

    public bool RequiresApiKey => _descriptor.SecureOptionIds.Count > 0;

    public bool IsConfigured => _descriptor.SecureOptionIds.All(id =>
        _descriptor.OptionValues.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value));

    public IReadOnlyList<Language> SupportedLanguages { get; }

    public bool IsStreaming => true;

    public ServiceExecutionPolicy ExecutionPolicy => _descriptor.Policy;

    public ServiceOrigin Origin { get; }

    /// <summary>
    /// Separates cache entries per plugin version and option set, so changing a prompt or key never
    /// replays a result produced by the previous configuration.
    /// </summary>
    public string? CacheKeyDiscriminator
    {
        get
        {
            var builder = new StringBuilder(_descriptor.Version);
            foreach (var (key, value) in _descriptor.OptionValues.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                builder.Append('|').Append(key).Append('=').Append(value);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return $"{_descriptor.Version}|{Convert.ToHexString(hash)[..16]}";
        }
    }

    /// <summary>Recent plugin and host log lines, shown by the settings page when a plugin misbehaves.</summary>
    public IReadOnlyList<string> RecentLogLines => _logger.Snapshot();

    public bool SupportsLanguagePair(Language from, Language to)
    {
        if (SupportedLanguages.Count == 0)
        {
            return true;
        }

        return (from == Language.Auto || SupportedLanguages.Contains(from)) && SupportedLanguages.Contains(to);
    }

    /// <summary>Plugins do not expose language detection; the host detects before dispatching.</summary>
    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        TranslationResult? completed = null;
        await foreach (var update in TranslateStreamUpdatesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (update is TranslationStreamUpdate.Completed done)
            {
                completed = done.Result;
            }
        }

        return completed ?? throw new TranslationException("The plugin finished without returning a result.")
        {
            ErrorCode = TranslationErrorCode.InvalidResponse,
            ServiceId = ServiceId
        };
    }

    public IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        => StreamUpdateAdapter.ToDeltas(TranslateStreamUpdatesAsync(request, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<TranslationStreamUpdate> TranslateStreamUpdatesAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Plugins keep module-level state, so calls into one instance are serialized.
        await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
            var timeout = await ResolveTimeoutAsync(host, request, cancellationToken).ConfigureAwait(false);
            var queryJson = BuildQueryJson(request);

            TranslationResult? completed = null;
            await foreach (var callEvent in host.CallTranslateAsync(queryJson, timeout, cancellationToken).ConfigureAwait(false))
            {
                var envelope = Parse(callEvent.PayloadJson);

                if (!callEvent.IsCompletion)
                {
                    // Bob streams the accumulated text, so each partial replaces what came before.
                    var partial = JoinParagraphs(envelope?.Result?.ToParagraphs);
                    if (partial is not null)
                    {
                        yield return new TranslationStreamUpdate.TextSnapshot(partial);
                    }

                    continue;
                }

                completed = MapCompletion(envelope, request);
                yield return new TranslationStreamUpdate.Completed(completed);
            }

            if (completed is null)
            {
                throw new TranslationException("The plugin finished without returning a result.")
                {
                    ErrorCode = TranslationErrorCode.InvalidResponse,
                    ServiceId = ServiceId
                };
            }
        }
        finally
        {
            _callGate.Release();
        }
    }

    /// <summary>Language codes the plugin declares right now (used when installing or updating it).</summary>
    public async Task<IReadOnlyList<string>> GetSupportedLanguageCodesAsync(CancellationToken cancellationToken = default)
    {
        var host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
        return await host.GetSupportedLanguageCodesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run the plugin's optional self-check.</summary>
    public async Task<BobValidationOutcome> ValidateAsync(CancellationToken cancellationToken = default)
    {
        await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var host = await GetHostAsync(cancellationToken).ConfigureAwait(false);
            if (!await host.HasEntryPointAsync("pluginValidate", cancellationToken).ConfigureAwait(false))
            {
                return BobValidationOutcome.NotSupported;
            }

            await foreach (var callEvent in host
                .CallValidateAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false))
            {
                if (!callEvent.IsCompletion)
                {
                    continue;
                }

                var envelope = Parse(callEvent.PayloadJson);
                if (envelope?.Error is { } error)
                {
                    return new BobValidationOutcome(BobValidationStatus.Invalid, BobErrorMapper.MapToException(error, ServiceId).Message);
                }

                // Bob's pluginValidate reports success as `{ result: true }`.
                return BobValidationOutcome.Valid;
            }

            return BobValidationOutcome.NotSupported;
        }
        finally
        {
            _callGate.Release();
        }
    }

    private async Task<BobScriptHost> GetHostAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Created lazily so installing a plugin never costs a JavaScript engine at startup.
        var host = _host ??= new BobScriptHost(
            BobPluginPackage.LoadFromDirectory(_descriptor.InstallDirectory),
            _descriptor.SandboxDirectory,
            _descriptor.OptionValues,
            _descriptor.SliceTimeoutSeconds,
            _httpClient,
            _logger);

        await host.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return host;
    }

    /// <summary>A plugin's declared timeout wins over the request's, since it knows its own backend.</summary>
    private static async Task<TimeSpan> ResolveTimeoutAsync(BobScriptHost host, TranslationRequest request, CancellationToken cancellationToken)
    {
        var declared = await host.GetDeclaredTimeoutAsync(cancellationToken).ConfigureAwait(false);
        return declared ?? TimeSpan.FromMilliseconds(request.TimeoutMs);
    }

    private string BuildQueryJson(TranslationRequest request)
    {
        var from = BobLanguageMap.ToBobCode(request.FromLanguage) ?? BobLanguageMap.AutoCode;
        var to = BobLanguageMap.ToBobCode(request.ToLanguage)
            ?? throw new TranslationException($"The plugin has no language code for {request.ToLanguage}.")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId
            };

        // Plugins generally expect a concrete detectFrom; fall back to a script guess when the host
        // has not detected anything, because "auto" makes some plugins refuse the request.
        var detected = request.DetectedFromLanguage ?? request.FromLanguage;
        var detectFrom = detected == Language.Auto
            ? BobLanguageMap.GuessByScript(request.Text)
            : BobLanguageMap.ToBobCode(detected) ?? BobLanguageMap.GuessByScript(request.Text);

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["text"] = request.Text,
            ["originalText"] = request.OriginalText ?? request.Text,
            ["from"] = from,
            ["to"] = to,
            ["detectFrom"] = detectFrom,
            ["detectTo"] = to
        });
    }

    /// <summary>Turn the plugin's final payload into a result, or throw for a real failure.</summary>
    private TranslationResult MapCompletion(BobEnvelope? envelope, TranslationRequest request)
    {
        if (envelope?.Error is { } error)
        {
            // "Nothing found" is an outcome, not a failure: dictionary and lookup plugins rely on it.
            return BobErrorMapper.TryMapToNoResult(error, request, DisplayName)
                ?? throw BobErrorMapper.MapToException(error, ServiceId);
        }

        if (envelope?.Result is null)
        {
            throw new TranslationException("The plugin returned an empty payload.")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }

        return BobResultMapper.Map(envelope.Result, request, DisplayName);
    }

    private BobEnvelope? Parse(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<BobEnvelope>(payloadJson, BobJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.Log("error", $"The plugin returned malformed JSON: {ex.Message}");
            throw new TranslationException("The plugin returned a malformed payload.", ex)
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }
    }

    private static string? JoinParagraphs(List<string>? paragraphs)
    {
        if (paragraphs is null || paragraphs.Count == 0)
        {
            return null;
        }

        return string.Join("\n", paragraphs.Where(p => p is not null));
    }

    private static IReadOnlyList<Language> ResolveSupportedLanguages(IReadOnlyList<string> codes)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var languages = new List<Language>();
        foreach (var code in codes)
        {
            var language = BobLanguageMap.FromBobCode(code);
            if (language is { } resolved && resolved != Language.Auto && !languages.Contains(resolved))
            {
                languages.Add(resolved);
            }
        }

        return languages;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host?.Dispose();
        _host = null;
        _callGate.Dispose();
    }
}
