using System.Net;
using System.Text.Json;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for CustomApiOcrService request construction with injected OCR options.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("OcrThinking")] // OcrThinkingSupport caches rejections process-wide
public class CustomApiOcrServiceTests : IDisposable
{
    public CustomApiOcrServiceTests() => OcrThinkingSupport.ResetForTests();

    // The rejected-configuration cache is process-wide; keep it from leaking between tests.
    public void Dispose() => OcrThinkingSupport.ResetForTests();

    [Fact]
    public async Task RecognizeAsync_UsesInjectedOptionsForRequestConstruction()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"recognized text\"}}]}")
            }));
        using var client = new HttpClient(handler);
        var options = new OcrServiceOptions(
            OcrEngineType.CustomApi,
            "edited-key",
            "https://example.com/v1/chat/completions",
            "vision-model",
            "extract with this prompt");
        var service = new CustomApiOcrService(client, options);

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized text");
        handler.LastRequestUri.Should().Be(new Uri("https://example.com/v1/chat/completions"));
        handler.LastAuthorization.Should().NotBeNull();
        handler.LastAuthorization!.Scheme.Should().Be("Bearer");
        handler.LastAuthorization.Parameter.Should().Be("edited-key");
        handler.LastContentType.Should().Be("application/json");
        handler.LastRequestBody.Should().NotBeNull();

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("vision-model");
        doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()
            .Should().Be("extract with this prompt");
        doc.RootElement.GetProperty("messages")[1]
            .GetProperty("content")[0]
            .GetProperty("image_url")
            .GetProperty("url")
            .GetString()
            .Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task RecognizeAsync_UsesResponsesFormat_WhenEndpointIsResponses()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"recognized text\"}]}]}")
            }));
        using var client = new HttpClient(handler);
        var options = new OcrServiceOptions(
            OcrEngineType.CustomApi,
            "edited-key",
            "https://api.openai.com/v1/responses",
            "vision-model",
            "extract with this prompt");
        var service = new CustomApiOcrService(client, options);

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized text");
        handler.LastRequestUri.Should().Be(new Uri("https://api.openai.com/v1/responses"));
        handler.LastRequestBody.Should().NotBeNull();

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("vision-model");
        doc.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("input")[0]
            .GetProperty("content")[0]
            .GetProperty("type")
            .GetString()
            .Should().Be("input_text");
        doc.RootElement.GetProperty("input")[0]
            .GetProperty("content")[1]
            .GetProperty("type")
            .GetString()
            .Should().Be("input_image");
        doc.RootElement.GetProperty("input")[0]
            .GetProperty("content")[1]
            .GetProperty("image_url")
            .GetString()
            .Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsTimeoutException_WhenHttpClientCancelsRequest()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            throw new TaskCanceledException("simulated timeout"));
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var service = new CustomApiOcrService(client);

        var act = async () => await service.RecognizeAsync(new byte[4], 1, 1);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*Custom API OCR request timed out*5s*");
    }

    #region Adaptive max tokens

    [Fact]
    public async Task RecognizeAsync_StartsAtDefaultMaxTokens_ForChatCompletions()
    {
        var handler = StubHandler(ChatCompletionsResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        await service.RecognizeAsync(new byte[4], 1, 1);

        handler.RequestBodies.Should().HaveCount(1);
        MaxTokensOf(handler.RequestBodies[0]).Should().Be(512);
    }

    [Fact]
    public async Task RecognizeAsync_StartsAtDefaultMaxTokens_ForResponses()
    {
        var handler = StubHandler(ResponsesResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions());

        await service.RecognizeAsync(new byte[4], 1, 1);

        handler.RequestBodies.Should().HaveCount(1);
        MaxOutputTokensOf(handler.RequestBodies[0]).Should().Be(512);
    }

    [Fact]
    public async Task RecognizeAsync_DoublesMaxTokens_WhenChatCompletionIsTruncated()
    {
        var handler = SequencedHandler(
            ChatCompletionsResponse("truncated", finishReason: "length"),
            ChatCompletionsResponse("the complete recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("the complete recognized text");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024);
    }

    [Fact]
    public async Task RecognizeAsync_PrefersCompletedText_OverLongerTruncatedText()
    {
        var handler = SequencedHandler(
            ChatCompletionsResponse("a much longer partial result", finishReason: "length"),
            ChatCompletionsResponse("complete"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("complete");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"choices":[]}""")]
    public async Task RecognizeAsync_PreservesPartial_WhenRetryIsInvalid(string invalidResponse)
    {
        var handler = SequencedHandler(
            ChatCompletionsResponse("partial result", finishReason: "length"),
            invalidResponse);
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("partial result");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024);
    }

    [Fact]
    public async Task RecognizeAsync_PreservesPartial_WhenChatRetryIsContentFiltered()
    {
        var handler = SequencedHandler(
            ChatCompletionsResponse("partial result", finishReason: "length"),
            ChatCompletionsResponse(string.Empty, finishReason: "content_filter"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("partial result");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024);
    }

    [Fact]
    public async Task RecognizeAsync_DoublesMaxTokens_WhenResponsesGenerationIsIncomplete()
    {
        var handler = SequencedHandler(
            ResponsesResponse("truncated", status: "incomplete", incompleteReason: "max_output_tokens"),
            ResponsesResponse("the complete recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("the complete recognized text");
        handler.RequestBodies.Select(MaxOutputTokensOf).Should().Equal(512, 1024);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task RecognizeAsync_PreservesPartial_WhenResponsesRetryDoesNotComplete(string status)
    {
        var handler = SequencedHandler(
            ResponsesResponse("partial result", status: "incomplete", incompleteReason: "max_output_tokens"),
            ResponsesResponse(string.Empty, status));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("partial result");
        handler.RequestBodies.Select(MaxOutputTokensOf).Should().Equal(512, 1024);
    }

    [Fact]
    public async Task RecognizeAsync_TreatsUsageAtBudgetAsTruncated_WhenFinishReasonIsMissing()
    {
        var handler = SequencedHandler(
            """{"choices":[{"message":{"content":"truncated"}}],"usage":{"completion_tokens":512}}""",
            ChatCompletionsResponse("the complete recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("the complete recognized text");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024);
    }

    [Fact]
    public async Task RecognizeAsync_RetriesMetadataLessChatResponse_ToLegacyBudget()
    {
        var handler = SequencedHandler(
            """{"choices":[{"message":{"content":"partial"}}]}""",
            """{"choices":[{"message":{"content":"longer partial"}}]}""",
            """{"choices":[{"message":{"content":"the complete recognized text"}}]}""");
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("the complete recognized text");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024, 2048);
    }

    [Fact]
    public async Task RecognizeAsync_RetriesMetadataLessResponsesResponse_ToLegacyBudget()
    {
        var handler = SequencedHandler(
            """{"output_text":"partial"}""",
            """{"output_text":"longer partial"}""",
            """{"output_text":"the complete recognized text"}""");
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("the complete recognized text");
        handler.RequestBodies.Select(MaxOutputTokensOf).Should().Equal(512, 1024, 2048);
    }

    [Fact]
    public async Task RecognizeAsync_RetriesEmptyMetadataLessChatResponse_ToCeiling()
    {
        var handler = SequencedHandler(
            """{"choices":[{"message":{}}]}""",
            ChatCompletionsResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions(enableThinking: true));

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized text");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(2048, 4096);
    }

    [Fact]
    public async Task RecognizeAsync_RetriesEmptyMetadataLessResponsesResponse_ToCeiling()
    {
        var handler = SequencedHandler(
            "{}",
            ResponsesResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(
            client,
            ResponsesOptions(enableThinking: true));

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized text");
        handler.RequestBodies.Select(MaxOutputTokensOf).Should().Equal(2048, 4096);
    }

    [Fact]
    public async Task RecognizeAsync_StopsEmptyMetadataLessChatResponse_AtLegacyBudget_WhenThinkingIsDisabled()
    {
        var handler = SequencedHandler(
            """{"choices":[{"message":{}}]}""",
            """{"choices":[{"message":{}}]}""",
            """{"choices":[{"message":{}}]}""");
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().BeEmpty();
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024, 2048);
    }

    [Fact]
    public async Task RecognizeAsync_StopsEmptyMetadataLessResponsesResponse_AtLegacyBudget_WhenThinkingIsDisabled()
    {
        var handler = SequencedHandler("{}", "{}", "{}");
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().BeEmpty();
        handler.RequestBodies.Select(MaxOutputTokensOf).Should().Equal(512, 1024, 2048);
    }

    [Fact]
    public async Task RecognizeAsync_StopsEscalatingAtCeiling_AndKeepsLongestText()
    {
        var handler = SequencedHandler(
            ChatCompletionsResponse("a", finishReason: "length"),
            ChatCompletionsResponse("the longest partial result", finishReason: "length"),
            ChatCompletionsResponse("bb", finishReason: "length"),
            ChatCompletionsResponse("ccc", finishReason: "length"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("the longest partial result");
        handler.RequestBodies.Select(MaxTokensOf).Should().Equal(512, 1024, 2048, 4096);
    }

    [Fact]
    public async Task RecognizeAsync_StartsHigher_WhenThinkingIsEnabled()
    {
        var handler = StubHandler(ChatCompletionsResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions(enableThinking: true));

        await service.RecognizeAsync(new byte[4], 1, 1);

        MaxTokensOf(handler.RequestBodies[0]).Should().Be(2048);
    }

    #endregion

    #region Thinking control

    [Fact]
    public async Task RecognizeAsync_DisablesThinkingByDefault_ForChatCompletions()
    {
        var handler = StubHandler(ChatCompletionsResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        await service.RecognizeAsync(new byte[4], 1, 1);

        StringAt(handler.RequestBodies[0], "thinking", "type").Should().Be("disabled");
    }

    [Fact]
    public async Task RecognizeAsync_UsesReasoningEffort_ForGpt5ChatCompletions()
    {
        var handler = StubHandler(ChatCompletionsResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(
            client,
            ChatCompletionsOptions(model: "gpt-5.4-mini"));

        await service.RecognizeAsync(new byte[4], 1, 1);

        StringAt(handler.RequestBodies[0], "reasoning_effort").Should().Be("none");
        HasProperty(handler.RequestBodies[0], "thinking").Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_OmitsThinkingField_WhenThinkingIsEnabled()
    {
        var handler = StubHandler(ChatCompletionsResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions(enableThinking: true));

        await service.RecognizeAsync(new byte[4], 1, 1);

        HasProperty(handler.RequestBodies[0], "thinking").Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_UsesReasoningEffort_ForGpt5OnResponsesEndpoint()
    {
        var handler = StubHandler(ResponsesResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions(model: "gpt-5.4-mini"));

        await service.RecognizeAsync(new byte[4], 1, 1);

        StringAt(handler.RequestBodies[0], "reasoning", "effort").Should().Be("none");
        HasProperty(handler.RequestBodies[0], "thinking").Should().BeFalse(
            "the Responses API has no thinking field");
    }

    [Fact]
    public async Task RecognizeAsync_OmitsUnsupportedReasoningEffort_ForGpt5Pro()
    {
        var handler = StubHandler(ResponsesResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions(model: "gpt-5.4-pro"));

        await service.RecognizeAsync(new byte[4], 1, 1);

        HasProperty(handler.RequestBodies[0], "reasoning").Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_OmitsReasoning_ForModelWithoutReasoningControl()
    {
        var handler = StubHandler(ResponsesResponse("recognized text"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ResponsesOptions(model: "qwen-vl-max"));

        await service.RecognizeAsync(new byte[4], 1, 1);

        HasProperty(handler.RequestBodies[0], "reasoning").Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_RetriesWithoutThinkingField_WhenProviderRejectsIt()
    {
        var handler = SequencedHandler(
            (HttpStatusCode.BadRequest,
                """{"error":{"message":"Unrecognized request argument supplied: thinking"}}"""),
            (HttpStatusCode.OK, ChatCompletionsResponse("recognized text")));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized text");
        handler.RequestBodies.Should().HaveCount(2);
        HasProperty(handler.RequestBodies[0], "thinking").Should().BeTrue();
        HasProperty(handler.RequestBodies[1], "thinking").Should().BeFalse();
        MaxTokensOf(handler.RequestBodies[1]).Should().Be(512,
            "dropping the field must not change the token budget");
    }

    [Fact]
    public async Task RecognizeAsync_RemembersRejection_AndSkipsThinkingFieldNextTime()
    {
        var options = ChatCompletionsOptions();
        var firstHandler = SequencedHandler(
            (HttpStatusCode.BadRequest, """{"error":"does not support thinking"}"""),
            (HttpStatusCode.OK, ChatCompletionsResponse("recognized text")));
        using var firstClient = new HttpClient(firstHandler);
        await new CustomApiOcrService(firstClient, options).RecognizeAsync(new byte[4], 1, 1);

        var secondHandler = StubHandler(ChatCompletionsResponse("recognized again"));
        using var secondClient = new HttpClient(secondHandler);
        var result = await new CustomApiOcrService(secondClient, options)
            .RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized again");
        secondHandler.RequestBodies.Should().HaveCount(1);
        HasProperty(secondHandler.RequestBodies[0], "thinking").Should().BeFalse();
    }

    [Fact]
    public void RejectionCache_PreservesEndpointPathAndModelCase()
    {
        OcrThinkingSupport.MarkRejectedBy(
            "https://example.com/V1/chat/completions",
            "Vision-A");

        OcrThinkingSupport.IsRejectedBy(
            "https://example.com/v1/chat/completions",
            "Vision-A").Should().BeFalse();
        OcrThinkingSupport.IsRejectedBy(
            "https://example.com/V1/chat/completions",
            "vision-a").Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsWithResponseBody_WhenErrorIsUnrelatedToThinking()
    {
        var handler = StubHandler("""{"error":{"message":"invalid api key"}}""", HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var act = async () => await service.RecognizeAsync(new byte[4], 1, 1);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*401*invalid api key*");
        handler.RequestBodies.Should().HaveCount(1, "an auth failure is not worth retrying");
    }

    [Fact]
    public async Task RecognizeAsync_DoesNotRetryForUnrelatedErrorContainingThink()
    {
        var handler = StubHandler(
            """{"error":{"message":"I think the image text includes \"thinking\", but its dimensions are invalid"}}""",
            HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);
        var options = ChatCompletionsOptions();
        var service = new CustomApiOcrService(client, options);

        var act = async () => await service.RecognizeAsync(new byte[4], 1, 1);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*400*I think the image text includes*");
        handler.RequestBodies.Should().HaveCount(1);
        OcrThinkingSupport.IsRejectedBy(options.Endpoint, options.Model).Should().BeFalse();
    }

    [Fact]
    public void IsThinkingFieldRejection_RequiresTheFieldActuallySent()
    {
        const string body =
            """{"error":{"message":"Unsupported request parameter","param":"reasoning_effort"}}""";

        OcrThinkingSupport.IsThinkingFieldRejection(
            HttpStatusCode.BadRequest,
            body,
            "reasoning_effort").Should().BeTrue();
        OcrThinkingSupport.IsThinkingFieldRejection(
            HttpStatusCode.BadRequest,
            body,
            "thinking").Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_PreservesLiteralThinkingTags_WhenThinkingIsEnabled()
    {
        var handler = StubHandler(
            ChatCompletionsResponse("<think>The image shows a sign.</think>\\nSTOP"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions(enableThinking: true));

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("<think>The image shows a sign.</think>\nSTOP");
    }

    [Fact]
    public async Task RecognizeAsync_PreservesLiteralThinkingTags_WhenThinkingIsDisabled()
    {
        var handler = StubHandler(
            ChatCompletionsResponse("Before <think>visible</think> after"));
        using var client = new HttpClient(handler);
        var service = new CustomApiOcrService(client, ChatCompletionsOptions());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("Before <think>visible</think> after");
    }

    #endregion

    #region Helpers

    private static OcrServiceOptions ChatCompletionsOptions(
        bool enableThinking = false,
        string model = "mimo-vl") =>
        new(
            OcrEngineType.CustomApi,
            "test-key",
            "https://example.com/v1/chat/completions",
            model,
            "extract the text",
            enableThinking);

    private static OcrServiceOptions ResponsesOptions(
        string model = "gpt-5.4-mini",
        bool enableThinking = false) =>
        new(
            OcrEngineType.CustomApi,
            "test-key",
            "https://api.openai.com/v1/responses",
            model,
            "extract the text",
            enableThinking);

    private static string ChatCompletionsResponse(string content, string finishReason = "stop") =>
        $$"""{"choices":[{"message":{"content":"{{content}}"},"finish_reason":"{{finishReason}}"}]}""";

    private static string ResponsesResponse(
        string text,
        string status = "completed",
        string? incompleteReason = null)
    {
        var details = incompleteReason is null
            ? string.Empty
            : $$""","incomplete_details":{"reason":"{{incompleteReason}}"}""";

        return $$"""{"status":"{{status}}"{{details}},"output":[{"content":[{"type":"output_text","text":"{{text}}"}]}]}""";
    }

    private static int MaxTokensOf(string requestBody) => IntAt(requestBody, "max_tokens");

    private static int MaxOutputTokensOf(string requestBody) => IntAt(requestBody, "max_output_tokens");

    private static int IntAt(string json, string propertyName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(propertyName).GetInt32();
    }

    /// <summary>
    /// Value at a nested property path, or null when any step is missing.
    /// </summary>
    private static string? StringAt(string json, params string[] path)
    {
        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement;

        foreach (var step in path)
        {
            if (!element.TryGetProperty(step, out var child))
            {
                return null;
            }

            element = child;
        }

        return element.ToString();
    }

    private static bool HasProperty(string json, string propertyName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(propertyName, out _);
    }

    private static RecordingHttpMessageHandler StubHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody)
        }));

    private static RecordingHttpMessageHandler SequencedHandler(params string[] responseBodies) =>
        SequencedHandler(responseBodies.Select(body => (HttpStatusCode.OK, body)).ToArray());

    /// <summary>
    /// Replies with each response in order; the last one repeats if the service asks again.
    /// </summary>
    private static RecordingHttpMessageHandler SequencedHandler(
        params (HttpStatusCode StatusCode, string Body)[] responses)
    {
        var callCount = 0;

        return new RecordingHttpMessageHandler((_, _) =>
        {
            var (statusCode, body) = responses[Math.Min(callCount, responses.Length - 1)];
            callCount++;

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            });
        });
    }

    #endregion
}
