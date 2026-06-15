using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for DeepL's dynamic supported-language refresh (fetch DeepL's official /v2/languages list
/// and union it onto the baseline) and the strict code → Language mapper.
/// </summary>
public class DeepLServiceLanguageRefreshTests
{
    // Sample shape of a DeepL GET /v2/languages?type=target response.
    private const string TargetLanguagesJson = """
        [
          {"language":"EN-US","name":"English (American)","supports_formality":false},
          {"language":"DE","name":"German","supports_formality":true},
          {"language":"ZH-HANT","name":"Chinese (traditional)","supports_formality":false},
          {"language":"VI","name":"Vietnamese","supports_formality":false}
        ]
        """;

    private static DeepLService CreateService(MockHttpMessageHandler handler) =>
        new(new HttpClient(handler));

    [Fact]
    public async Task RefreshSupportedLanguagesAsync_WithoutApiKey_DoesNotFetch()
    {
        var handler = new MockHttpMessageHandler();
        var service = CreateService(handler);
        var baselineCount = service.SupportedLanguages.Count;

        // No Configure() => no API key => the languages endpoint must not be called.
        await service.RefreshSupportedLanguagesAsync();

        handler.Requests.Should().BeEmpty();
        service.SupportedLanguages.Count.Should().Be(baselineCount);
    }

    [Fact]
    public async Task RefreshSupportedLanguagesAsync_WithApiKey_CallsLanguagesEndpointWithAuth()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse(TargetLanguagesJson);

        var service = CreateService(handler);
        service.Configure("test-api-key:fx", useWebFirst: true);

        await service.RefreshSupportedLanguagesAsync();

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.ToString().Should().Contain("/v2/languages");
        request.RequestUri!.Query.Should().Contain("type=target");
        request.Headers.Authorization!.Scheme.Should().Be("DeepL-Auth-Key");
        request.Headers.Authorization!.Parameter.Should().Be("test-api-key:fx");
    }

    [Fact]
    public async Task RefreshSupportedLanguagesAsync_IsAdditive_NeverRemovesBaselineLanguages()
    {
        var handler = new MockHttpMessageHandler();
        // A deliberately tiny response; the union must still keep the full baseline.
        handler.EnqueueJsonResponse("""[{"language":"EN-US","name":"English"}]""");

        var service = CreateService(handler);
        service.Configure("test-api-key:fx", useWebFirst: true);

        await service.RefreshSupportedLanguagesAsync();

        // Baseline entries (e.g. Vietnamese from #174) survive a sparse refresh.
        service.SupportedLanguages.Should().Contain(Language.Vietnamese);
        service.SupportedLanguages.Should().Contain(Language.German);
    }

    [Fact]
    public async Task RefreshSupportedLanguagesAsync_FailedFetch_KeepsBaseline()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse("error", System.Net.HttpStatusCode.ServiceUnavailable);

        var service = CreateService(handler);
        service.Configure("test-api-key:fx", useWebFirst: true);

        await service.RefreshSupportedLanguagesAsync();

        // A non-success response leaves the baseline intact (best-effort enhancement).
        service.SupportedLanguages.Should().Contain(Language.English);
        service.SupportedLanguages.Should().NotContain(Language.ClassicalChinese);
    }

    [Fact]
    public async Task RefreshSupportedLanguagesAsync_MalformedJson_KeepsBaseline_DoesNotThrow()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse("not-json{");

        var service = CreateService(handler);
        service.Configure("test-api-key:fx", useWebFirst: true);

        // A malformed/unexpected response must be a no-op, not throw (best-effort refresh).
        await service.RefreshSupportedLanguagesAsync();

        service.SupportedLanguages.Should().Contain(Language.English);
        service.SupportedLanguages.Should().Contain(Language.Vietnamese);
    }

    [Theory]
    [InlineData("EN", Language.English)]
    [InlineData("EN-US", Language.English)]
    [InlineData("EN-GB", Language.English)]
    [InlineData("PT-BR", Language.Portuguese)]
    [InlineData("PT-PT", Language.Portuguese)]
    [InlineData("ZH", Language.SimplifiedChinese)]
    [InlineData("ZH-HANS", Language.SimplifiedChinese)]
    [InlineData("ZH-HANT", Language.TraditionalChinese)]
    [InlineData("AR", Language.Arabic)]
    [InlineData("vi", Language.Vietnamese)]
    public void MapDeepLCode_KnownCodes_MapToExpectedLanguage(string code, Language expected)
    {
        DeepLService.MapDeepLCode(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("EN-ZZ")]
    public void MapDeepLCode_UnknownCodes_ReturnNull_NotEnglish(string? code)
    {
        // Guards against using LanguageCodes.FromIso639, whose fallback coerces unknown codes to English.
        DeepLService.MapDeepLCode(code).Should().BeNull();
    }

    [Fact]
    public void ParseLanguages_SkipsUnknownCodes()
    {
        const string json = """
            [
              {"language":"EN-US","name":"English"},
              {"language":"XX","name":"Nonexistent"},
              {"language":"VI","name":"Vietnamese"}
            ]
            """;

        var result = DeepLService.ParseLanguages(json);

        result.Should().Contain(Language.English);
        result.Should().Contain(Language.Vietnamese);
        result.Should().HaveCount(2); // "XX" skipped, not coerced to English
    }
}
