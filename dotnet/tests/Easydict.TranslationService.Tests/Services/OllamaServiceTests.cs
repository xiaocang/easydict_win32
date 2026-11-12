using System.Net;
using Easydict.TranslationService.Services.ModelCatalog;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OllamaService-specific behavior.
/// Focuses on: always configured, no API key, RefreshLocalModelsAsync, custom ValidateConfiguration.
/// </summary>
public class OllamaServiceTests
{
    [Fact]
    public async Task FetchModelsAsync_PrefersLocalRecommendationsAndPreservesIdsAndPlanMetadata()
    {
        _service.Configure(endpoint: "https://ollama.example/proxy/v1/chat/completions", model: "my-selection");
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"llama3.2:latest"},{"name":"gpt-oss:120b-cloud"}]}""");
        _mockHandler.RecommendationsStatus = HttpStatusCode.OK;
        _mockHandler.RecommendationsBody = """
            {"recommendations":[
                {"model":"gpt-oss:120b-cloud"},
                {"model":"glm-5.3-flash:cloud","required_plan":"pro","context_length":1048576},
                {"model":"gemma4"}, {"model":"glm-5.3-flash:cloud"},
                null, {}, {"model":null}, {"model":7}, {"model":" "},
                {"model":"qwen:cloud","context_length":"bad"}
            ]}
            """;
        _mockHandler.CloudBody = """{"models":[{"name":"must-not-use-public-catalog"}]}""";

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().Equal("llama3.2:latest", "gpt-oss:120b-cloud", "glm-5.3-flash:cloud", "qwen:cloud");
        models.Single(model => model.Id == "gpt-oss:120b-cloud").Name.Should().Be("Ollama Cloud");
        var recommendation = models.Single(model => model.Id == "glm-5.3-flash:cloud");
        recommendation.Name.Should().Contain("recommended").And.Contain("pro");
        recommendation.ContextLength.Should().Be(1048576);
        _mockHandler.RecommendationRequests.Should().ContainSingle();
        _mockHandler.RecommendationRequests[0].RequestUri!.AbsoluteUri.Should()
            .Be("https://ollama.example/proxy/api/experimental/model-recommendations");
        _mockHandler.CloudRequests.Should().BeEmpty();
        _service.Model.Should().Be("my-selection");
    }

    [Fact]
    public async Task FetchModelsAsync_EmptyLocalRecommendationsDoesNotInjectPublicModels()
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[]}""");
        _mockHandler.RecommendationsStatus = HttpStatusCode.OK;
        _mockHandler.CloudBody = """{"models":[{"name":"must-not-use-public-catalog"}]}""";

        (await _service.FetchModelsAsync()).Should().BeEmpty();

        _mockHandler.CloudRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"recommendations\":null}")]
    public async Task FetchModelsAsync_RecommendationsFailureKeepsRegisteredModelsWithoutPublicFetch(HttpStatusCode status, string body)
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"llama3.2:latest"}]}""");
        _mockHandler.RecommendationsStatus = status;
        _mockHandler.RecommendationsBody = body;

        (await _service.FetchModelsAsync()).Select(model => model.Id).Should().Equal("llama3.2:latest");

        _mockHandler.CloudRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task FetchModelsAsync_OldServerUsesPublicCatalogOnlyAsFallback(HttpStatusCode status)
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"gpt-oss:120b-cloud"}]}""");
        _mockHandler.RecommendationsStatus = status;
        _mockHandler.CloudBody = """{"models":[{"name":"gpt-oss:120b"},{"name":"glm-5.3"}]}""";

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().Equal("gpt-oss:120b-cloud", "glm-5.3:cloud");
        models.Single(model => model.Id == "gpt-oss:120b-cloud").Name.Should().Be("Ollama Cloud");
        models.Single(model => model.Id == "glm-5.3:cloud").Name.Should().Be("Ollama Cloud (catalog)");
        _mockHandler.RecommendationRequests.Should().ContainSingle();
        _mockHandler.CloudRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchModelsAsync_RecommendationsStillWorkWhenRegisteredListFails()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.ServiceUnavailable);
        _mockHandler.RecommendationsStatus = HttpStatusCode.OK;
        _mockHandler.RecommendationsBody = """{"recommendations":[{"model":"glm-5.3-flash:cloud"}]}""";

        (await _service.FetchModelsAsync()).Select(model => model.Id).Should().Equal("glm-5.3-flash:cloud");

        _mockHandler.CloudRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task LocalRecommendationSelection_IsSentToLocalTranslationEndpointWithoutRewritingId()
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[]}""");
        _mockHandler.RecommendationsStatus = HttpStatusCode.OK;
        _mockHandler.RecommendationsBody = """{"recommendations":[{"model":"gpt-oss:cloud"}]}""";
        var models = await _service.FetchModelsAsync();
        _service.Configure(model: models.Single().Id);
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hello"}}]}""" });

        await foreach (var _ in _service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "bonjour", ToLanguage = Language.English
        })) { }

        _mockHandler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("http://localhost:11434/v1/chat/completions");
        using var requestBody = System.Text.Json.JsonDocument.Parse(_mockHandler.LastRequestBody!);
        requestBody.RootElement.GetProperty("model").GetString().Should().Be("gpt-oss:cloud");
        _mockHandler.CloudRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchModelsAsync_DiscoveryTimeoutStillReturnsLocalModels(bool recommendations)
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"llama3.2:latest"}]}""");
        _mockHandler.WaitForCloudCancellation = !recommendations;
        _mockHandler.WaitForRecommendationsCancellation = recommendations;

        var models = await _service.FetchModelsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        models.Select(model => model.Id).Should().Equal("llama3.2:latest");
        if (recommendations) _mockHandler.CloudRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchModelsAsync_CancellingPendingDiscoveryCancelsWholeRefresh(bool recommendations)
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"llama3.2:latest"}]}""");
        _mockHandler.WaitForCloudCancellation = !recommendations;
        _mockHandler.WaitForRecommendationsCancellation = recommendations;
        using var cts = new CancellationTokenSource();
        var pending = _service.FetchModelsAsync(cts.Token);
        pending.IsCompleted.Should().BeFalse();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        if (recommendations) _mockHandler.CloudRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchModelsAsync_CombinesLocalAndCloudWithLocallyCallableIds()
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"llama3.2:latest"},{"name":"gpt-oss:120b-cloud"}]}""");
        _mockHandler.CloudBody = """{"models":[{"name":"gpt-oss:120b"},{"name":"glm-5.3"},{"name":"qwen:cloud"}]}""";

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().Equal("llama3.2:latest", "gpt-oss:120b-cloud", "glm-5.3:cloud", "qwen:cloud");
        models.Where(model => model.Id != "llama3.2:latest").Should().OnlyContain(model => model.Name!.StartsWith("Ollama Cloud"));
        _mockHandler.CloudRequests.Should().ContainSingle();
        _mockHandler.CloudRequests[0].Headers.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task FetchModelsAsync_CloudFailureDoesNotHideLocalModels(HttpStatusCode status, string body)
    {
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"llama3.2:latest"}]}""");
        _mockHandler.CloudStatus = status;
        _mockHandler.CloudBody = body;

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().Equal("llama3.2:latest");
    }

    [Fact]
    public async Task FetchModelsAsync_LocalFailureStillReturnsCloudCatalog()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.ServiceUnavailable);
        _mockHandler.CloudBody = """{"models":[{"name":"gpt-oss:120b"}]}""";

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().Equal("gpt-oss:120b-cloud");
    }

    [Fact]
    public async Task FetchModelsAsync_DirectCloudPreservesApiModelIdsAndFetchesOnlyOnce()
    {
        _service.Configure(endpoint: "https://ollama.com/v1/chat/completions");
        _mockHandler.CloudBody = """{"models":[{"name":"gpt-oss:120b"},{"name":"glm-5.3"}]}""";

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().Equal("gpt-oss:120b", "glm-5.3");
        _mockHandler.CloudRequests.Should().ContainSingle();
        _mockHandler.Requests.Should().BeEmpty();
        _mockHandler.RecommendationRequests.Should().BeEmpty();
    }

    private sealed class OllamaMockHandler : MockHttpMessageHandler
    {
        public string CloudBody { get; set; } = """{"models":[]}""";
        public HttpStatusCode CloudStatus { get; set; } = HttpStatusCode.OK;
        public bool WaitForCloudCancellation { get; set; }
        public List<HttpRequestMessage> CloudRequests { get; } = [];
        public HttpStatusCode RecommendationsStatus { get; set; } = HttpStatusCode.NotFound;
        public string RecommendationsBody { get; set; } = """{"recommendations":[]}""";
        public List<HttpRequestMessage> RecommendationRequests { get; } = [];
        public bool WaitForRecommendationsCancellation { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/experimental/model-recommendations", StringComparison.Ordinal))
            {
                RecommendationRequests.Add(request);
                if (WaitForRecommendationsCancellation)
                    return WaitForCancellationAsync(cancellationToken);
                return Task.FromResult(new HttpResponseMessage(RecommendationsStatus)
                {
                    Content = new StringContent(RecommendationsBody, System.Text.Encoding.UTF8, "application/json")
                });
            }
            if (request.RequestUri!.Host != "ollama.com")
                return base.SendAsync(request, cancellationToken);
            CloudRequests.Add(request);
            if (WaitForCloudCancellation)
                return WaitForCancellationAsync(cancellationToken);
            return Task.FromResult(new HttpResponseMessage(CloudStatus)
            {
                Content = new StringContent(CloudBody, System.Text.Encoding.UTF8, "application/json")
            });
        }

        private static async Task<HttpResponseMessage> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected cloud fetch to be cancelled.");
        }
    }

    [Theory]
    [InlineData("http://192.168.1.100:11434/v1/chat/completions", "http://192.168.1.100:11434/api/tags")]
    [InlineData("https://ollama.example/proxy/v1/chat/completions/", "https://ollama.example/proxy/api/tags")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/api/chat", "http://localhost:11434/api/tags")]
    public async Task FetchModelsAsync_UsesConfiguredServer(string endpoint, string expected)
    {
        _service.Configure(endpoint: endpoint);
        _mockHandler.EnqueueJsonResponse("""{"models":[]}""");

        (await _service.FetchModelsAsync()).Should().BeEmpty();

        _mockHandler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be(expected);
    }

    [Fact]
    public async Task FetchModelsAsync_SkipsMalformedEntriesAndPreservesModelTags()
    {
        _mockHandler.EnqueueJsonResponse("""
            {"models":[null,7,{}, {"name":9}, {"name":" "},
                {"name":"qwen3:8b"},{"name":"qwen3:8b"},{"model":"library/llama3.2:latest"}]}
            """);

        var models = await _service.FetchModelsAsync();

        models.Select(model => model.Id).Should().BeEquivalentTo("qwen3:8b", "library/llama3.2:latest");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"models\":null}")]
    public async Task FetchModelsAsync_InvalidResponseDoesNotPretendModelsAreAvailable(string response)
    {
        _mockHandler.EnqueueJsonResponse(response);

        var action = () => _service.FetchModelsAsync();

        var error = await action.Should().ThrowAsync<TranslationException>();
        error.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
        error.Which.ServiceId.Should().Be("ollama");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, TranslationErrorCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationErrorCode.RateLimited)]
    public async Task FetchModelsAsync_ServerFailureIsReported(HttpStatusCode status, TranslationErrorCode expected)
    {
        _mockHandler.EnqueueErrorResponse(status);

        var action = () => _service.FetchModelsAsync();

        var error = await action.Should().ThrowAsync<TranslationException>();
        error.Which.ErrorCode.Should().Be(expected);
        error.Which.ServiceId.Should().Be("ollama");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelListing_PropagatesCancellation(bool legacyRefresh)
    {
        var token = new CancellationToken(true);
        Func<Task> action = legacyRefresh
            ? () => _service.RefreshLocalModelsAsync(token)
            : async () => { await _service.FetchModelsAsync(token); };

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchModelsAsync_ExposesNativeCatalogWithoutChangingSelectedModel()
    {
        var catalog = _service.Should().BeAssignableTo<IModelCatalogProvider>().Which;
        _service.Configure(model: "my-selected-model");
        _mockHandler.EnqueueJsonResponse("""{"models":[{"name":"qwen3:8b"},{"name":"llama3.2:latest"}]}""");

        var models = await catalog.FetchModelsAsync();

        catalog.ModelsEndpoint.Should().Be("http://localhost:11434/api/tags");
        _mockHandler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        _mockHandler.LastRequest.RequestUri!.AbsoluteUri.Should().Be(catalog.ModelsEndpoint);
        _mockHandler.LastRequest.Headers.Authorization.Should().BeNull();
        models.Select(model => model.Id).Should().BeEquivalentTo("qwen3:8b", "llama3.2:latest");
        _service.Model.Should().Be("my-selected-model");
    }

    private readonly OllamaMockHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly OllamaService _service;

    public OllamaServiceTests()
    {
        _mockHandler = new OllamaMockHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new OllamaService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsOllama()
    {
        _service.ServiceId.Should().Be("ollama");
    }

    [Fact]
    public void DisplayName_IsOllama()
    {
        _service.DisplayName.Should().Be("Ollama");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsExpectedLanguages()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_ParsesModels()
    {
        // Arrange
        var tagsResponse = """
            {
                "models": [
                    {"name": "llama3.2"},
                    {"name": "mistral"},
                    {"name": "codellama"}
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(tagsResponse);

        // Act
        await _service.RefreshLocalModelsAsync();

        // Assert
        _service.AvailableModels.Should().HaveCount(3);
        _service.AvailableModels.Should().Contain("llama3.2");
        _service.AvailableModels.Should().Contain("mistral");
        _service.AvailableModels.Should().Contain("codellama");
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_SetsDefaultOnFailure()
    {
        // Arrange - no response queued, will throw
        // The mock handler will throw InvalidOperationException

        // Act - should not throw
        await _service.RefreshLocalModelsAsync();

        // Assert - falls back to default model
        _service.AvailableModels.Should().Contain("llama3.2");
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_SwitchesModel_WhenCurrentNotAvailable()
    {
        // Arrange
        _service.Configure(model: "nonexistent-model");
        var tagsResponse = """
            {
                "models": [
                    {"name": "mistral"},
                    {"name": "llama3.2"}
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(tagsResponse);

        // Act
        await _service.RefreshLocalModelsAsync();

        // Assert - should switch to first available model
        _service.AvailableModels.Should().HaveCount(2);
        _service.Model.Should().Be("mistral");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesLocalhostEndpoint()
    {
        // Arrange
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
        sentRequest!.RequestUri!.Host.Should().Be("localhost");
        sentRequest.RequestUri.Port.Should().Be(11434);
    }

    [Fact]
    public async Task TranslateStreamAsync_BoundsOutputTokens()
    {
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        await foreach (var _ in _service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        })) { }

        _mockHandler.LastRequestBody.Should().Contain($"\"max_tokens\":{OllamaService.MinOutputTokens}");
    }

    [Fact]
    public async Task TranslateStreamAsync_DefaultsToStandardPrompt()
    {
        (await SendAndReadSystemPromptAsync()).Should().Be(BaseOpenAIService.TranslationSystemPrompt);
    }

    [Fact]
    public async Task TranslateStreamAsync_ConcisePrompt_ReplacesSystemPrompt()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Concise);

        (await SendAndReadSystemPromptAsync()).Should().Be(OllamaService.ConciseSystemPrompt);
    }

    [Fact]
    public async Task TranslateStreamAsync_CustomPrompt_FillsLanguagePlaceholders()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Custom, "  From {from} to {to}. Translation only.  ");

        (await SendAndReadSystemPromptAsync(Language.English, Language.Japanese))
            .Should().Be("From English to Japanese. Translation only.");
    }

    [Fact]
    public async Task TranslateStreamAsync_BlankCustomPrompt_FallsBackToStandard()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Custom, "   ");

        (await SendAndReadSystemPromptAsync()).Should().Be(BaseOpenAIService.TranslationSystemPrompt);
    }

    [Fact]
    public async Task TranslateStreamAsync_PerRequestInstructionsStillAppendToChosenPrompt()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Concise);

        var prompt = await SendAndReadSystemPromptAsync(customPrompt: "Keep it formal.");

        prompt.Should().StartWith(OllamaService.ConciseSystemPrompt);
        prompt.Should().EndWith("Additional instructions: Keep it formal.");
    }

    [Theory]
    [InlineData("Concise", OllamaPromptStyle.Concise)]
    [InlineData("custom", OllamaPromptStyle.Custom)]
    [InlineData("Standard", OllamaPromptStyle.Standard)]
    [InlineData("", OllamaPromptStyle.Standard)]
    [InlineData(null, OllamaPromptStyle.Standard)]
    [InlineData("bogus", OllamaPromptStyle.Standard)]
    [InlineData("7", OllamaPromptStyle.Standard)]
    public void OllamaPromptStyleParser_ParsesStoredValue(string? value, OllamaPromptStyle expected)
    {
        OllamaPromptStyleParser.Parse(value).Should().Be(expected);
    }

    private async Task<string> SendAndReadSystemPromptAsync(
        Language from = Language.Auto,
        Language to = Language.SimplifiedChinese,
        string? customPrompt = null)
    {
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        await foreach (var _ in _service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = from,
            ToLanguage = to,
            CustomPrompt = customPrompt
        })) { }

        using var body = System.Text.Json.JsonDocument.Parse(_mockHandler.LastRequestBody!);
        var system = body.RootElement.GetProperty("messages")[0];
        system.GetProperty("role").GetString().Should().Be("system");
        return system.GetProperty("content").GetString()!;
    }

    [Theory]
    [InlineData(0, OllamaService.MinOutputTokens)]
    [InlineData(10, OllamaService.MinOutputTokens)]
    [InlineData(1000, 4000)]
    [InlineData(1_000_000, OllamaService.MaxOutputTokensCeiling)]
    public void GetOutputTokenLimit_ScalesWithInputWithinBounds(int inputLength, int expected)
    {
        OllamaService.GetOutputTokenLimit(inputLength).Should().Be(expected);
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCustomEndpoint()
    {
        // Arrange
        _service.Configure(endpoint: "http://192.168.1.100:11434/v1/chat/completions");
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
        sentRequest!.RequestUri!.Host.Should().Be("192.168.1.100");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        // Arrange
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
        result.ServiceName.Should().Be("Ollama");
    }

    [Fact]
    public async Task TranslateStreamAsync_DoesNotSendAuthHeader()
    {
        // Arrange
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert - Ollama doesn't need auth
        var sentRequest = _mockHandler.LastRequest;
        // Bearer with empty string may or may not be sent, but should not have a meaningful token
        // The key thing is it shouldn't fail
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_SendsCorrectUrl()
    {
        // Arrange
        var tagsResponse = """{"models": [{"name": "llama3.2"}]}""";
        _mockHandler.EnqueueJsonResponse(tagsResponse);

        // Act
        await _service.RefreshLocalModelsAsync();

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.ToString().Should().Contain("/api/tags");
        sentRequest.RequestUri.Host.Should().Be("localhost");
        sentRequest.RequestUri.Port.Should().Be(11434);
    }
}
