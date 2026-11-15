using System.Net;
using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

public class DeepLServiceTests
{
    private readonly HttpClient _httpClient;
    private readonly DeepLService _service;

    public DeepLServiceTests()
    {
        _httpClient = new HttpClient();
        _service = new DeepLService(_httpClient);
    }

    [Fact]
    public async Task TranslateAsync_OneShotRequest_UsesCurrentProfileAndParsesTranslation()
    {
        var handler = new DeepLProtocolHandler(
            """{"resultCount":1,"results":[{"trackId":1552407475,"bundleId":"com.linguee.DeepLMobileTranslator","artistName":"DeepL SE","version":"26.58"}]}""",
            """{"translations":[{"text":"Hello, world!","detected_source_language":"ZH"}]}""");
        using var httpClient = new HttpClient(handler);
        var service = new DeepLService(httpClient, new DeepLAppVersionProvider(httpClient));
        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await service.TranslateAsync(request);

        result.TranslatedText.Should().Be("Hello, world!");
        result.DetectedLanguage.Should().Be(Language.SimplifiedChinese);
        handler.TranslationRequestUri.Should().Be(new Uri("https://oneshot-free.www.deepl.com/v1/translate"));
        handler.AuthorizationHeader.Should().Be("None");
        handler.UserAgentHeader.Should().Be("DeepL/26.58 CFNetwork/3826.600.41 Darwin/25.0.0");
        handler.OsVersionHeader.Should().Be("26.0");
        Guid.TryParse(handler.InstanceIdHeader, out _).Should().BeTrue();
        Guid.TryParse(handler.SessionIdHeader, out _).Should().BeTrue();

        using var body = JsonDocument.Parse(handler.TranslationRequestBody!);
        body.RootElement.GetProperty("text")[0].GetString().Should().Be("你好世界");
        body.RootElement.GetProperty("target_lang").GetString().Should().Be("en-US");
        body.RootElement.GetProperty("source_lang").GetString().Should().Be("zh");
        body.RootElement.GetProperty("usage_type").GetString().Should().Be("translate");
        body.RootElement.GetProperty("app_information").GetProperty("app_version").GetString().Should().Be("26.58");
    }

    [Fact]
    public async Task TranslateAsync_OneShotOutdatedClient_ThrowsServiceUnavailable()
    {
        var handler = new DeepLProtocolHandler(
            """{"resultCount":1,"results":[{"trackId":1552407475,"bundleId":"com.linguee.DeepLMobileTranslator","artistName":"DeepL SE","version":"26.58"}]}""",
            """{"title":"OutdatedClient","message":"Update the DeepL client."}""",
            HttpStatusCode.Forbidden);
        using var httpClient = new HttpClient(handler);
        var service = new DeepLService(httpClient, new DeepLAppVersionProvider(httpClient));
        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var exception = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(request));

