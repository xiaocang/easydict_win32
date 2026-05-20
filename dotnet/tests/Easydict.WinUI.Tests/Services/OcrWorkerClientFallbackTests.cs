using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.Workers;
using FluentAssertions;
using Xunit;
using SidecarClientType = Easydict.SidecarClient.SidecarClient;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class OcrWorkerClientFallbackTests
{
    [Fact]
    public async Task RecognizeAsync_FallsBackToInProcService_WhenWorkerStartFails()
    {
        var fallback = new FakeOcrService();
        using var client = new OcrWorkerClient(
            SettingsService.Instance,
            fallback,
            _ => Task.FromException<SidecarClientType>(new WorkerStartFailedException("missing worker")));

        var result = await client.RecognizeAsync(
            new byte[] { 0, 0, 0, 255 },
            pixelWidth: 1,
            pixelHeight: 1,
            preferredLanguageTag: "en-US");

        result.Text.Should().Be("fallback text");
        result.Lines.Should().ContainSingle().Which.Text.Should().Be("fallback text");
        fallback.RecognizeCallCount.Should().Be(1);
        fallback.LastPreferredLanguageTag.Should().Be("en-US");
    }

    private sealed class FakeOcrService : IOcrService
    {
        public int RecognizeCallCount { get; private set; }
        public string? LastPreferredLanguageTag { get; private set; }

        public string ServiceId => "fake_ocr";
        public string DisplayName => "Fake OCR";
        public bool IsAvailable => true;

        public Task<OcrResult> RecognizeAsync(
            ReadOnlyMemory<byte> pixelData,
            int pixelWidth,
            int pixelHeight,
            string? preferredLanguageTag = null,
            CancellationToken cancellationToken = default)
        {
            RecognizeCallCount++;
            LastPreferredLanguageTag = preferredLanguageTag;
            return Task.FromResult(new OcrResult
            {
                Text = "fallback text",
                Lines =
                [
                    new OcrLine
                    {
                        Text = "fallback text",
                        BoundingRect = new OcrRect(0, 0, pixelWidth, pixelHeight),
                    },
                ],
            });
        }

        public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => [];
    }
}
