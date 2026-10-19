using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Google Translate service using the free GTX API.
/// No API key required.
/// </summary>
public sealed class GoogleTranslateService : BaseTranslationService
{
    private const string BaseUrl = "https://translate.googleapis.com/translate_a/single";

    // 429 fallback: the endpoint used by the Google Translate web frontend, called with the
    // public key embedded in that frontend (wt_lib). Like gtx itself, this endpoint and key are
    // undocumented and unsupported by Google and may change or stop working at any time, so the
    // fallback is strictly best-effort. If 429s reappear, refresh the key from
    // LunaTranslator's translator/google.py (github.com/HIllya51/LunaTranslator).
    private const string WebFallbackUrl = "https://translate-pa.googleapis.com/v1/translateHtml";
    private const string WebFallbackApiKey = "AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520";

    // After a gtx 429, skip gtx for a while and go straight to the web endpoint, so a throttled
    // client does not send two requests per query. Retry-After is honoured, within a cap.
    internal static readonly TimeSpan DefaultGtxCooldown = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaxGtxCooldown = TimeSpan.FromMinutes(5);

    private static readonly Regex BrTagRegex = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TimeProvider _timeProvider;

    // UTC ticks until which gtx is skipped; 0 when not throttled. Accessed via Interlocked.
    private long _gtxCooldownUntilTicks;

    private static readonly IReadOnlyList<Language> _googleLanguages =
    [
        Language.SimplifiedChinese, Language.TraditionalChinese, Language.English,
        Language.Japanese, Language.Korean, Language.French, Language.Spanish,
        Language.Portuguese, Language.Italian, Language.German, Language.Russian,
        Language.Arabic, Language.Swedish, Language.Romanian, Language.Thai,
        Language.Dutch, Language.Hungarian, Language.Greek, Language.Danish,
        Language.Finnish, Language.Polish, Language.Czech, Language.Turkish,
        Language.Ukrainian, Language.Bulgarian, Language.Indonesian, Language.Malay,
        Language.Vietnamese, Language.Persian, Language.Hindi, Language.Telugu,
        Language.Tamil, Language.Urdu, Language.Filipino, Language.Bengali,
        Language.Norwegian, Language.Hebrew
    ];

    public GoogleTranslateService(HttpClient httpClient) : this(httpClient, TimeProvider.System)
    {
    }

    internal GoogleTranslateService(HttpClient httpClient, TimeProvider timeProvider) : base(httpClient)
    {
        _timeProvider = timeProvider;
    }

    public override string ServiceId => "google";
    public override string DisplayName => "Google Translate";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => true;
    public override IReadOnlyList<Language> SupportedLanguages => _googleLanguages;

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var sourceCode = request.FromLanguage == Language.Auto
            ? "auto"
            : GetLanguageCode(request.FromLanguage);
        var targetCode = GetLanguageCode(request.ToLanguage);

        if (IsGtxCoolingDown())
        {
            // gtx returned 429 recently: don't hit it again until the cooldown expires
            return await TryTranslateViaWebEndpointAsync(request, sourceCode, targetCode, cancellationToken)
                ?? throw CreateRateLimitedException();
        }

