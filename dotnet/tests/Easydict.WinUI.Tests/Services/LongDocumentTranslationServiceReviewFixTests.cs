using System.Reflection;
using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using WinUiLongDocumentTranslationService = Easydict.WinUI.Services.LongDocumentTranslationService;
using FluentAssertions;
using PdfSharpCore.Pdf;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class LongDocumentTranslationServiceReviewFixTests
{
    [Fact]
    public void CalculateReadingOrderScore_ShouldBePageLocalAndStableForLargeCounts()
    {
        var method = typeof(WinUiLongDocumentTranslationService)
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
    public void FindColumnSplitIndices_ShouldSplitThreeColumnsWithLargeGaps()
    {
        var method = typeof(WinUiLongDocumentTranslationService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FindColumnSplitIndices" && m.GetParameters().Length == 2);
        method.Should().NotBeNull();

        var wordBoxes = new List<(double Left, double Right)>
        {
            (10, 80),
            (120, 190), // gap = 40
            (230, 300)  // gap = 40
        };

        var splits = (IReadOnlyList<int>)method!.Invoke(null, [wordBoxes, 10d])!;
        splits.Should().Equal([0, 1]);
    }

    [Fact]
    public void FindColumnSplitIndices_ShouldNotSplitNormalSentenceWithSmallGaps()
    {
        var method = typeof(WinUiLongDocumentTranslationService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FindColumnSplitIndices" && m.GetParameters().Length == 2);
        method.Should().NotBeNull();

        var wordBoxes = new List<(double Left, double Right)>
        {
            (10, 40),
            (48, 78),  // gap = 8
            (86, 116), // gap = 8
            (124, 154) // gap = 8
        };

        var splits = (IReadOnlyList<int>)method!.Invoke(null, [wordBoxes, 10d])!;
        splits.Should().BeEmpty();
    }

    [Fact]
    public void IsSafeTableLikeCell_ShouldAllowWideHeaderSentenceInHeaderBand()
    {
        var method = typeof(PdfExportService)
            .GetMethod("IsSafeTableLikeCell", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = 612;
        page.Height = 792;

        var metadata = new LongDocumentChunkMetadata
        {
            ChunkIndex = 0,
            PageNumber = 1,
            SourceBlockId = "p1-header-b1",
            SourceBlockType = SourceBlockType.Paragraph,
            OrderInPage = 0,
            RegionType = LayoutRegionType.TableLike,
            RegionConfidence = 0.8,
            RegionSource = LayoutRegionSource.Heuristic,
            ReadingOrderScore = 1,
            BoundingBox = new BlockRect(10, 720, 580, 24),
            TextStyle = new BlockTextStyle { RotationAngle = 0 }
        };

        var result = (bool)method!.Invoke(null, [metadata, page, "Provided proper attribution is provided."])!;
        result.Should().BeTrue();
    }

    [Fact]
    public void IsSafeTableLikeCell_ShouldRejectWideNumericTableLikeRowEvenInHeaderBand()
    {
        var method = typeof(PdfExportService)
            .GetMethod("IsSafeTableLikeCell", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = 612;
        page.Height = 792;

        var metadata = new LongDocumentChunkMetadata
        {
            ChunkIndex = 0,
            PageNumber = 1,
            SourceBlockId = "p1-header-b2",
            SourceBlockType = SourceBlockType.Paragraph,
            OrderInPage = 0,
            RegionType = LayoutRegionType.TableLike,
            RegionConfidence = 0.8,
            RegionSource = LayoutRegionSource.Heuristic,
            ReadingOrderScore = 1,
            BoundingBox = new BlockRect(10, 720, 580, 24),
            TextStyle = new BlockTextStyle { RotationAngle = 0 }
        };

        var result = (bool)method!.Invoke(null, [metadata, page, "1.0  2.0  3.0"])!;
        result.Should().BeFalse();
    }

    [Fact]
    public void NormalizeTranslationForOverlay_ShouldPreserveAsteriskMarker()
    {
        var method = typeof(PdfExportService)
            .GetMethod("NormalizeTranslationForOverlay", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = (string)method!.Invoke(null, ["Ashish Vaswani*", "阿希什·瓦斯瓦尼＊"])!;
        result.Should().Contain("*");

        var square = (string)method.Invoke(null, ["Name*", "名字□"])!;
        square.Should().Contain("*");
        square.Should().NotContain("□");
    }

    [Fact]
    public void WriteBackfillIssuesSidecar_ShouldWriteBothFilenamesEvenWhenEmpty()
    {
        var method = typeof(PdfExportService)
            .GetMethod("WriteBackfillIssuesSidecar", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var tempDir = Path.Combine(Path.GetTempPath(), "Easydict.WinUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var outputPath = Path.Combine(tempDir, "out.pdf");
        try
        {
            method!.Invoke(null, [outputPath, null]);

            File.Exists($"{outputPath}.backfill_issue.json").Should().BeTrue();
            File.Exists($"{outputPath}.backfill_issues.json").Should().BeTrue();

            File.ReadAllText($"{outputPath}.backfill_issue.json").Trim().Should().Be("[]");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void TryPatchPdfLiteralToken_ShouldPatchMultiSegmentTjArray()
    {
        var serviceType = typeof(PdfExportService);
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
        var serviceType = typeof(PdfExportService);
        var method = serviceType.GetMethod("TryPatchPdfLiteralToken", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        const string content = "BT /F1 11 Tf (short) Tj ET";
        var args = new object?[] { content, "short", "this translation is longer", null };

        var patched = (bool)method!.Invoke(null, args)!;
        patched.Should().BeFalse();
        args[3].Should().Be(content);
    }

    [Fact]
    public void BuildQualityReportFromRetry_ShouldKeepCoreBackfillMetrics()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
        var summaryType = serviceType.GetNestedType("RetryExecutionSummary", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("BuildQualityReportFromRetry", BindingFlags.NonPublic | BindingFlags.Static);

        summaryType.Should().NotBeNull();
        method.Should().NotBeNull();

        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Pdf,
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
        var serviceType = typeof(WinUiLongDocumentTranslationService);
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
        var serviceType = typeof(WinUiLongDocumentTranslationService);
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
        merged.PageMetrics![1].CandidateBlocks.Should().Be(3);
        merged.PageMetrics![1].OverlayModeBlocks.Should().Be(1);
        merged.PageMetrics![2].MissingBoundingBoxBlocks.Should().Be(1);
    }

    [Fact]
    public void EnforceTerminologyConsistency_ShouldPreferNearbyPageCanonicalTranslation()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
        var method = serviceType.GetMethod("EnforceTerminologyConsistency", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Pdf,
            SourceChunks = ["Term A", "Term A", "Term A"],
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
                },
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 1,
                    PageNumber = 2,
                    SourceBlockId = "p2-body-b1",
                    SourceBlockType = SourceBlockType.Paragraph,
                    OrderInPage = 0,
                    RegionType = LayoutRegionType.Body,
                    RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = 1
                },
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 2,
                    PageNumber = 10,
                    SourceBlockId = "p10-body-b1",
                    SourceBlockType = SourceBlockType.Paragraph,
                    OrderInPage = 0,
                    RegionType = LayoutRegionType.Body,
                    RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = 1
                }
            ],
            TranslatedChunks = new Dictionary<int, string>
            {
                [0] = "近页术语",
                [2] = "远页术语"
            },
            FailedChunkIndexes = []
        };

        method!.Invoke(null, [checkpoint]);

        checkpoint.TranslatedChunks[1].Should().Be("近页术语");
    }

    [Fact]
    public void InferRegionInfoFromBlockId_ShouldReturnConfidenceAndSource()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
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
        var serviceType = typeof(WinUiLongDocumentTranslationService);
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
    public void MergeRetryBackfillMetrics_Accumulate_MergesBlockIssues()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
        var method = serviceType.GetMethod("MergeRetryBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var previous = new BackfillQualityMetrics
        {
            CandidateBlocks = 1, RenderedBlocks = 0, MissingBoundingBoxBlocks = 1,
            ShrinkFontBlocks = 0, TruncatedBlocks = 0, ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 0, StructuredFallbackBlocks = 0,
            BlockIssues = [new BackfillBlockIssue { ChunkIndex = 0, SourceBlockId = "p1-body-b1", PageNumber = 1, Kind = "skipped-rotated" }]
        };
        var current = new BackfillQualityMetrics
        {
            CandidateBlocks = 1, RenderedBlocks = 0, MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0, TruncatedBlocks = 1, ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 0, StructuredFallbackBlocks = 0,
            BlockIssues = [new BackfillBlockIssue { ChunkIndex = 1, SourceBlockId = "p1-body-b2", PageNumber = 1, Kind = "truncated" }]
        };

        var merged = (BackfillQualityMetrics?)method!.Invoke(null, [previous, current]);
        merged.Should().NotBeNull();
        merged!.BlockIssues.Should().NotBeNull();
        merged.BlockIssues!.Should().HaveCount(2);
        merged.BlockIssues![0].Kind.Should().Be("skipped-rotated");
        merged.BlockIssues[1].Kind.Should().Be("truncated");
    }

    [Fact]
    public void MergeRetryBackfillMetrics_CoreOnly_PreservesBlockIssues()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
        var method = serviceType.GetMethod("MergeRetryBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var current = new BackfillQualityMetrics
        {
            CandidateBlocks = 1, RenderedBlocks = 0, MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0, TruncatedBlocks = 0, ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 0, StructuredFallbackBlocks = 0,
            BlockIssues = [new BackfillBlockIssue { ChunkIndex = 0, SourceBlockId = "p1-body-b1", PageNumber = 1, Kind = "skipped-grid" }]
        };

        var merged = (BackfillQualityMetrics?)method!.Invoke(null, [null, current]);
        merged.Should().NotBeNull();
        merged!.RetryMergeStrategy.Should().Be("core-only");
        merged.BlockIssues.Should().NotBeNull();
        merged.BlockIssues!.Should().HaveCount(1);
        merged.BlockIssues![0].Kind.Should().Be("skipped-grid");
    }

    [Fact]
    public void MergeRetryBackfillMetrics_CheckpointOnly_PreservesBlockIssues()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
        var method = serviceType.GetMethod("MergeRetryBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var previous = new BackfillQualityMetrics
        {
            CandidateBlocks = 1, RenderedBlocks = 0, MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0, TruncatedBlocks = 0, ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 0, StructuredFallbackBlocks = 0,
            BlockIssues = [new BackfillBlockIssue { ChunkIndex = 0, SourceBlockId = "p1-body-b1", PageNumber = 1, Kind = "skipped-table-like" }]
        };

        var merged = (BackfillQualityMetrics?)method!.Invoke(null, [previous, null]);
        merged.Should().NotBeNull();
        merged!.RetryMergeStrategy.Should().Be("checkpoint-only");
        merged.BlockIssues.Should().NotBeNull();
        merged.BlockIssues!.Should().HaveCount(1);
        merged.BlockIssues![0].Kind.Should().Be("skipped-table-like");
    }

    [Fact]
    public void MergeRetryBackfillMetrics_Accumulate_NullBlockIssues_StaysNull()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
        var method = serviceType.GetMethod("MergeRetryBackfillMetrics", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var previous = new BackfillQualityMetrics
        {
            CandidateBlocks = 1, RenderedBlocks = 1, MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0, TruncatedBlocks = 0, ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 1, StructuredFallbackBlocks = 0
        };
        var current = new BackfillQualityMetrics
        {
            CandidateBlocks = 1, RenderedBlocks = 1, MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0, TruncatedBlocks = 0, ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 1, StructuredFallbackBlocks = 0
        };

        var merged = (BackfillQualityMetrics?)method!.Invoke(null, [previous, current]);
        merged.Should().NotBeNull();
        merged!.RetryMergeStrategy.Should().Be("accumulate");
        merged.BlockIssues.Should().BeNull();
    }

    [Theory]
    // "BT /F1 11 Tf " is 13 chars; "(Hello World)" is at 13..25, " Tj" ends at 29
    [InlineData("BT /F1 11 Tf (Hello World) Tj ET", "Hello World", 13, 29)]
    // "(Hello)" is at 13..19, " Tj" ends at 23
    [InlineData("BT /F1 11 Tf (Hello) Tj ET", "Hello", 13, 23)]
    // escaped parens: "(escaped\(paren\))" is at 0..17, " Tj" ends at 21
    [InlineData("(escaped\\(paren\\)) Tj", "escaped(paren)", 0, 21)]
    public void FindTextOperatorRange_ShouldFindLiteralTjForm(string content, string source, int expectedStart, int expectedEnd)
    {
        var (start, end) = PdfExportService.FindTextOperatorRange(content, source);
        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [Fact]
    public void FindTextOperatorRange_ShouldFindTjArrayForm()
    {
        const string content = "BT /F1 11 Tf [(Hello) -80 (World)] TJ ET";
        var (start, end) = PdfExportService.FindTextOperatorRange(content, "Hello World");
        start.Should().Be(13);
        end.Should().Be(37);
    }

    [Theory]
    [InlineData("BT /F1 11 Tf (Other text) Tj ET", "Hello World")]   // different text
    [InlineData("BT ET", "Hello")]                                      // no text operators
    [InlineData("", "Hello")]                                           // empty stream
    public void FindTextOperatorRange_ShouldReturnMinusOneWhenNotFound(string content, string source)
    {
        var (start, end) = PdfExportService.FindTextOperatorRange(content, source);
        start.Should().Be(-1);
        end.Should().Be(-1);
    }

    [Fact]
    public void BuildPerLetterEraseRects_ShouldReturnNullWhenNoCharacterData()
    {
        var metadata = new LongDocumentChunkMetadata
        {
            ChunkIndex = 0, PageNumber = 1, SourceBlockId = "p1-body-b1",
            SourceBlockType = SourceBlockType.Paragraph, OrderInPage = 0,
            RegionType = LayoutRegionType.Body, RegionConfidence = 0.9,
            RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 1,
            FormulaCharacters = null
        };

        var result = PdfExportService.BuildPerLetterEraseRects(metadata, 792.0);
        result.Should().BeNull();
    }

    [Fact]
    public void BuildPerLetterEraseRects_ShouldConvertGlyphCoordsFromPdfToScreenSpace()
    {
        // Glyph at PDF coords: left=50, bottom=100, width=20, height=12
        // In PDF space Y increases upward, so glyph top = bottom + height = 112.
        // In screen space (Y-down, pageHeight=792): drawY = 792 - 112 = 680.
        var metadata = new LongDocumentChunkMetadata
        {
            ChunkIndex = 0, PageNumber = 1, SourceBlockId = "p1-body-b1",
            SourceBlockType = SourceBlockType.Paragraph, OrderInPage = 0,
            RegionType = LayoutRegionType.Body, RegionConfidence = 0.9,
            RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 1,
            FormulaCharacters = new BlockFormulaCharacters
            {
                Characters =
                [
                    new FormulaCharacterInfo("A", "TimesNewRoman", 11, GlyphLeft: 50, GlyphBottom: 100,
                        GlyphWidth: 20, GlyphHeight: 12, IsMathFont: false, IsSubscript: false, IsSuperscript: false)
                ],
                MedianTextFontSize = 11,
                MedianBaselineY = 100,
                HasMathFontCharacters = false
            }
        };

        var rects = PdfExportService.BuildPerLetterEraseRects(metadata, pageHeight: 792.0);
        rects.Should().NotBeNull().And.HaveCount(1);
        rects![0].X.Should().BeApproximately(50, 0.001);
        rects[0].Y.Should().BeApproximately(680, 0.001);   // 792 - (100 + 12) = 680
        rects[0].Width.Should().BeApproximately(20, 0.001);
        rects[0].Height.Should().BeApproximately(12, 0.001);
    }

    [Fact]
    public void BuildPerLetterEraseRects_ShouldSkipGlyphsWithZeroDimensions()
    {
        var metadata = new LongDocumentChunkMetadata
        {
            ChunkIndex = 0, PageNumber = 1, SourceBlockId = "p1-body-b1",
            SourceBlockType = SourceBlockType.Paragraph, OrderInPage = 0,
            RegionType = LayoutRegionType.Body, RegionConfidence = 0.9,
            RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 1,
            FormulaCharacters = new BlockFormulaCharacters
            {
                Characters =
                [
                    new FormulaCharacterInfo(" ", "TimesNewRoman", 11, 50, 100, GlyphWidth: 0, GlyphHeight: 12,
                        false, false, false),  // zero width — space, skip
                    new FormulaCharacterInfo("A", "TimesNewRoman", 11, 70, 100, GlyphWidth: 10, GlyphHeight: 12,
                        false, false, false)   // valid
                ],
                MedianTextFontSize = 11,
                MedianBaselineY = 100,
                HasMathFontCharacters = false
            }
        };

        var rects = PdfExportService.BuildPerLetterEraseRects(metadata, pageHeight: 792.0);
        rects.Should().NotBeNull().And.HaveCount(1);
        rects![0].X.Should().BeApproximately(70, 0.001);
    }

    [Fact]
    public void InferRegionType_ShouldRespectTwoColumnBoundariesWhenEnabled()
    {
        var serviceType = typeof(WinUiLongDocumentTranslationService);
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

        var leftCol = (LayoutRegionType)inferMethod!.Invoke(null, [profile!, 50d, 400d, 700d, 650d, "Left column text"])!;
        var rightCol = (LayoutRegionType)inferMethod.Invoke(null, [profile!, 600d, 950d, 700d, 650d, "Right column text"])!;
        var body = (LayoutRegionType)inferMethod.Invoke(null, [profile!, 440d, 560d, 700d, 650d, "Center text"])!;

        leftCol.Should().Be(LayoutRegionType.LeftColumn);
        rightCol.Should().Be(LayoutRegionType.RightColumn);
        body.Should().Be(LayoutRegionType.Body);
    }
}
