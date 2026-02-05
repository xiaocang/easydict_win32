using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for VolcanoService (火山翻译).
/// </summary>
public class VolcanoServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly VolcanoService _service;

    public VolcanoServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new VolcanoService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsVolcano()
    {
        _service.ServiceId.Should().Be("volcano");
    }

    [Fact]
    public void DisplayName_IsVolcano()
    {
        _service.DisplayName.Should().Be("Volcano");
    }

    [Fact]
    public void RequiresApiKey_IsTrue()
    {
        _service.RequiresApiKey.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenNoKeys()
    {
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenOnlyAccessKeyId()
    {
        _service.Configure("access-key-id", "");
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenOnlySecretAccessKey()
    {
        _service.Configure("", "secret-access-key");
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue_AfterConfiguration()
    {
        _service.Configure("access-key-id", "secret-access-key");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsExpectedLanguages()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.Auto);
        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.ClassicalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.German);
        languages.Should().Contain(Language.Spanish);
    }

    [Fact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("test-access-key", "test-secret-key");
        var volcanoResponse = """
            {
                "TranslationList": [
                    {
                        "Translation": "你好，世界！",
                        "DetectedSourceLanguage": "en"
                    }
                ],
                "ResponseMetadata": {
                    "RequestId": "test-request-id",
                    "Action": "TranslateText",
                    "Version": "2020-06-01",
                    "Service": "translate",
                    "Region": "cn-north-1"
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(volcanoResponse);

        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好，世界！");
        result.OriginalText.Should().Be("Hello, world!");
        result.ServiceName.Should().Be("Volcano");
        result.DetectedLanguage.Should().Be(Language.English);
    }

    [Fact]
    public async Task TranslateAsync_AutoDetect_OmitsSourceLanguage()
    {
        // Arrange
        _service.Configure("test-access-key", "test-secret-key");
        var volcanoResponse = """
            {
                "TranslationList": [
                    {
                        "Translation": "你好",
                        "DetectedSourceLanguage": "en"
                    }
                ],
                "ResponseMetadata": {
                    "RequestId": "test-request-id"
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(volcanoResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert - SourceLanguage should not be in the request body
        var requestBody = _mockHandler.LastRequestBody;
        requestBody.Should().NotBeNull();
        requestBody.Should().NotContain("\"SourceLanguage\"");
        requestBody.Should().Contain("\"TargetLanguage\":\"zh\"");
        requestBody.Should().Contain("\"TextList\":[\"Hello\"]");
    }

    [Fact]
    public async Task TranslateAsync_WithSourceLanguage_IncludesSourceLanguage()
    {
        // Arrange
        _service.Configure("test-access-key", "test-secret-key");
        var volcanoResponse = """
            {
                "TranslationList": [
                    {
                        "Translation": "Hello",
                        "DetectedSourceLanguage": "zh"
                    }
                ],
                "ResponseMetadata": {}
            }
            """;
        _mockHandler.EnqueueJsonResponse(volcanoResponse);

        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var requestBody = _mockHandler.LastRequestBody;
        requestBody.Should().Contain("\"SourceLanguage\":\"zh\"");
        requestBody.Should().Contain("\"TargetLanguage\":\"en\"");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectHeaders()
    {
        // Arrange
        _service.Configure("test-access-key", "test-secret-key");
        var volcanoResponse = """
            {
                "TranslationList": [{"Translation": "测试"}],
                "ResponseMetadata": {}
            }
            """;
        _mockHandler.EnqueueJsonResponse(volcanoResponse);

        var request = new TranslationRequest
        {
            Text = "Test",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();

        // Should have Authorization header with HMAC-SHA256
        sentRequest!.Headers.Should().ContainSingle(h => h.Key == "Authorization");
        var authHeader = sentRequest.Headers.GetValues("Authorization").First();
        authHeader.Should().StartWith("HMAC-SHA256 Credential=test-access-key/");

        // Should have X-Date header
        sentRequest.Headers.Should().ContainSingle(h => h.Key == "X-Date");
        var xDate = sentRequest.Headers.GetValues("X-Date").First();
        xDate.Should().MatchRegex(@"^\d{8}T\d{6}Z$");

        // Should have Host header
        sentRequest.Headers.Should().ContainSingle(h => h.Key == "Host");
        var host = sentRequest.Headers.GetValues("Host").First();
        host.Should().Be("translate.volcengineapi.com");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectUrl()
    {
        // Arrange
        _service.Configure("test-access-key", "test-secret-key");
        var volcanoResponse = """
            {
                "TranslationList": [{"Translation": "测试"}],
                "ResponseMetadata": {}
            }
            """;
        _mockHandler.EnqueueJsonResponse(volcanoResponse);

        var request = new TranslationRequest
        {
            Text = "Test",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.ToString().Should().Be(
            "https://translate.volcengineapi.com/?Action=TranslateText&Version=2020-06-01");
        sentRequest.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task TranslateAsync_ThrowsWhenNotConfigured()
    {
        // Arrange
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
        exception.ServiceId.Should().Be("volcano");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnUnauthorized()
    {
        // Arrange
        _service.Configure("invalid-key", "invalid-secret");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "Invalid credentials");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnForbidden()
    {
        // Arrange
        _service.Configure("test-key", "test-secret");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Forbidden, "Access denied");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnRateLimited()
    {
        // Arrange
        _service.Configure("test-key", "test-secret");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnApiError()
    {
        // Arrange
        _service.Configure("test-key", "test-secret");
        var errorResponse = """
            {
                "TranslationList": null,
                "ResponseMetadata": {
                    "RequestId": "test-id",
                    "Error": {
                        "Code": "InvalidParameter",
                        "Message": "Invalid source language"
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(errorResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.Message.Should().Contain("Invalid source language");
        exception.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
    }

    [Fact]
    public void ComputeAuthorization_ProducesCorrectFormat()
    {
        // Arrange
        _service.Configure("AKID12345", "SecretKey12345");
        var body = System.Text.Encoding.UTF8.GetBytes("""{"TargetLanguage":"zh","TextList":["Hello"]}""");
        var xDate = "20240101T120000Z";
        var shortDate = "20240101";

        // Act
        var authorization = _service.ComputeAuthorization(body, xDate, shortDate);

        // Assert
        authorization.Should().StartWith("HMAC-SHA256 Credential=AKID12345/20240101/cn-north-1/translate/request,");
        authorization.Should().Contain("SignedHeaders=content-type;host;x-date,");
        authorization.Should().Contain("Signature=");
    }

    [Fact]
    public void ComputeAuthorization_IsDeterministic()
    {
        // Arrange
        _service.Configure("AKID12345", "SecretKey12345");
        var body = System.Text.Encoding.UTF8.GetBytes("""{"TargetLanguage":"zh","TextList":["Hello"]}""");
        var xDate = "20240101T120000Z";
        var shortDate = "20240101";

        // Act
        var auth1 = _service.ComputeAuthorization(body, xDate, shortDate);
        var auth2 = _service.ComputeAuthorization(body, xDate, shortDate);

        // Assert
        auth1.Should().Be(auth2);
    }

    [Fact]
    public void ComputeAuthorization_DifferentBodies_ProduceDifferentSignatures()
    {
        // Arrange
        _service.Configure("AKID12345", "SecretKey12345");
        var body1 = System.Text.Encoding.UTF8.GetBytes("""{"TargetLanguage":"zh","TextList":["Hello"]}""");
        var body2 = System.Text.Encoding.UTF8.GetBytes("""{"TargetLanguage":"zh","TextList":["World"]}""");
        var xDate = "20240101T120000Z";
        var shortDate = "20240101";

        // Act
        var auth1 = _service.ComputeAuthorization(body1, xDate, shortDate);
        var auth2 = _service.ComputeAuthorization(body2, xDate, shortDate);

        // Assert
        auth1.Should().NotBe(auth2);
    }

    [Fact]
    public async Task DetectLanguageAsync_ReturnsAuto()
    {
        var result = await _service.DetectLanguageAsync("Hello");
        result.Should().Be(Language.Auto);
    }

    [Fact]
    public void Configure_SetsCredentials()
    {
        _service.Configure("my-access-key", "my-secret-key");
        _service.IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData(Language.SimplifiedChinese, "zh")]
    [InlineData(Language.TraditionalChinese, "zh-Hant")]
    [InlineData(Language.ClassicalChinese, "lzh")]
    [InlineData(Language.English, "en")]
    [InlineData(Language.Japanese, "ja")]
    [InlineData(Language.Korean, "ko")]
    public async Task TranslateAsync_UsesCorrectLanguageCodes(Language toLang, string expectedCode)
    {
        // Arrange
        _service.Configure("test-key", "test-secret");
        var volcanoResponse = """
            {
                "TranslationList": [{"Translation": "translated"}],
                "ResponseMetadata": {}
            }
            """;
        _mockHandler.EnqueueJsonResponse(volcanoResponse);

        var request = new TranslationRequest
        {
            Text = "test",
            FromLanguage = Language.English,
            ToLanguage = toLang
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var requestBody = _mockHandler.LastRequestBody;
        requestBody.Should().Contain($"\"TargetLanguage\":\"{expectedCode}\"");
    }
}
