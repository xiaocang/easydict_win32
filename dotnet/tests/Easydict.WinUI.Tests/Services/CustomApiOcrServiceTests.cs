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
public class CustomApiOcrServiceTests
{
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
            .Should().StartWith("data:image/jpeg;base64,");
    }
}
