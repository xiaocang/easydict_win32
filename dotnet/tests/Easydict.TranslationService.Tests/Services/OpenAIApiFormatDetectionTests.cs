using System.Net;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OpenAI API format auto-detection (URL prefix + endpoint probe).
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
    [InlineData("https://api.openai.com/v1/", OpenAIApiFormat.Auto)]
    [InlineData("https://example.com/api", OpenAIApiFormat.Auto)]
    [InlineData("not-a-url", OpenAIApiFormat.Auto)]
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
    public async Task TranslateStreamAsync_ProbeFallback_FromChatCompletionsToResponses_OnAmbiguousUrl()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        // Ambiguous URL on a non-openai host → preferred first format is ChatCompletions.
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://custom.example.com/v1/");

        mockHandler.EnqueueErrorResponse(HttpStatusCode.NotFound, "Unknown route");
        EnqueueResponsesStream(mockHandler, "Hello");

        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        mockHandler.Requests.Should().HaveCount(2);
        service.DetectedFormat.Should().Be(OpenAIApiFormat.Responses);
    }

    [Fact]
    public async Task TranslateStreamAsync_ProbeFallback_PrefersResponses_OnOfficialOpenAIHost()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://api.openai.com/v1/");

        EnqueueResponsesStream(mockHandler, "Hi");

        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        mockHandler.Requests.Should().HaveCount(1);
        service.DetectedFormat.Should().Be(OpenAIApiFormat.Responses);
    }

    [Fact]
    public async Task TranslateStreamAsync_CachesFormat_AcrossCalls_UntilReset()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://custom.example.com/v1/");

        // First call probes: ChatCompletions 404 → Responses succeeds.
        mockHandler.EnqueueErrorResponse(HttpStatusCode.NotFound, "no route");
        EnqueueResponsesStream(mockHandler, "first");

        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));
        service.DetectedFormat.Should().Be(OpenAIApiFormat.Responses);

        // Second call: cached → no probe, just one request in Responses format.
        EnqueueResponsesStream(mockHandler, "second");
        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        mockHandler.Requests.Should().HaveCount(3);
        mockHandler.LastRequestBody!.Should().Contain("\"instructions\":");

        // Reconfigure → cache reset.
        service.WithConfig("sk-test", "https://other.example.com/v1/chat/completions");
        service.DetectedFormat.Should().Be(OpenAIApiFormat.Auto);

        mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"third"}}]}"""
        });
        await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));
        service.DetectedFormat.Should().Be(OpenAIApiFormat.ChatCompletions);
    }

    [Fact]
    public async Task TranslateStreamAsync_DoesNotFallback_OnAuthError()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://custom.example.com/v1/");

        // First (probe) request returns 401 — should NOT fall back; should surface auth error.
        mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "Invalid API key");

        var act = async () => await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        await act.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
        mockHandler.Requests.Should().HaveCount(1);
        service.DetectedFormat.Should().Be(OpenAIApiFormat.Auto);
    }

    [Fact]
    public async Task TranslateStreamAsync_DoesNotFallback_When404OnDeterministicUrl()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        // URL deterministically says ChatCompletions; a 404 should surface, not retry.
        var service = new ConfigurableOpenAIService(httpClient)
            .WithConfig("sk-test", "https://api.openai.com/v1/chat/completions");

        mockHandler.EnqueueErrorResponse(HttpStatusCode.NotFound, "model not found");

        var act = async () => await ConsumeAsync(service.TranslateStreamAsync(SampleRequest));

        await act.Should().ThrowAsync<TranslationException>();
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
