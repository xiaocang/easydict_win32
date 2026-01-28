using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for GitHubModelsService-specific behavior.
/// </summary>
public class GitHubModelsServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly GitHubModelsService _service;

    public GitHubModelsServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new GitHubModelsService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsGitHub()
    {
        _service.ServiceId.Should().Be("github");
    }

    [Fact]
    public void DisplayName_IsGitHubModels()
    {
        _service.DisplayName.Should().Be("GitHub Models");
    }

    [Fact]
    public void RequiresApiKey_IsTrue()
    {
        _service.RequiresApiKey.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenNoApiKey()
    {
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue_AfterConfigure()
    {
        _service.Configure("ghp_test_token");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void AvailableModels_ContainsExpectedModels()
    {
        GitHubModelsService.AvailableModels.Should().Contain("gpt-4.1");
        GitHubModelsService.AvailableModels.Should().Contain("gpt-4.1-mini");
        GitHubModelsService.AvailableModels.Should().Contain("gpt-4.1-nano");
        GitHubModelsService.AvailableModels.Should().Contain("gpt-4o");
        GitHubModelsService.AvailableModels.Should().Contain("gpt-4o-mini");
        GitHubModelsService.AvailableModels.Should().Contain("deepseek-v3-0324");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesGitHubEndpoint()
    {
        // Arrange
        _service.Configure("ghp_test");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("models.github.ai");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("ghp_test");
        _mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"你好"}}]}"""
        });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Be("GitHub Models");
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsWhenNotConfigured()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        var action = async () =>
        {
            await foreach (var _ in _service.TranslateStreamAsync(request)) { }
        };

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsBearerToken()
    {
        // Arrange
        _service.Configure("ghp_my_token");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sentRequest.Headers.Authorization.Parameter.Should().Be("ghp_my_token");
    }
}