        var url = BuildUrl(request.Text, sourceCode, targetCode);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(httpRequest);
        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                StartGtxCooldown(response);
                return await TryTranslateViaWebEndpointAsync(request, sourceCode, targetCode, cancellationToken)
                    ?? throw CreateRateLimitedException();
            }

            throw new TranslationException($"Google API error: {response.StatusCode}")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse(json, request);
    }

    public override async Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!IsGtxCoolingDown())
        {
            var url = BuildUrl(text, "auto", "en");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(httpRequest);
            using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseDetectedLanguage(json);
            }

            StartGtxCooldown(response);
        }

        // gtx is throttled: the web endpoint also reports the detected source language
        var fallback = await TryCallWebEndpointAsync(text, "auto", "en", cancellationToken);
        if (fallback?.Detected is { } detected && detected != Language.Auto)
        {
            return detected;
        }

        throw new HttpRequestException(
            $"Google API error: {HttpStatusCode.TooManyRequests}", null, HttpStatusCode.TooManyRequests);
    }

    private static Language ParseDetectedLanguage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // With dj=1, response is an object with "src" property
        if (root.TryGetProperty("src", out var srcElement) &&
            srcElement.ValueKind == JsonValueKind.String)
        {
            var detectedCode = srcElement.GetString() ?? "en";
            return LanguageCodes.FromIso639(detectedCode);
        }

        return Language.Auto;
    }

    private bool IsGtxCoolingDown() =>
        _timeProvider.GetUtcNow().UtcTicks < Interlocked.Read(ref _gtxCooldownUntilTicks);

    private void StartGtxCooldown(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow();
        var retryAfter = response.Headers.RetryAfter;
        var cooldown = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - now : DefaultGtxCooldown);
        if (cooldown <= TimeSpan.Zero)
        {
            cooldown = DefaultGtxCooldown;
        }
        else if (cooldown > MaxGtxCooldown)
        {
            cooldown = MaxGtxCooldown;
        }

        Interlocked.Exchange(ref _gtxCooldownUntilTicks, (now + cooldown).UtcTicks);
        Debug.WriteLine($"[Google] gtx returned 429; using the web endpoint for {cooldown.TotalSeconds:F0}s.");
    }

    private TranslationException CreateRateLimitedException() =>
        new($"Google API error: {HttpStatusCode.TooManyRequests}")
        {
            ErrorCode = TranslationErrorCode.RateLimited,
            ServiceId = ServiceId
        };

    private static string BuildUrl(string text, string sourceCode, string targetCode)
    {
        var encodedText = HttpUtility.UrlEncode(text);

        return $"{BaseUrl}?client=gtx&sl={sourceCode}&tl={targetCode}" +
               $"&dt=t&dt=bd&dt=at&dt=ex&dt=ld&dt=md&dt=qca&dt=rw&dt=rm&dt=ss" +
               $"&dj=1&ie=UTF-8&oe=UTF-8&q={encodedText}";
    }

    private TranslationResult ParseResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract translated text from sentences array
        var sb = new StringBuilder();
        if (root.TryGetProperty("sentences", out var sentences))
        {
            foreach (var sentence in sentences.EnumerateArray())
            {
                if (sentence.TryGetProperty("trans", out var trans))
                {
                    var part = trans.GetString();
                    if (!string.IsNullOrEmpty(part))
                    {
                        sb.Append(part);
                    }
                }
            }
        }
        var translatedText = sb.ToString();

        // Get detected source language
        var detectedLang = Language.Auto;
        if (root.TryGetProperty("src", out var srcElement))
        {
            var srcCode = srcElement.GetString() ?? "";
            detectedLang = LanguageCodes.FromIso639(srcCode);
        }

        // Extract phonetics from src_translit (source romanization)
        var phonetics = ExtractPhonetics(root);

        // Get alternatives if available
        List<string>? alternatives = null;
        if (root.TryGetProperty("alternative_translations", out var altTrans))
        {
            alternatives = [];
            foreach (var alt in altTrans.EnumerateArray())
            {
                if (alt.TryGetProperty("alternative", out var altArray))
                {
                    foreach (var item in altArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("word_postproc", out var word))
                        {
                            var altText = word.GetString();
                            if (!string.IsNullOrEmpty(altText) && altText != translatedText)
                                alternatives.Add(altText);
                        }
                    }
                }
            }
        }

        // Build WordResult if we have phonetics
        WordResult? wordResult = null;
        if (phonetics != null)
        {
            wordResult = new WordResult
            {
                Phonetics = phonetics
            };
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = detectedLang,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
            Alternatives = alternatives?.Count > 0 ? alternatives : null,
            WordResult = wordResult
        };
    }

    /// <summary>
    /// Extract phonetic transliterations from the dj=1 response.
    /// The sentences array may contain entries with src_translit (source romanization)
    /// and translit (target romanization).
    /// </summary>
    private static List<Phonetic>? ExtractPhonetics(JsonElement root)
    {
        if (!root.TryGetProperty("sentences", out var sentences))
            return null;

        StringBuilder? srcBuilder = null;
        StringBuilder? translitBuilder = null;

        foreach (var sentence in sentences.EnumerateArray())
        {
            if (sentence.TryGetProperty("src_translit", out var srcT))
            {
                var text = srcT.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    (srcBuilder ??= new StringBuilder()).Append(text);
                }
            }

            if (sentence.TryGetProperty("translit", out var t))
            {
                var text = t.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    (translitBuilder ??= new StringBuilder()).Append(text);
                }
            }
        }

        string? srcTranslit = srcBuilder?.Length > 0 ? srcBuilder.ToString() : null;
        string? translit = translitBuilder?.Length > 0 ? translitBuilder.ToString() : null;

        if (srcTranslit == null && translit == null)
            return null;

        var phonetics = new List<Phonetic>();

        if (!string.IsNullOrEmpty(srcTranslit))
        {
            phonetics.Add(new Phonetic { Text = srcTranslit, Accent = "src" });
        }

        if (!string.IsNullOrEmpty(translit))
        {
            phonetics.Add(new Phonetic { Text = translit, Accent = "dest" });
        }

        return phonetics.Count > 0 ? phonetics : null;
    }

    /// <summary>
    /// 429 fallback for translation: plain text only (no dictionary/phonetics).
    /// Returns null when the web endpoint fails; the caller then reports the gtx rate limit.
    /// </summary>
    private async Task<TranslationResult?> TryTranslateViaWebEndpointAsync(
        TranslationRequest request,
        string sourceCode,
        string targetCode,
        CancellationToken cancellationToken)
    {
        var fallback = await TryCallWebEndpointAsync(request.Text, sourceCode, targetCode, cancellationToken);
        if (fallback is not { } result)
        {
            return null;
        }

        return new TranslationResult
        {
            TranslatedText = result.Text,
            OriginalText = request.Text,
            DetectedLanguage = result.Detected,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
    }

    /// <summary>
    /// POST to the endpoint the Google Translate web frontend uses (same approach as
    /// LunaTranslator's translator/google.py). Detected is read from root[1][0] when the
    /// endpoint reports it. Returns null on any failure except cancellation/timeout
    /// (rethrown so the caller's retry policy applies).
    /// </summary>
    private async Task<(string Text, Language Detected)?> TryCallWebEndpointAsync(
        string text,
        string sourceCode,
        string targetCode,
        CancellationToken cancellationToken)
    {
        try
        {
            // JSON+protobuf body of the web client: [[[[html], sl, tl], "wt_lib"]]
            var payload = JsonSerializer.Serialize(new object[]
            {
                new object[] { new object[] { EncodeForTranslateHtml(text) }, sourceCode, targetCode },
                "wt_lib"
            });

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, WebFallbackUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf")
            };
            httpRequest.Headers.Add("X-Goog-Api-Key", WebFallbackApiKey);
            AddBrowserHeaders(httpRequest);

            using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[Google] Web endpoint fallback failed: HTTP {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array ||
                root.GetArrayLength() == 0 ||
                root[0].ValueKind != JsonValueKind.Array)
            {
                Debug.WriteLine($"[Google] Web endpoint fallback failed: unexpected response shape ({root.ValueKind})");
                return null;
            }

            // The endpoint returns HTML; concatenate all chunks so long inputs are not truncated
            var sb = new StringBuilder();
            foreach (var chunk in root[0].EnumerateArray())
            {
                if (chunk.ValueKind == JsonValueKind.String)
                {
                    sb.Append(chunk.GetString());
                }
            }

            var translated = DecodeFromTranslateHtml(sb.ToString());
            if (string.IsNullOrEmpty(translated))
            {
                Debug.WriteLine("[Google] Web endpoint fallback failed: empty translation");
                return null;
            }

            // root[1][0] carries the detected source language (verified live with sl=auto)
            var detected = Language.Auto;
            if (root.GetArrayLength() > 1 &&
                root[1].ValueKind == JsonValueKind.Array &&
                root[1].GetArrayLength() > 0 &&
                root[1][0].ValueKind == JsonValueKind.String)
            {
                detected = LanguageCodes.FromIso639(root[1][0].GetString() ?? "");
            }

            return (translated, detected);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't log the message for HTTP errors: it may contain request details
            Debug.WriteLine($"[Google] Web endpoint fallback failed: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// translateHtml parses its input as HTML: escape markup characters so text such as
    /// "a &lt; b" or "AT&amp;T" survives, and turn line breaks into &lt;br&gt; so they are kept.
    /// </summary>
    internal static string EncodeForTranslateHtml(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\r\n", "<br>")
            .Replace("\r", "<br>")
            .Replace("\n", "<br>");

    /// <summary>
    /// Reverse of <see cref="EncodeForTranslateHtml"/>: &lt;br&gt; back to "\n", then decode entities.
    /// </summary>
    internal static string DecodeFromTranslateHtml(string html) =>
        WebUtility.HtmlDecode(BrTagRegex.Replace(html, "\n"));
}
