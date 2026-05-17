using System.Net;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OpenAI API format auto-detection via URL inspection and explicit override.
/// </summary>
public class OpenAIApiFormatDetectionTests
{
    private static readonly TranslationRequest SampleRequest = new()
    {
        Text = "Hello",
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese
    };

    [Theory]
    [InlineData("https://api.openai.com/v1/responses", OpenAIApiFormat.Responses)]
    [InlineData("https://api.openai.com/v1/responses/", OpenAIApiFormat.Responses)]
    [InlineData("https://my-proxy.example.com/openai/v1/responses", OpenAIApiFormat.Responses)]
    [InlineData("https://api.openai.com/v1/chat/completions", OpenAIApiFormat.ChatCompletions)]
    [InlineData("http://localhost:11434/v1/chat/completions", OpenAIApiFormat.ChatCompletions)]
    [InlineData("https://api.openai.com/v1/", OpenAIApiFormat.ChatCompletions)]
    [InlineData("https://example.com/api", OpenAIApiFormat.ChatCompletions)]
    [InlineData("not-a-url", OpenAIApiFormat.ChatCompletions)]
    public void DetectFormatFromUrl_RecognizesKnownSuffixes(string endpoint, OpenAIApiFormat expected)
    {
        BaseOpenAIService.DetectFormatFromUrl(endpoint).Should().Be(expected);
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesResponsesFormat_WhenUrlEndsWithResponses()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://api.openai.com/v1/responses");

        EnqueueResponsesStream(mockHandler, "Hi");

        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        var body = mockHandler.LastRequestBody!;
        body.Should().Contain("\"instructions\":");
        body.Should().Contain("\"input\":");
        body.Should().NotContain("\"messages\":");
        service.DetectedFormat.Should().Be(OpenAIApiFormat.Responses);
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesChatCompletionsFormat_WhenUrlEndsWithChatCompletions()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://api.openai.com/v1/chat/completions");

        mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"Hi"}}]}"""
        });

        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        var body = mockHandler.LastRequestBody!;
        body.Should().Contain("\"messages\":");
        body.Should().NotContain("\"instructions\":");
        service.DetectedFormat.Should().Be(OpenAIApiFormat.ChatCompletions);
    }

    [Fact]
    public async Task TranslateStreamAsync_DefaultsToChatCompletions_ForAmbiguousUrl()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://custom.example.com/v1/");

        mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"Hi"}}]}"""
        });

        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        mockHandler.LastRequestBody!.Should().Contain("\"messages\":");
        service.DetectedFormat.Should().Be(OpenAIApiFormat.ChatCompletions);
    }

    [Fact]
    public void Pin_ResponsesFormat_OverridesUrl()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var openai = new OpenAIService(httpClient);
        openai.Configure("sk-test", endpoint: "https://api.openai.com/v1/chat/completions",
            formatOverride: OpenAIApiFormat.Responses);

        openai.DetectedFormat.Should().Be(OpenAIApiFormat.Responses);
    }

    [Fact]
    public void Pin_ChatCompletionsFormat_OverridesUrl()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var openai = new OpenAIService(httpClient);
        openai.Configure("sk-test", endpoint: "https://api.openai.com/v1/responses",
            formatOverride: OpenAIApiFormat.ChatCompletions);

        openai.DetectedFormat.Should().Be(OpenAIApiFormat.ChatCompletions);
    }

    [Fact]
    public void Pin_AutoFormat_ClearsOverrideAndFallsBackToUrl()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var openai = new OpenAIService(httpClient);

        openai.Configure("sk-test",
            endpoint: "https://api.openai.com/v1/responses",
            formatOverride: OpenAIApiFormat.ChatCompletions);
        openai.DetectedFormat.Should().Be(OpenAIApiFormat.ChatCompletions);

        openai.Configure("sk-test",
            endpoint: "https://api.openai.com/v1/responses",
            formatOverride: OpenAIApiFormat.Auto);
        openai.DetectedFormat.Should().Be(OpenAIApiFormat.Responses);
    }

    [Fact]
    public async Task PinnedFormat_OverridesUrlInspection()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var openai = new OpenAIService(httpClient);

        // URL says ChatCompletions, but user pinned Responses → Responses body must be sent.
        openai.Configure("sk-test",
            endpoint: "https://api.openai.com/v1/chat/completions",
            formatOverride: OpenAIApiFormat.Responses);

        EnqueueResponsesStream(mockHandler, "ok");
        await ConsumeAsync(openai.TranslateStreamAsync(SampleRequest));

        var body = mockHandler.LastRequestBody!;
        body.Should().Contain("\"instructions\":");
        body.Should().Contain("\"input\":");
        body.Should().NotContain("\"messages\":");
        mockHandler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task TranslateStreamAsync_PropagatesError_WithoutRetry()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://api.openai.com/v1/chat/completions");

        mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "Invalid API key");

        var act = async () => await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        await act.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
        mockHandler.Requests.Should().HaveCount(1);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<string> stream)
    {
        await foreach (var _ in stream) { }
    }

    private static void EnqueueResponsesStream(MockHttpMessageHandler handler, string text)
    {
        var sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"" + text + "\"}\n" +
            "\n" +
            "data: [DONE]\n\n";

        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
    }

    /// <summary>
    /// Test subclass that exposes Configure() so we can flex endpoint/API key.
    /// </summary>
    private sealed class ConfigurableOpenAIService : BaseOpenAIService
    {
        private string _apiKey = "";
        private string _endpoint = "";

        public ConfigurableOpenAIService(HttpClient httpClient) : base(httpClient) { }

        public override string ServiceId => "configurable-test";
        public override string DisplayName => "Configurable Test";
        public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
        public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

        public override string Endpoint => _endpoint;
        public override string ApiKey => _apiKey;
        public override string Model => "test-model";

        public ConfigurableOpenAIService WithConfig(string apiKey, string endpoint)
        {
            _apiKey = apiKey;
            _endpoint = endpoint;
            ResetFormatDetection();
            return this;
        }
    }
}