        exception.Message.Should().Contain("client version is outdated");
        exception.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        exception.ServiceId.Should().Be("deepl");
    }

    [Fact]
    public void SupportedLanguages_ContainsTraditionalChinese()
    {
        _service.SupportedLanguages.Should().Contain(Language.TraditionalChinese);
    }

    [Fact]
    public void SupportsLanguagePair_TraditionalChineseToEnglish_ReturnsTrue()
    {
        _service.SupportsLanguagePair(Language.TraditionalChinese, Language.English)
            .Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_EnglishToTraditionalChinese_ReturnsTrue()
    {
        _service.SupportsLanguagePair(Language.English, Language.TraditionalChinese)
            .Should().BeTrue();
    }

    [Theory]
    // Regression set for #174 (initially missing) ...
    [InlineData(Language.Vietnamese)]
    [InlineData(Language.Arabic)]
    [InlineData(Language.Thai)]
    [InlineData(Language.Hebrew)]
    [InlineData(Language.Tamil)]
    [InlineData(Language.Telugu)]
    // ... plus the rest of DeepL's current (100+ language, next-gen model) support present in the enum.
    [InlineData(Language.Hindi)]
    [InlineData(Language.Bengali)]
    [InlineData(Language.Urdu)]
    [InlineData(Language.Malay)]
    [InlineData(Language.Filipino)]
    [InlineData(Language.Persian)]
    [InlineData(Language.Estonian)]
    [InlineData(Language.Latvian)]
    [InlineData(Language.Lithuanian)]
    [InlineData(Language.Slovak)]
    [InlineData(Language.Slovenian)]
    public void SupportedLanguages_ApiMode_ContainsDeepLSupportedLanguages(Language language)
    {
        // The official API supports the full next-gen set; an API key selects API mode.
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportedLanguages.Should().Contain(language);
    }

    private sealed class DeepLProtocolHandler(
        string appStoreResponse,
        string translationResponse,
        HttpStatusCode translationStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? TranslationRequestUri { get; private set; }
        public string? TranslationRequestBody { get; private set; }
        public string? AuthorizationHeader { get; private set; }
        public string? UserAgentHeader { get; private set; }
        public string? OsVersionHeader { get; private set; }
        public string? InstanceIdHeader { get; private set; }
        public string? SessionIdHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, appStoreResponse);
            }

            TranslationRequestUri = request.RequestUri;
            TranslationRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationHeader = ReadHeader(request, "Authorization");
            UserAgentHeader = request.Headers.UserAgent.ToString();
            OsVersionHeader = ReadHeader(request, "x-app-os-version");
            InstanceIdHeader = ReadHeader(request, "x-app-instance-id");
            SessionIdHeader = ReadHeader(request, "x-app-session-id");
            return JsonResponse(translationStatus, translationResponse);
        }

        private static string? ReadHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
            new(statusCode)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
    }

    [Fact]
    public void SupportedLanguages_ExcludesClassicalChinese()
    {
        // DeepL has no Classical/Literary Chinese target; excluded in both web and API mode.
        _service.SupportedLanguages.Should().NotContain(Language.ClassicalChinese);
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportedLanguages.Should().NotContain(Language.ClassicalChinese);
    }

    [Fact]
    public void SupportsLanguagePair_ApiMode_JapaneseToVietnamese_ReturnsTrue()
    {
        // The official API still supports this pair when API mode is explicitly selected.
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportsLanguagePair(Language.Japanese, Language.Vietnamese)
            .Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_ApiMode_EnglishToVietnamese_ReturnsTrue()
    {
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportsLanguagePair(Language.English, Language.Vietnamese)
            .Should().BeTrue();
    }

    [Theory]
    // These languages are not in the current one-shot target map and remain API-only.
    [InlineData(Language.Thai)]
    [InlineData(Language.Hindi)]
    public void SupportedLanguages_KeylessWebMode_ExcludesApiOnlyLanguages(Language language)
    {
        _service.SupportedLanguages.Should().NotContain(language);
    }

    [Fact]
    public void SupportedLanguages_KeylessWebMode_ContainsOneShotLanguages()
    {
        _service.SupportedLanguages.Should().Contain(Language.English);
        _service.SupportedLanguages.Should().Contain(Language.German);
        _service.SupportedLanguages.Should().Contain(Language.Japanese);
        _service.SupportedLanguages.Should().Contain(Language.Vietnamese);
        _service.SupportedLanguages.Should().Contain(Language.Arabic);
        _service.SupportedLanguages.Should().Contain(Language.Hebrew);
    }

    [Theory]
    [InlineData(Language.Vietnamese)]
    [InlineData(Language.Arabic)]
    [InlineData(Language.Hebrew)]
    public void SupportsLanguagePair_KeylessWebMode_IncludesOneShotLanguages(Language language)
    {
        _service.SupportsLanguagePair(Language.English, language).Should().BeTrue();
        _service.SupportsLanguagePair(language, Language.English).Should().BeTrue();
    }

    [Fact]
    public void ServiceId_IsDeepL()
    {
        _service.ServiceId.Should().Be("deepl");
    }

    [Fact]
    public void DisplayName_IsDeepL()
    {
        _service.DisplayName.Should().Be("DeepL");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        // Web mode doesn't require API key
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue()
    {
        // Web mode is always available
        _service.IsConfigured.Should().BeTrue();
    }
}
