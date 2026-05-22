using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.Workers;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Workers;

[Trait("Category", "WinUI")]
public sealed class LongDocWorkerClientResultHydrationTests
{
    [Fact]
    public async Task HydrateResultAsync_ReadsFullResultFromResultJsonPath()
    {
        var path = LongDocResultFileStore.CreateTempPath();
        try
        {
            await LongDocResultFileStore.WriteAsync(path, new TranslateDocumentResult
            {
                State = "Completed",
                OutputPath = @"C:\docs\paper_zh.pdf",
                BilingualOutputPath = @"C:\docs\paper_bilingual.pdf",
                TotalChunks = 20,
                SucceededChunks = 19,
                FailedChunkIndexes = [7],
                QualityReport = "{\"large\":\"payload\"}",
            });

            var pointer = new TranslateDocumentResult
            {
                State = "Completed",
                ResultJsonPath = path,
            };

            var hydrated = await LongDocWorkerClient.HydrateResultAsync(pointer);

            hydrated.OutputPath.Should().Be(@"C:\docs\paper_zh.pdf");
            hydrated.BilingualOutputPath.Should().Be(@"C:\docs\paper_bilingual.pdf");
            hydrated.TotalChunks.Should().Be(20);
            hydrated.SucceededChunks.Should().Be(19);
            hydrated.FailedChunkIndexes.Should().Equal(7);
            hydrated.QualityReport.Should().Be("{\"large\":\"payload\"}");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task HydrateResultAsync_ThrowsInvalidResponse_WhenResultJsonPathIsMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "Easydict", "longdoc-results", $"{Guid.NewGuid():N}.json");
        var pointer = new TranslateDocumentResult
        {
            State = "Completed",
            ResultJsonPath = missingPath,
        };

        var act = () => LongDocWorkerClient.HydrateResultAsync(pointer);

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
    }

    [Fact]
    public void MapResult_MapsFlatWorkerEnvelope()
    {
        var mapped = LongDocWorkerClient.MapResult(new TranslateDocumentResult
        {
            State = "PartiallyCompleted",
            OutputPath = @"C:\docs\paper_zh.txt",
            BilingualOutputPath = @"C:\docs\paper_bilingual.txt",
            TotalChunks = 4,
            SucceededChunks = 3,
            FailedChunkIndexes = [2],
            QualityReport = """
                {
                  "stageTimingsMs": { "translate": 12 },
                  "totalBlocks": 4,
                  "translatedBlocks": 3,
                  "skippedBlocks": 0,
                  "failedBlocks": []
                }
                """,
        });

        mapped.State.Should().Be(LongDocumentJobState.PartialSuccess);
        mapped.OutputPath.Should().Be(@"C:\docs\paper_zh.txt");
        mapped.BilingualOutputPath.Should().Be(@"C:\docs\paper_bilingual.txt");
        mapped.TotalChunks.Should().Be(4);
        mapped.SucceededChunks.Should().Be(3);
        mapped.FailedChunkIndexes.Should().Equal(2);
        mapped.QualityReport.TotalBlocks.Should().Be(4);
        mapped.QualityReport.StageTimingsMs["translate"].Should().Be(12);
        mapped.Checkpoint.Should().BeNull();
    }

    [Fact]
    public void MapResult_BuildsFallbackQualityReport_WhenWorkerOmitsReport()
    {
        var mapped = LongDocWorkerClient.MapResult(new TranslateDocumentResult
        {
            State = "Completed",
            OutputPath = @"C:\docs\paper_zh.txt",
            TotalChunks = 2,
            SucceededChunks = 2,
        });

        mapped.State.Should().Be(LongDocumentJobState.Completed);
        mapped.QualityReport.TotalBlocks.Should().Be(2);
        mapped.QualityReport.TranslatedBlocks.Should().Be(2);
        mapped.QualityReport.FailedBlocks.Should().BeEmpty();
    }

    [Fact]
    public void CanFallbackToInProc_ReturnsTrue_WhenWorkerProcessExitsUnexpectedly()
    {
        var exception = new TranslationException(
            "Long-document worker exited unexpectedly",
            new SidecarProcessExitedException(unchecked((int)0xC0000409)));

        LongDocWorkerClient.CanFallbackToInProc(exception).Should().BeTrue();
    }
}
