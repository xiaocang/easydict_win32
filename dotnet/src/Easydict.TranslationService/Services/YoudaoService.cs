using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Youdao translation and dictionary service.
/// Supports both free web API (no key required) and official OpenAPI (with AppKey/AppSecret).
/// Dictionary mode provides US/UK phonetics, definitions, and examples for English words.
/// </summary>
public sealed class YoudaoService : BaseTranslationService
{
    // Updated to use jsonapi_s with jsonversion=4 (matches tisfeng/Easydict implementation)
    private const string WebDictEndpoint = "https://dict.youdao.com/jsonapi_s";
    private const string WebTranslateEndpoint = "https://fanyi.youdao.com/translate_o";
    private const string OpenApiEndpoint = "https://openapi.youdao.com/api";
    private const string DictVoiceBaseUrl = "https://dict.youdao.com/dictvoice?audio=";

    private static readonly IReadOnlyList<Language> _youdaoLanguages =
    [
        Language.Auto,
        Language.SimplifiedChinese, Language.TraditionalChinese, Language.English,
        Language.Japanese, Language.Korean, Language.French, Language.Spanish,
        Language.Portuguese, Language.Italian, Language.German, Language.Russian,
        Language.Arabic, Language.Swedish, Language.Thai, Language.Dutch,
        Language.Indonesian, Language.Vietnamese, Language.Hindi
    ];

    private string _appKey = "";
    private string _appSecret = "";
    private bool _useOfficialApi;

    public YoudaoService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "youdao";
    public override string DisplayName => "Youdao";
    public override bool RequiresApiKey => false; // Web mode doesn't require API key
    public override bool IsConfigured => true; // Web mode is always available
    public override IReadOnlyList<Language> SupportedLanguages => _youdaoLanguages;

    /// <summary>
    /// Configure the Youdao service.
    /// </summary>
    /// <param name="appKey">Optional Youdao AppKey for official API.</param>
    /// <param name="appSecret">Optional Youdao AppSecret for official API.</param>
    /// <param name="useOfficialApi">If true and keys provided, use official API; otherwise use web API.</param>
    public void Configure(string? appKey, string? appSecret, bool useOfficialApi = false)
    {
        _appKey = appKey ?? "";
        _appSecret = appSecret ?? "";
        _useOfficialApi = useOfficialApi;
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        // Use official API if configured
        if (_useOfficialApi && !string.IsNullOrEmpty(_appKey) && !string.IsNullOrEmpty(_appSecret))
        {
            try
            {
                return await TranslateWithOpenApiAsync(request, cancellationToken);
            }
            catch (TranslationException)
            {
                // Re-throw TranslationException to preserve error codes
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Youdao] Official API failed: {ex.Message}, falling back to web");
                // Fall through to web API for non-translation errors
            }
        }

