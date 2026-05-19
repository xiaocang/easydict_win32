using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
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
}
