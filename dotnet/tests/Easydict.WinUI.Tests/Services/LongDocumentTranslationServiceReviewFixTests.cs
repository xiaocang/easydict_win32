using System.Reflection;
using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class LongDocumentTranslationServiceReviewFixTests
{
    [Fact]
    public void CalculateReadingOrderScore_ShouldBePageLocalAndStableForLargeCounts()
    {
        var method = typeof(LongDocumentTranslationService)
            .GetMethod("CalculateReadingOrderScore", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var first = (double)method!.Invoke(null, [0, 200])!;
        var mid = (double)method.Invoke(null, [100, 200])!;
        var last = (double)method.Invoke(null, [199, 200])!;

        first.Should().Be(1d);
        mid.Should().BeGreaterThan(0d).And.BeLessThan(1d);
        last.Should().Be(0d);
    }

    [Fact]
    public void MergeBackfillMetrics_ShouldNotPolluteStageTimingKeys()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var metricsType = serviceType.GetNestedType("BackfillRenderingMetrics", BindingFlags.NonPublic);
        var mergeMethod = serviceType.GetMethod("MergeBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);

        metricsType.Should().NotBeNull();
        mergeMethod.Should().NotBeNull();

        var metrics = Activator.CreateInstance(metricsType!, [10, 8, 1, 2, 1, 3, 5, 0, null]);
        var baseReport = new LongDocumentQualityReport
        {
            StageTimingsMs = new Dictionary<string, long>
            {
                ["translate"] = 123,
                ["structured-layout-output"] = 20
            },
            BackfillMetrics = null,
            TotalBlocks = 10,
            TranslatedBlocks = 8,
            SkippedBlocks = 1,
            FailedBlocks = []
        };

        var merged = (LongDocumentQualityReport)mergeMethod!.Invoke(null, [baseReport, metrics!])!;
        merged.StageTimingsMs.Keys.Should().Contain(["translate", "structured-layout-output"]);
        merged.StageTimingsMs.Keys.Should().NotContain(key => key.StartsWith("backfill", StringComparison.OrdinalIgnoreCase));
        merged.BackfillMetrics.Should().NotBeNull();
        merged.BackfillMetrics!.ObjectReplaceBlocks.Should().Be(3);
    }

    [Fact]
    public void BuildQualityReportFromRetry_ShouldKeepCoreBackfillMetrics()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var summaryType = serviceType.GetNestedType("RetryExecutionSummary", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("BuildQualityReportFromRetry", BindingFlags.NonPublic | BindingFlags.Static);

        summaryType.Should().NotBeNull();
        method.Should().NotBeNull();

        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Manual,
            SourceChunks = ["a"],
            ChunkMetadata =
            [
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 0,
                    PageNumber = 1,
                    SourceBlockId = "p1-body-b1",
                    SourceBlockType = SourceBlockType.Paragraph,
                    OrderInPage = 0,
                    RegionType = LayoutRegionType.Body,
                    RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = 1
                }
            ],
            TranslatedChunks = new Dictionary<int, string> { [0] = "A" },
            FailedChunkIndexes = []
        };

        var coreReport = new LongDocumentQualityReport
        {
            StageTimingsMs = new Dictionary<string, long> { ["translate"] = 1 },
            BackfillMetrics = new BackfillQualityMetrics
            {
                CandidateBlocks = 0,
                RenderedBlocks = 0,
                MissingBoundingBoxBlocks = 0,
                ShrinkFontBlocks = 0,
                TruncatedBlocks = 0,
                ObjectReplaceBlocks = 2,
                OverlayModeBlocks = 0,
                StructuredFallbackBlocks = 0
            },
            TotalBlocks = 1,
            TranslatedBlocks = 1,
            SkippedBlocks = 0,
            FailedBlocks = []
        };

        var summary = Activator.CreateInstance(summaryType!, [coreReport, 0]);
        var report = (LongDocumentQualityReport)method!.Invoke(null, [checkpoint, summary!])!;

        report.BackfillMetrics.Should().NotBeNull();
        report.BackfillMetrics!.ObjectReplaceBlocks.Should().Be(2);
        report.BackfillMetrics.RetryMergeStrategy.Should().Be("core-only");
    }

    [Fact]
    public void MergeRetryBackfillMetrics_ShouldAccumulateWhenBothExist()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var method = serviceType.GetMethod("MergeRetryBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var previous = new BackfillQualityMetrics
        {
            CandidateBlocks = 1,
            RenderedBlocks = 1,
            MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0,
            TruncatedBlocks = 0,
            ObjectReplaceBlocks = 1,
            OverlayModeBlocks = 0,
            StructuredFallbackBlocks = 0
        };

        var current = new BackfillQualityMetrics
        {
            CandidateBlocks = 2,
            RenderedBlocks = 1,
            MissingBoundingBoxBlocks = 1,
            ShrinkFontBlocks = 1,
            TruncatedBlocks = 1,
            ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 1,
            StructuredFallbackBlocks = 0
        };

        var merged = (BackfillQualityMetrics?)method!.Invoke(null, [previous, current]);
        merged.Should().NotBeNull();
        merged!.CandidateBlocks.Should().Be(3);
        merged.ObjectReplaceBlocks.Should().Be(1);
        merged.OverlayModeBlocks.Should().Be(1);
        merged.RetryMergeStrategy.Should().Be("accumulate");
    }



    [Fact]
    public void MergeRetryBackfillMetrics_ShouldMergePageMetrics()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var method = serviceType.GetMethod("MergeRetryBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var previous = new BackfillQualityMetrics
        {
            CandidateBlocks = 1,
            RenderedBlocks = 1,
            MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0,
            TruncatedBlocks = 0,
            ObjectReplaceBlocks = 1,
            OverlayModeBlocks = 0,
            StructuredFallbackBlocks = 0,
            PageMetrics = new Dictionary<int, BackfillPageMetrics>
            {
                [1] = new BackfillPageMetrics
                {
                    CandidateBlocks = 1,
                    RenderedBlocks = 1,
                    MissingBoundingBoxBlocks = 0,
                    ShrinkFontBlocks = 0,
                    TruncatedBlocks = 0,
                    ObjectReplaceBlocks = 1,
                    OverlayModeBlocks = 0,
                    StructuredFallbackBlocks = 0
                }
            }
        };

        var current = new BackfillQualityMetrics
        {
            CandidateBlocks = 2,
            RenderedBlocks = 1,
            MissingBoundingBoxBlocks = 1,
            ShrinkFontBlocks = 0,
            TruncatedBlocks = 0,
            ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 1,
            StructuredFallbackBlocks = 0,
            PageMetrics = new Dictionary<int, BackfillPageMetrics>
            {
                [1] = new BackfillPageMetrics
                {
                    CandidateBlocks = 2,
                    RenderedBlocks = 1,
                    MissingBoundingBoxBlocks = 1,
                    ShrinkFontBlocks = 0,
                    TruncatedBlocks = 0,
                    ObjectReplaceBlocks = 0,
                    OverlayModeBlocks = 1,
                    StructuredFallbackBlocks = 0
                },
                [2] = new BackfillPageMetrics
                {
                    CandidateBlocks = 1,
                    RenderedBlocks = 0,
                    MissingBoundingBoxBlocks = 1,
                    ShrinkFontBlocks = 0,
                    TruncatedBlocks = 0,
                    ObjectReplaceBlocks = 0,
                    OverlayModeBlocks = 0,
                    StructuredFallbackBlocks = 0
                }
            }
        };

        var merged = (BackfillQualityMetrics?)method!.Invoke(null, [previous, current]);
        merged.Should().NotBeNull();
        merged!.PageMetrics.Should().NotBeNull();
        merged.PageMetrics!.Should().ContainKey(1);
        merged.PageMetrics.Should().ContainKey(2);
        merged.PageMetrics[1].CandidateBlocks.Should().Be(3);
        merged.PageMetrics[1].OverlayModeBlocks.Should().Be(1);
        merged.PageMetrics[2].MissingBoundingBoxBlocks.Should().Be(1);
    }

    [Fact]
    public void TryPatchPdfLiteralToken_ShouldPatchMultiSegmentTjArray()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var method = serviceType.GetMethod("TryPatchPdfLiteralToken", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        const string content = "BT /F1 11 Tf [(Hello) -80 (World)] TJ ET";
        var args = new object?[] { content, "Hello World", "Bonjour World", null };

        var patched = (bool)method!.Invoke(null, args)!;
        patched.Should().BeTrue();
        args[3].Should().BeOfType<string>();
        var patchedContent = (string)args[3]!;
        patchedContent.Should().Contain(" Tj");
        patchedContent.Should().Contain("(Bonjour World)");
        patchedContent.Should().NotContain("[(Hello)");
    }



    [Fact]
    public void TryPatchPdfLiteralToken_ShouldReturnFalseWhenTranslationWouldBeTruncated()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var method = serviceType.GetMethod("TryPatchPdfLiteralToken", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        const string content = "BT /F1 11 Tf (short) Tj ET";
        var args = new object?[] { content, "short", "this translation is longer", null };

        var patched = (bool)method!.Invoke(null, args)!;
        patched.Should().BeFalse();
        args[3].Should().Be(content);
    }

    [Fact]
    public void InferRegionInfoFromBlockId_ShouldReturnConfidenceAndSource()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var method = serviceType.GetMethod("InferRegionInfoFromBlockId", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var tableInfo = method!.Invoke(null, ["p2-table-b3"]);
        tableInfo.Should().NotBeNull();
        var tableType = (LayoutRegionType)tableInfo!.GetType().GetField("Item1")!.GetValue(tableInfo)!;
        var tableConfidence = (double)tableInfo.GetType().GetField("Item2")!.GetValue(tableInfo)!;
        var tableSource = (LayoutRegionSource)tableInfo.GetType().GetField("Item3")!.GetValue(tableInfo)!;
        tableType.Should().Be(LayoutRegionType.TableLike);
        tableConfidence.Should().BeGreaterThan(0.8);
        tableSource.Should().Be(LayoutRegionSource.Heuristic);

        var unknownInfo = method.Invoke(null, ["p9-raw-b1"]);
        unknownInfo.Should().NotBeNull();
        var unknownType = (LayoutRegionType)unknownInfo!.GetType().GetField("Item1")!.GetValue(unknownInfo)!;
        var unknownSource = (LayoutRegionSource)unknownInfo.GetType().GetField("Item3")!.GetValue(unknownInfo)!;
        unknownType.Should().Be(LayoutRegionType.Unknown);
        unknownSource.Should().Be(LayoutRegionSource.Unknown);
    }

    [Fact]
    public void InferRegionType_ShouldUseAdaptiveHeaderFooterAndTableHints()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var profileType = serviceType.GetNestedType("LayoutProfile", BindingFlags.NonPublic);
        var inferMethod = serviceType.GetMethod("InferRegionType", BindingFlags.NonPublic | BindingFlags.Static);

        profileType.Should().NotBeNull();
        inferMethod.Should().NotBeNull();

        var profile = Activator.CreateInstance(profileType!, [
            1000d,
            1400d,
            false,
            450d,
            550d,
            1250d,
            140d
        ]);

        var header = (LayoutRegionType)inferMethod!.Invoke(null, [profile!, 100d, 600d, 1300d, 1220d, "Header text"])!;
        var footer = (LayoutRegionType)inferMethod.Invoke(null, [profile!, 100d, 600d, 220d, 100d, "Footer text"])!;
        var table = (LayoutRegionType)inferMethod.Invoke(null, [profile!, 100d, 900d, 700d, 650d, "1.0  2.0  3.0"])!;

        header.Should().Be(LayoutRegionType.Header);
        footer.Should().Be(LayoutRegionType.Footer);
        table.Should().Be(LayoutRegionType.TableLike);
    }

    [Fact]
    public void InferRegionType_ShouldRespectTwoColumnBoundariesWhenEnabled()
    {
        var serviceType = typeof(LongDocumentTranslationService);
        var profileType = serviceType.GetNestedType("LayoutProfile", BindingFlags.NonPublic);
        var inferMethod = serviceType.GetMethod("InferRegionType", BindingFlags.NonPublic | BindingFlags.Static);

        profileType.Should().NotBeNull();
        inferMethod.Should().NotBeNull();

        var profile = Activator.CreateInstance(profileType!, [
            1000d,
            1400d,
            true,
            420d,
            580d,
            1300d,
            80d
        ]);

        var left = (LayoutRegionType)inferMethod!.Invoke(null, [profile!, 120d, 320d, 900d, 820d, "left paragraph"])!;
        var right = (LayoutRegionType)inferMethod.Invoke(null, [profile!, 700d, 900d, 900d, 820d, "right paragraph"])!;
        var body = (LayoutRegionType)inferMethod.Invoke(null, [profile!, 470d, 530d, 900d, 820d, "center bridge"])!;

        left.Should().Be(LayoutRegionType.LeftColumn);
        right.Should().Be(LayoutRegionType.RightColumn);
        body.Should().Be(LayoutRegionType.Body);
    }

}
