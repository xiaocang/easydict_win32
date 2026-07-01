using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
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
    public async Task RecognizeAsync_ThrowsArgumentException_WhenBufferShorterThanExpected()
    {
        var fallback = new FakeOcrService();
        using var client = new OcrWorkerClient(
            SettingsService.Instance,
            fallback,
            _ => throw new InvalidOperationException("worker should not start for invalid input"));

        var act = async () => await client.RecognizeAsync(
            new byte[3],
            pixelWidth: 1,
            pixelHeight: 1,
            preferredLanguageTag: "en-US");

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "pixelData");
        fallback.RecognizeCallCount.Should().Be(0);
    }

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

    [Fact]
    public void CanFallbackToInProc_ReturnsTrue_WhenWorkerProcessExitsUnexpectedly()
    {
        OcrWorkerClient.CanFallbackToInProc(new SidecarProcessExitedException(unchecked((int)0xC0000409)))
            .Should().BeTrue();
    }

    // Regression for issue #176: the worker used to naively join words with spaces, so CJK text
    // came back as "你 好 世 界". MapResult must re-merge per-word data with the same CJK-aware
    // merger as the in-process WindowsOcrService (no space between adjacent CJK characters).
    [Fact]
    public void MapResult_MergesCjkWordsWithoutSpaces_WhenWordsProvided()
    {
        var dto = new OcrResultDto
        {
            Text = "你 好 世 界", // legacy naive join — must be ignored when Words are present
            Lines =
            [
                new OcrLineDto
                {
                    Text = "你 好 世 界",
                    Words = ["你", "好", "世", "界"],
                    BoundingRect = new OcrRectDto(0, 0, 100, 20),
                },
            ],
        };

        var result = OcrWorkerClient.MapResult(dto);

        result.Text.Should().Be("你好世界");
        result.Lines.Should().ContainSingle().Which.Text.Should().Be("你好世界");
    }

    [Fact]
    public void MapResult_KeepsSpacesBetweenLatinWords()
    {
        var dto = new OcrResultDto
        {
            Lines =
            [
                new OcrLineDto
                {
                    Words = ["Hello", "world"],
                    BoundingRect = new OcrRectDto(0, 0, 100, 20),
                },
            ],
        };

        var result = OcrWorkerClient.MapResult(dto);

        result.Text.Should().Be("Hello world");
    }

    [Fact]
    public void MapResult_FallsBackToWorkerText_WhenNoWordData()
    {
        var dto = new OcrResultDto
        {
            Text = "legacy joined text",
            Lines =
            [
                new OcrLineDto
                {
                    Text = "legacy joined text",
                    BoundingRect = new OcrRectDto(0, 0, 100, 20),
                },
            ],
        };

        var result = OcrWorkerClient.MapResult(dto);

        result.Text.Should().Be("legacy joined text");
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
