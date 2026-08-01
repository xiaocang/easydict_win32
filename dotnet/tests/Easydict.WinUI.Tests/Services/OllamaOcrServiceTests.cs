using System.Net;
using System.Text.Json;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for OllamaOcrService input validation at the <see cref="OllamaOcrService.RecognizeAsync"/>
/// entry point. Matches the <see cref="WindowsOcrService"/> validation pattern so upstream
/// capture / pixel-stride bugs fail fast with a clear message rather than producing a
/// silently corrupted image.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("OcrThinking")] // OcrThinkingSupport caches rejections process-wide
public class OllamaOcrServiceTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOcrService _service;

    public OllamaOcrServiceTests()
    {
        OcrThinkingSupport.ResetForTests();
        _httpClient = new HttpClient();
        _service = new OllamaOcrService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        OcrThinkingSupport.ResetForTests();
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsArgumentException_WhenBufferShorterThanExpected()
    {
        // 100x100 BGRA8 expects 40000 bytes; supply only 100.
        var tooShort = new byte[100];

        var act = async () => await _service.RecognizeAsync(tooShort, 100, 100);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "pixelData");
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsArgumentException_WhenBufferIsDefault()
    {
        var act = async () => await _service.RecognizeAsync(default, 10, 10);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "pixelData");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public async Task RecognizeAsync_ThrowsArgumentOutOfRangeException_ForInvalidDimensions(int width, int height)
    {
        var buffer = new byte[4];

        var act = async () => await _service.RecognizeAsync(buffer, width, height);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task RecognizeAsync_UsesInjectedOptionsForRequestConstruction()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":\"recognized text\"}")
            }));
        using var client = new HttpClient(handler);
        var options = new OcrServiceOptions(
            OcrEngineType.Ollama,
            null,
            "http://localhost:12345/custom-ocr",
            "edited-model",
            "edited prompt");
        var service = new OllamaOcrService(client, options);

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("recognized text");
        handler.LastRequestUri.Should().Be(new Uri("http://localhost:12345/custom-ocr"));
        handler.LastContentType.Should().Be("application/json");
        handler.LastRequestBody.Should().NotBeNull();

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("edited-model");
        doc.RootElement.GetProperty("prompt").GetString().Should().Be("edited prompt");
        doc.RootElement.GetProperty("images").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RecognizeAsync_SendsPngImagePayload()
    {
        var handler = StubHandler("""{"response":"recognized text"}""");
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options());

        // Opaque red pixel — enough to exercise the encoder path.
        await service.RecognizeAsync(new byte[] { 0, 0, 255, 255 }, 1, 1);

        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var imageB64 = doc.RootElement.GetProperty("images")[0].GetString();
        imageB64.Should().NotBeNullOrEmpty();

        var bytes = Convert.FromBase64String(imageB64!);
        bytes.Length.Should().BeGreaterThan(8);
        // PNG signature
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50);
        bytes[2].Should().Be(0x4E);
        bytes[3].Should().Be(0x47);
        // Must not be a BMP ("BM") payload — Ollama Cloud 500s on BMP for some models.
        bytes[0].Should().NotBe((byte)'B');
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
        var service = new OllamaOcrService(client);

        var act = async () => await service.RecognizeAsync(new byte[4], 1, 1);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*Ollama OCR request timed out*5s*");
    }

    [Fact]
    public async Task RecognizeAsync_DisablesThinkingByDefault()
    {
        var handler = StubHandler("""{"response":"recognized text"}""");
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options());

        await service.RecognizeAsync(new byte[4], 1, 1);

        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        doc.RootElement.GetProperty("think").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_PreservesLiteralThinkingTags_WhenThinkingIsDisabled()
    {
        var handler = StubHandler(
            """{"response":"Before <think>visible</think> after"}""");
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("Before <think>visible</think> after");
    }

    [Fact]
    public async Task RecognizeAsync_OmitsThinkField_WhenThinkingIsEnabled()
    {
        var handler = StubHandler("""{"response":"recognized text"}""");
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options(enableThinking: true));

        await service.RecognizeAsync(new byte[4], 1, 1);

        HasThinkField(handler.RequestBodies[0]).Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_RetriesWithoutThinkField_WhenModelDoesNotSupportThinking()
    {
        var callCount = 0;
        var handler = new RecordingHttpMessageHandler((_, _) =>
        {
            var isFirstCall = callCount++ == 0;

            return Task.FromResult(isFirstCall
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":"registry.ollama.ai/library/glm-ocr does not support thinking"}""")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"response":"Before <think>visible</think> after"}""")
                });
        });
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options());

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("Before <think>visible</think> after");
        handler.RequestBodies.Should().HaveCount(2);
        HasThinkField(handler.RequestBodies[0]).Should().BeTrue();
        HasThinkField(handler.RequestBodies[1]).Should().BeFalse();
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsWithResponseBody_WhenErrorIsUnrelatedToThinking()
    {
        var handler = StubHandler("""{"error":"model not found"}""", HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options());

        var act = async () => await service.RecognizeAsync(new byte[4], 1, 1);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*404*model not found*");
        handler.RequestBodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task RecognizeAsync_PreservesLiteralThinkingTags_WhenThinkingIsEnabled()
    {
        var handler = StubHandler(
            """{"response":"<think>This looks like a road sign.</think>\nSTOP"}""");
        using var client = new HttpClient(handler);
        var service = new OllamaOcrService(client, Options(enableThinking: true));

        var result = await service.RecognizeAsync(new byte[4], 1, 1);

        result.Text.Should().Be("<think>This looks like a road sign.</think>\nSTOP");
    }

    private static bool HasThinkField(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.TryGetProperty("think", out _);
    }

    private static OcrServiceOptions Options(bool enableThinking = false) =>
        new(
            OcrEngineType.Ollama,
            null,
            "http://localhost:11434/api/generate",
            "glm-ocr",
            "extract the text",
            enableThinking);

    private static RecordingHttpMessageHandler StubHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody)
        }));
}