        // Use web API: dictionary for words, translate for sentences
        if (IsWordQuery(request.Text))
        {
            try
            {
                return await TranslateWithWebDictAsync(request, cancellationToken);
            }
            catch (TranslationException)
            {
                // Re-throw TranslationException to preserve error codes
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Youdao] Web dict failed: {ex.Message}, falling back to translate");
                // Fall back to web translate for non-translation errors
            }
        }

        return await TranslateWithWebAsync(request, cancellationToken);
    }

    /// <summary>
    /// Check if query text looks like a single word or short phrase suitable for dictionary lookup.
    /// This is public so TranslationManager can use it for phonetic enrichment triggering.
    /// </summary>
    public static bool IsWordQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        // Too long for a typical dictionary word
        if (trimmed.Length > 50)
            return false;

        // Contains line breaks or sentence-ending punctuation (indicates a sentence, not a word)
        if (trimmed.Contains('\n') || trimmed.Contains('.') || trimmed.Contains('!') || trimmed.Contains('?'))
            return false;

        // For English: letters, hyphens, apostrophes, spaces
        // For other languages: allow more characters but keep it short
        var wordChars = trimmed.Count(c => char.IsLetter(c) || c == '-' || c == '\'' || c == ' ');
        return wordChars >= trimmed.Length * 0.8;
    }

    /// <summary>
    /// Translate using Youdao web dictionary API (provides phonetics and definitions).
    /// Uses POST to jsonapi_s endpoint with sign authentication (matching tisfeng/Easydict).
    /// </summary>
    private async Task<TranslationResult> TranslateWithWebDictAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        // Compute sign parameters (algorithm from tisfeng/Easydict)
        var ww = request.Text + "webdict";
        var time = ww.Length % 10;
        var salt = ComputeMd5(ww);
        var key = "Mk6hqtUp33DGGtoS63tTJbMUYjRrG1Lu";
        var sign = ComputeMd5($"web{request.Text}{time}{key}{salt}");

        // Determine foreign language code (only en/ja/fr/ko supported for dict lookup)
        var le = GetDictForeignLanguage(request);

        var url = $"{WebDictEndpoint}?doctype=json&jsonversion=4";

        var formData = new Dictionary<string, string>
        {
            { "q", request.Text },
            { "le", le },
            { "client", "web" },
            { "t", time.ToString() },
            { "sign", sign },
            { "keyfrom", "webdict" }
        };

        using var content = new FormUrlEncodedContent(formData);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        httpRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        httpRequest.Headers.Add("Referer", "https://dict.youdao.com/");

        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException($"Youdao dict API error: {response.StatusCode}")
            {
                ErrorCode = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? TranslationErrorCode.RateLimited
                    : TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseWebDictResponse(json, request);
    }

    /// <summary>
    /// Determine the foreign language code for dictionary lookup.
    /// Only en/ja/fr/ko are supported for Youdao dict API.
    /// </summary>
    private static string GetDictForeignLanguage(TranslationRequest request)
    {
        // If from Chinese, use target language; otherwise use source language
        var targetLang = request.FromLanguage switch
        {
            Language.SimplifiedChinese or Language.TraditionalChinese => request.ToLanguage,
            _ => request.FromLanguage
        };

        return targetLang switch
        {
            Language.English => "en",
            Language.Japanese => "ja",
            Language.French => "fr",
            Language.Korean => "ko",
            _ => "en"  // Default to English
        };
    }

    /// <summary>
    /// Compute MD5 hash for web dict sign calculation.
    /// </summary>
    private static string ComputeMd5(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Translate using Youdao web translate API (simple translation, no dictionary data).
    /// </summary>
    private async Task<TranslationResult> TranslateWithWebAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var fromCode = GetYoudaoLanguageCode(request.FromLanguage);
        var toCode = GetYoudaoLanguageCode(request.ToLanguage);

        var formData = new Dictionary<string, string>
        {
            { "i", request.Text },
            { "from", fromCode },
            { "to", toCode },
            { "client", "fanyideskweb" },
            { "keyfrom", "fanyi.web" }
        };

        using var content = new FormUrlEncodedContent(formData);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, WebTranslateEndpoint)
        {
            Content = content
        };
        httpRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        httpRequest.Headers.Add("Referer", "https://fanyi.youdao.com/");

        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException($"Youdao translate API error: {response.StatusCode}")
            {
                ErrorCode = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? TranslationErrorCode.RateLimited
                    : TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseWebTranslateResponse(json, request);
    }

    /// <summary>
    /// Translate using Youdao official OpenAPI (requires AppKey and AppSecret).
    /// </summary>
    private async Task<TranslationResult> TranslateWithOpenApiAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var salt = Guid.NewGuid().ToString();
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fromCode = GetYoudaoLanguageCode(request.FromLanguage);
        var toCode = GetYoudaoLanguageCode(request.ToLanguage);

        // Truncate input for signature (per Youdao API v3 spec)
        var input = request.Text.Length <= 20
            ? request.Text
            : request.Text[..10] + request.Text.Length + request.Text[^10..];

        var sign = ComputeSign(_appKey, input, salt, curtime, _appSecret);

        var formData = new Dictionary<string, string>
        {
            { "q", request.Text },
            { "from", fromCode },
            { "to", toCode },
            { "appKey", _appKey },
            { "salt", salt },
            { "sign", sign },
            { "signType", "v3" },
            { "curtime", curtime }
        };

        using var content = new FormUrlEncodedContent(formData);
        using var response = await HttpClient.PostAsync(OpenApiEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationException($"Youdao OpenAPI error: {response.StatusCode}")
            {
                ErrorCode = response.StatusCode == HttpStatusCode.Unauthorized
                    ? TranslationErrorCode.InvalidApiKey
                    : TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOpenApiResponse(json, request);
    }

    private TranslationResult ParseWebDictResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract phonetics (US/UK)
        // Handle both object and array formats for simple.word (API may return either)
        List<Phonetic>? phonetics = null;
        if (root.TryGetProperty("simple", out var simple) &&
            simple.TryGetProperty("word", out var wordElement))
        {
            // Normalize: if word is an array, use the first element
            var word = wordElement.ValueKind == JsonValueKind.Array && wordElement.GetArrayLength() > 0
                ? wordElement[0]
                : wordElement;

            phonetics = ExtractPhoneticsFromWord(word);
        }

        // Also try ec.word for phonetics if simple.word didn't have them
        if ((phonetics == null || phonetics.Count == 0) &&
            root.TryGetProperty("ec", out var ecForPhonetics) &&
            ecForPhonetics.TryGetProperty("word", out var ecWordElement))
        {
            var ecWord = ecWordElement.ValueKind == JsonValueKind.Array && ecWordElement.GetArrayLength() > 0
                ? ecWordElement[0]
                : ecWordElement;

            phonetics = ExtractPhoneticsFromWord(ecWord);
        }

        // Extract definitions by part of speech
        // Handle both object and array formats for ec.word (API may return either)
        List<Definition>? definitions = null;
        if (root.TryGetProperty("ec", out var ec) &&
            ec.TryGetProperty("word", out var ecWordForDef))
        {
            // Normalize: if word is an array, use the first element
            var ecWordObj = ecWordForDef.ValueKind == JsonValueKind.Array && ecWordForDef.GetArrayLength() > 0
                ? ecWordForDef[0]
                : ecWordForDef;

            if (ecWordObj.TryGetProperty("trs", out var trs) &&
                trs.ValueKind == JsonValueKind.Array)
            {
                definitions = [];
                foreach (var tr in trs.EnumerateArray())
                {
                    if (tr.TryGetProperty("pos", out var pos) &&
                        tr.TryGetProperty("tran", out var tran))
                    {
                        var partOfSpeech = pos.GetString();
                        var meaning = tran.GetString();
                        
                        if (!string.IsNullOrEmpty(meaning))
                        {
                            definitions.Add(new Definition
                            {
                                PartOfSpeech = partOfSpeech,
                                Meanings = [meaning]
                            });
                        }
                    }
                }
            }
        }

        // Build translated text from definitions
        var translatedText = request.Text;
        if (definitions?.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var def in definitions.Take(3))
            {
                if (!string.IsNullOrEmpty(def.PartOfSpeech))
                    sb.Append($"{def.PartOfSpeech} ");
                sb.AppendLine(string.Join("; ", def.Meanings ?? []));
            }
            translatedText = sb.ToString().TrimEnd();
        }

        WordResult? wordResult = null;
        if (phonetics?.Count > 0 || definitions?.Count > 0)
        {
            wordResult = new WordResult
            {
                Phonetics = phonetics?.Count > 0 ? phonetics : null,
                Definitions = definitions?.Count > 0 ? definitions : null
            };
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = request.FromLanguage == Language.Auto ? Language.English : request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
            WordResult = wordResult
        };
    }

    private TranslationResult ParseWebTranslateResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var translatedText = request.Text;
        if (root.TryGetProperty("translateResult", out var translateResult) &&
            translateResult.ValueKind == JsonValueKind.Array &&
            translateResult.GetArrayLength() > 0)
        {
            var sb = new StringBuilder();
            foreach (var paragraph in translateResult.EnumerateArray())
            {
                if (paragraph.ValueKind == JsonValueKind.Array)
                {
                    foreach (var segment in paragraph.EnumerateArray())
                    {
                        if (segment.TryGetProperty("tgt", out var tgt))
                        {
                            var text = tgt.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                sb.Append(text);
                            }
                        }
                    }
                }
            }
            if (sb.Length > 0)
            {
                translatedText = sb.ToString();
            }
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
    }

    private TranslationResult ParseOpenApiResponse(string json, TranslationRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check error code
        if (root.TryGetProperty("errorCode", out var errorCode))
        {
            var code = errorCode.GetString();
            if (code != "0")
            {
                throw new TranslationException($"Youdao API error: {code}")
                {
                    ErrorCode = code switch
                    {
                        "401" or "108" => TranslationErrorCode.InvalidApiKey,
                        "411" => TranslationErrorCode.RateLimited,
                        _ => TranslationErrorCode.ServiceUnavailable
                    },
                    ServiceId = ServiceId
                };
            }
        }

        // Extract translated text
        var translatedText = request.Text;
        if (root.TryGetProperty("translation", out var translation) &&
            translation.ValueKind == JsonValueKind.Array &&
            translation.GetArrayLength() > 0)
        {
            translatedText = string.Join(" ", translation.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrEmpty(s)));
        }

        // Extract phonetics and definitions from basic section
        List<Phonetic>? phonetics = null;
        List<Definition>? definitions = null;

        if (root.TryGetProperty("basic", out var basic))
        {
            // Phonetics
            if (basic.TryGetProperty("us-phonetic", out var usPhonetic))
            {
                var usText = usPhonetic.GetString();
                if (!string.IsNullOrEmpty(usText))
                {
                    phonetics ??= [];
                    string? audioUrl = null;
                    
                    if (basic.TryGetProperty("us-speech", out var usSpeech))
                    {
                        audioUrl = usSpeech.GetString();
                    }
                    
                    phonetics.Add(new Phonetic { Text = usText, Accent = "US", AudioUrl = audioUrl });
                }
            }

            if (basic.TryGetProperty("uk-phonetic", out var ukPhonetic))
            {
                var ukText = ukPhonetic.GetString();
                if (!string.IsNullOrEmpty(ukText))
                {
                    phonetics ??= [];
                    string? audioUrl = null;
                    
                    if (basic.TryGetProperty("uk-speech", out var ukSpeech))
                    {
                        audioUrl = ukSpeech.GetString();
                    }
                    
                    phonetics.Add(new Phonetic { Text = ukText, Accent = "UK", AudioUrl = audioUrl });
                }
            }

            // Definitions
            if (basic.TryGetProperty("explains", out var explains) &&
                explains.ValueKind == JsonValueKind.Array)
            {
                definitions = [];
                foreach (var explain in explains.EnumerateArray())
                {
                    var text = explain.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Try to parse "n. meaning" format
                        var parts = text.Split(new[] { ". " }, 2, StringSplitOptions.None);
                        if (parts.Length == 2 && parts[0].Length <= 10)
                        {
                            definitions.Add(new Definition
                            {
                                PartOfSpeech = parts[0],
                                Meanings = [parts[1]]
                            });
                        }
                        else
                        {
                            definitions.Add(new Definition
                            {
                                PartOfSpeech = null,
                                Meanings = [text]
                            });
                        }
                    }
                }
            }
        }

        // Detect source language
        var detectedLang = request.FromLanguage;
        if (request.FromLanguage == Language.Auto && root.TryGetProperty("l", out var langPair))
        {
            var pair = langPair.GetString() ?? "";
            var fromCode = pair.Split(new[] { '2' }, 2)[0];
            detectedLang = LanguageCodeFromYoudao(fromCode);
        }

        WordResult? wordResult = null;
        if (phonetics?.Count > 0 || definitions?.Count > 0)
        {
            wordResult = new WordResult
            {
                Phonetics = phonetics?.Count > 0 ? phonetics : null,
                Definitions = definitions?.Count > 0 ? definitions : null
            };
        }

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = detectedLang,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
            WordResult = wordResult
        };
    }

    /// <summary>
    /// Extract US/UK phonetics from a word JSON element.
    /// </summary>
    private static List<Phonetic>? ExtractPhoneticsFromWord(JsonElement word)
    {
        var phonetics = new List<Phonetic>();

        if (word.TryGetProperty("usphone", out var usphone))
        {
            var usText = usphone.GetString();
            if (!string.IsNullOrEmpty(usText))
            {
                string? audioUrl = null;
                
                // Add audio URL if available
                if (word.TryGetProperty("usspeech", out var usspeech))
                {
                    var audioPath = usspeech.GetString();
                    if (!string.IsNullOrEmpty(audioPath))
                    {
                        audioUrl = DictVoiceBaseUrl + HttpUtility.UrlEncode(audioPath);
                    }
                }
                
                phonetics.Add(new Phonetic { Text = usText, Accent = "US", AudioUrl = audioUrl });
            }
        }

        if (word.TryGetProperty("ukphone", out var ukphone))
        {
            var ukText = ukphone.GetString();
            if (!string.IsNullOrEmpty(ukText))
            {
                string? audioUrl = null;
                
                // Add audio URL if available
                if (word.TryGetProperty("ukspeech", out var ukspeech))
                {
                    var audioPath = ukspeech.GetString();
                    if (!string.IsNullOrEmpty(audioPath))
                    {
                        audioUrl = DictVoiceBaseUrl + HttpUtility.UrlEncode(audioPath);
                    }
                }
                
                phonetics.Add(new Phonetic { Text = ukText, Accent = "UK", AudioUrl = audioUrl });
            }
        }

        return phonetics.Count > 0 ? phonetics : null;
    }

    /// <summary>
    /// Compute Youdao OpenAPI v3 signature.
    /// sign = sha256(appKey + input + salt + curtime + appSecret)
    /// </summary>
    private static string ComputeSign(string appKey, string input, string salt, string curtime, string appSecret)
    {
        var signStr = appKey + input + salt + curtime + appSecret;
        var bytes = Encoding.UTF8.GetBytes(signStr);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetYoudaoLanguageCode(Language language)
    {
        return language switch
        {
            Language.Auto => "auto",
            Language.SimplifiedChinese => "zh-CHS",
            Language.TraditionalChinese => "zh-CHT",
            Language.English => "en",
            Language.Japanese => "ja",
            Language.Korean => "ko",
            Language.French => "fr",
            Language.Spanish => "es",
            Language.Portuguese => "pt",
            Language.Italian => "it",
            Language.German => "de",
            Language.Russian => "ru",
            Language.Arabic => "ar",
            Language.Swedish => "sv",
            Language.Thai => "th",
            Language.Dutch => "nl",
            Language.Indonesian => "id",
            Language.Vietnamese => "vi",
            Language.Hindi => "hi",
            _ => "en"
        };
    }

    private static Language LanguageCodeFromYoudao(string code)
    {
        return code switch
        {
            "zh-CHS" => Language.SimplifiedChinese,
            "zh-CHT" => Language.TraditionalChinese,
            "en" => Language.English,
            "ja" => Language.Japanese,
            "ko" => Language.Korean,
            "fr" => Language.French,
            "es" => Language.Spanish,
            "pt" => Language.Portuguese,
            "it" => Language.Italian,
            "de" => Language.German,
            "ru" => Language.Russian,
            "ar" => Language.Arabic,
            "sv" => Language.Swedish,
            "th" => Language.Thai,
            "nl" => Language.Dutch,
            "id" => Language.Indonesian,
            "vi" => Language.Vietnamese,
            "hi" => Language.Hindi,
            _ => Language.Auto
        };
    }

    protected override string GetLanguageCode(Language language)
    {
        return GetYoudaoLanguageCode(language);
    }
}
