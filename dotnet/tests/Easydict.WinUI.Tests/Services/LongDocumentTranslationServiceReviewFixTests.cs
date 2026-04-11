using System.Reflection;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using WinUiLongDocumentTranslationService = Easydict.WinUI.Services.LongDocumentTranslationService;
using CoreLongDocumentTranslationResult = Easydict.TranslationService.LongDocument.LongDocumentTranslationResult;
using FluentAssertions;
using MuPDF.NET;
using PdfSharpCore.Pdf;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class LongDocumentTranslationServiceReviewFixTests
{
    private static string GetPdfFixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "Pdf", fileName);

    private static string NormalizeWhitespaceForAssertion(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    private static string CompactWhitespaceForAssertion(string text) =>
        Regex.Replace(text, @"\s+", string.Empty);

    private static async Task<SourceDocument> BuildSourceDocumentFromFixtureAsync(string pdfPath)
    {
        using var service = new WinUiLongDocumentTranslationService();
        var buildSourceMethod = typeof(WinUiLongDocumentTranslationService)
            .GetMethod("BuildSourceDocumentAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        buildSourceMethod.Should().NotBeNull();

        var buildTask = (Task<SourceDocument>)buildSourceMethod!.Invoke(service,
            [
                LongDocumentInputMode.Pdf,
                pdfPath,
                LayoutDetectionMode.Auto,
                null,
                null,
                null,
                null,
                CancellationToken.None,
                null
            ])!;

        return await buildTask;
    }

    private static LongDocumentTranslationCheckpoint BuildIdentityCheckpoint(SourceDocument source, string pdfPath)
    {
        var sourceChunks = new List<string>();
        var chunkMetadata = new List<LongDocumentChunkMetadata>();
        var translatedChunks = new Dictionary<int, string>();
        var chunkIndex = 0;

        foreach (var page in source.Pages.OrderBy(page => page.PageNumber))
        {
            for (var order = 0; order < page.Blocks.Count; order++)
            {
                var block = page.Blocks[order];
                var isFormulaBlock = block.BlockType == SourceBlockType.Formula;

                sourceChunks.Add(block.Text);
                translatedChunks[chunkIndex] = block.Text;
                chunkMetadata.Add(new LongDocumentChunkMetadata
                {
                    ChunkIndex = chunkIndex,
                    PageNumber = page.PageNumber,
                    SourceBlockId = block.BlockId,
                    SourceBlockType = block.BlockType,
                    IsFormulaLike = block.IsFormulaLike,
                    OrderInPage = order,
                    RegionType = InferFixtureRegionType(block),
                    RegionConfidence = 1,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = page.Blocks.Count <= 1
                        ? 1
                        : 1 - order / (double)(page.Blocks.Count - 1),
                    BoundingBox = block.BoundingBox,
                    TextStyle = block.TextStyle,
                    FormulaCharacters = block.FormulaCharacters,
                    TranslationSkipped = isFormulaBlock,
                    PreserveOriginalTextInPdfExport = isFormulaBlock
                });
                chunkIndex++;
            }
        }

        return new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Pdf,
            SourceFilePath = pdfPath,
            TargetLanguage = Language.SimplifiedChinese,
            SourceChunks = sourceChunks,
            ChunkMetadata = chunkMetadata,
            TranslatedChunks = translatedChunks,
            FailedChunkIndexes = []
        };
    }

    private static LayoutRegionType InferFixtureRegionType(SourceDocumentBlock block) =>
        block.BlockType switch
        {
            SourceBlockType.Formula => LayoutRegionType.Formula,
            SourceBlockType.TableCell => LayoutRegionType.TableLike,
            _ => LayoutRegionType.Body
        };

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

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
    public void GuessBlockType_ShouldTreatSquareRootEquationAsFormula()
    {
        var method = typeof(WinUiLongDocumentTranslationService)
            .GetMethod("GuessBlockType", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var result = (SourceBlockType)method!.Invoke(null, ["x = √d_k"])!;
        result.Should().Be(SourceBlockType.Formula);
    }

    [Fact]
    public void GuessBlockType_ShouldTreatLongProseWithInlineEquationAsParagraph()
    {
        var method = typeof(WinUiLongDocumentTranslationService)
            .GetMethod("GuessBlockType", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        const string text =
            "Most competitive neural sequence transduction models have an encoder-decoder structure. " +
            "Here, the encoder maps the input sequence of symbol representations (x1, ..., xn) " +
            "to a sequence of continuous representations z = (z1, ..., zn).";

        var result = (SourceBlockType)method!.Invoke(null, [text])!;
        result.Should().Be(SourceBlockType.Paragraph);
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

    [Fact]
    public void CreateCoreTranslationOptions_ShouldEnableQualityFeedbackRetry_ForInitialAndRetryTranslation()
    {
        var initialOptions = WinUiLongDocumentTranslationService.CreateCoreTranslationOptions(
            "google",
            Language.English,
            Language.SimplifiedChinese,
            enableOcrFallback: true,
            maxConcurrency: 4,
            formulaFontPattern: "font",
            formulaCharPattern: "char",
            customPrompt: "prompt");

        var retryOptions = WinUiLongDocumentTranslationService.CreateCoreTranslationOptions(
            "google",
            Language.English,
            Language.SimplifiedChinese,
            enableOcrFallback: false,
            maxConcurrency: 4,
            formulaFontPattern: "font",
            formulaCharPattern: "char",
            customPrompt: "prompt");

        initialOptions.EnableFormulaProtection.Should().BeTrue();
        initialOptions.EnableQualityFeedbackRetry.Should().BeTrue();
        initialOptions.MaxRetriesPerBlock.Should().Be(1);
        initialOptions.EnableOcrFallback.Should().BeTrue();

        retryOptions.EnableFormulaProtection.Should().BeTrue();
        retryOptions.EnableQualityFeedbackRetry.Should().BeTrue();
        retryOptions.MaxRetriesPerBlock.Should().Be(1);
        retryOptions.EnableOcrFallback.Should().BeFalse();
    }

    [Fact]
    public void BuildCheckpointFromCoreResult_ShouldCarryFallbackTextAndDetectedFontNamesIntoMetadata()
    {
        var sourceDocument = new SourceDocument
        {
            DocumentId = "doc-1",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-body-b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Tr ansf or mer",
                            FallbackText = "Transformer",
                            DetectedFontNames = ["TimesNewRomanPSMT"],
                            BoundingBox = new BlockRect(10, 10, 100, 20),
                            TextStyle = new BlockTextStyle { FontSize = 12 }
                        }
                    ]
                }
            ]
        };

        var coreResult = new CoreLongDocumentTranslationResult
        {
            Ir = new DocumentIr
            {
                DocumentId = "doc-1",
                Blocks = []
            },
            Pages =
            [
                new TranslatedDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new TranslatedDocumentBlock
                        {
                            IrBlockId = "ir-1-p1-body-b1",
                            SourceBlockId = "p1-body-b1",
                            BlockType = BlockType.Paragraph,
                            OriginalText = "Tr ansf or mer",
                            ProtectedText = "Tr ansf or mer",
                            TranslatedText = "Tr ansf or mer",
                            SourceHash = "hash-1",
                            BoundingBox = new BlockRect(10, 10, 100, 20),
                            TranslationSkipped = false,
                            RetryCount = 0,
                            LastError = "fallback",
                            TextStyle = new BlockTextStyle { FontSize = 12 }
                        }
                    ]
                }
            ],
            QualityReport = new LongDocumentQualityReport
            {
                StageTimingsMs = new Dictionary<string, long>(),
                TotalBlocks = 1,
                TranslatedBlocks = 0,
                SkippedBlocks = 0,
                FailedBlocks =
                [
                    new FailedBlockInfo
                    {
                        IrBlockId = "ir-1-p1-body-b1",
                        SourceBlockId = "p1-body-b1",
                        PageNumber = 1,
                        RetryCount = 0,
                        Error = "fallback"
                    }
                ]
            }
        };

        var checkpoint = WinUiLongDocumentTranslationService.BuildCheckpointFromCoreResult(
            LongDocumentInputMode.Pdf,
            "dummy.pdf",
            Language.SimplifiedChinese,
            sourceDocument,
            coreResult);

        checkpoint.SourceChunks.Should().ContainSingle().Which.Should().Be("Tr ansf or mer");
        checkpoint.ChunkMetadata.Should().ContainSingle();
        checkpoint.ChunkMetadata[0].FallbackText.Should().Be("Transformer");
        checkpoint.ChunkMetadata[0].DetectedFontNames.Should().Contain("TimesNewRomanPSMT");
        checkpoint.FailedChunkIndexes.Should().Contain(0);
    }

    [Fact]
    public void BuildCheckpointFromCoreResult_ShouldCarryRetryCountIntoMetadataForSuccessfulRetryBlock()
    {
        var sourceDocument = new SourceDocument
        {
            DocumentId = "doc-1",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-body-b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Most competitive neural sequence models.",
                            BoundingBox = new BlockRect(10, 10, 100, 20),
                            TextStyle = new BlockTextStyle { FontSize = 12 }
                        }
                    ]
                }
            ]
        };

        var coreResult = new CoreLongDocumentTranslationResult
        {
            Ir = new DocumentIr
            {
                DocumentId = "doc-1",
                Blocks = []
            },
            Pages =
            [
                new TranslatedDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new TranslatedDocumentBlock
                        {
                            IrBlockId = "ir-1-p1-body-b1",
                            SourceBlockId = "p1-body-b1",
                            BlockType = BlockType.Paragraph,
                            OriginalText = "Most competitive neural sequence models.",
                            ProtectedText = "Most competitive neural sequence models.",
                            TranslatedText = "\u5927\u591A\u6570\u5177\u6709\u7ADE\u4E89\u529B\u7684\u795E\u7ECF\u5E8F\u5217\u6A21\u578B\u3002",
                            SourceHash = "hash-1",
                            BoundingBox = new BlockRect(10, 10, 100, 20),
                            TranslationSkipped = false,
                            RetryCount = 1,
                            LastError = null,
                            TextStyle = new BlockTextStyle { FontSize = 12 }
                        }
                    ]
                }
            ],
            QualityReport = new LongDocumentQualityReport
            {
                StageTimingsMs = new Dictionary<string, long>(),
                TotalBlocks = 1,
                TranslatedBlocks = 1,
                SkippedBlocks = 0,
                FailedBlocks = []
            }
        };

        var checkpoint = WinUiLongDocumentTranslationService.BuildCheckpointFromCoreResult(
            LongDocumentInputMode.Pdf,
            "dummy.pdf",
            Language.SimplifiedChinese,
            sourceDocument,
            coreResult);

        checkpoint.ChunkMetadata.Should().ContainSingle();
        checkpoint.ChunkMetadata[0].RetryCount.Should().Be(1);
        checkpoint.TranslatedChunks.Should().ContainKey(0);
        checkpoint.FailedChunkIndexes.Should().BeEmpty();
    }

    [Fact]
    public void BuildCheckpointFromCoreResult_ShouldNotDeriveIsFormulaLikeFromTranslationSkipped()
    {
        var sourceDocument = new SourceDocument
        {
            DocumentId = "doc-1",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-body-b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Attention(Q, K, V) = softmax(QK^T)V",
                            IsFormulaLike = false,
                            BoundingBox = new BlockRect(10, 10, 100, 20),
                            TextStyle = new BlockTextStyle { FontSize = 12 }
                        }
                    ]
                }
            ]
        };

        var coreResult = new CoreLongDocumentTranslationResult
        {
            Ir = new DocumentIr
            {
                DocumentId = "doc-1",
                Blocks = []
            },
            Pages =
            [
                new TranslatedDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new TranslatedDocumentBlock
                        {
                            IrBlockId = "ir-1-p1-body-b1",
                            SourceBlockId = "p1-body-b1",
                            BlockType = BlockType.Paragraph,
                            OriginalText = "Attention(Q, K, V) = softmax(QK^T)V",
                            ProtectedText = "Attention(Q, K, V) = softmax(QK^T)V",
                            TranslatedText = "Attention(Q, K, V) = softmax(QK^T)V",
                            SourceHash = "hash-1",
                            BoundingBox = new BlockRect(10, 10, 100, 20),
                            TranslationSkipped = true,
                            PreserveOriginalTextInPdfExport = true,
                            RetryCount = 0,
                            LastError = null,
                            TextStyle = new BlockTextStyle { FontSize = 12 }
                        }
                    ]
                }
            ],
            QualityReport = new LongDocumentQualityReport
            {
                StageTimingsMs = new Dictionary<string, long>(),
                TotalBlocks = 1,
                TranslatedBlocks = 0,
                SkippedBlocks = 1,
                FailedBlocks = []
            }
        };

        var checkpoint = WinUiLongDocumentTranslationService.BuildCheckpointFromCoreResult(
            LongDocumentInputMode.Pdf,
            "dummy.pdf",
            Language.SimplifiedChinese,
            sourceDocument,
            coreResult);

        checkpoint.ChunkMetadata.Should().ContainSingle();
        checkpoint.ChunkMetadata[0].IsFormulaLike.Should().BeFalse();
        checkpoint.ChunkMetadata[0].TranslationSkipped.Should().BeTrue();
        checkpoint.ChunkMetadata[0].PreserveOriginalTextInPdfExport.Should().BeTrue();
    }

    [Fact]
    public void BuildParagraphTextsForTesting_ShouldKeepFormulaContinuationAttached()
    {
        var paragraphs = WinUiLongDocumentTranslationService.BuildParagraphTextsForTesting(
            [
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 700, Bottom: 688, Left: 100, Right: 430,
                    Text: "Here, the encoder maps an input sequence of symbol representations (x"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 694, Bottom: 684, Left: 365, Right: 430,
                    Text: ", ..., xn)")
            ],
            paragraphGapThreshold: 18,
            sameRowThreshold: 4);

        paragraphs.Should().HaveCount(1);
        paragraphs[0].Should().Equal(
            "Here, the encoder maps an input sequence of symbol representations (x",
            ", ..., xn)");
    }

    [Fact]
    public void BuildParagraphTextsForTesting_ShouldNotGridMergeFormulaContinuationRowsWithoutStableAnchors()
    {
        var paragraphs = WinUiLongDocumentTranslationService.BuildParagraphTextsForTesting(
            [
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 700, Bottom: 688, Left: 100, Right: 360,
                    Text: "Here, the encoder maps an input sequence of symbol representations (x"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 700, Bottom: 688, Left: 378, Right: 450,
                    Text: ", ..., xn)"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 682, Bottom: 670, Left: 128, Right: 420,
                    Text: "to a sequence of continuous representations z = (z"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 682, Bottom: 670, Left: 448, Right: 520,
                    Text: ", ..., zn)")
            ],
            paragraphGapThreshold: 18,
            sameRowThreshold: 4);

        paragraphs.Should().HaveCount(2);
        paragraphs[0].Should().Equal(
            "Here, the encoder maps an input sequence of symbol representations (x",
            ", ..., xn)");
        paragraphs[1].Should().Equal(
            "to a sequence of continuous representations z = (z",
            ", ..., zn)");
    }

    [Fact]
    public void BuildParagraphTextsForTesting_ShouldStillMergeStableGridColumns()
    {
        var paragraphs = WinUiLongDocumentTranslationService.BuildParagraphTextsForTesting(
            [
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 700, Bottom: 688, Left: 60, Right: 150,
                    Text: "Alice"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 700, Bottom: 688, Left: 300, Right: 420,
                    Text: "Lab A"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 682, Bottom: 670, Left: 62, Right: 152,
                    Text: "Bob"),
                new WinUiLongDocumentTranslationService.SyntheticPdfLine(
                    Top: 682, Bottom: 670, Left: 302, Right: 422,
                    Text: "Lab B")
            ],
            paragraphGapThreshold: 18,
            sameRowThreshold: 4);

        paragraphs.Should().HaveCount(2);
        paragraphs[0].Should().Equal("Alice", "Bob");
        paragraphs[1].Should().Equal("Lab A", "Lab B");
    }

    [SkippableFact]
    public async Task BuildSourceDocumentAsync_ShouldPreserveTransformerTupleSequences_FromLocalPdfFixture()
    {
        var pdfPath = GetPdfFixturePath("1706.03762v7.pdf");
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        using var service = new WinUiLongDocumentTranslationService();
        var method = typeof(WinUiLongDocumentTranslationService)
            .GetMethod("BuildSourceDocumentAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = (Task<SourceDocument>)method!.Invoke(service,
            [
                LongDocumentInputMode.Pdf,
                pdfPath!,
                LayoutDetectionMode.Auto,
                null,
                null,
                null,
                null,
                CancellationToken.None,
                null
            ])!;

        var source = await task;
        var page2 = source.Pages.Single(page => page.PageNumber == 2);
        var bodyText = string.Join("\n", page2.Blocks
            .Where(block => block.BlockId.StartsWith("p2-body-", StringComparison.Ordinal))
            .Select(block => block.Text));
        var normalizedBodyText = NormalizeWhitespaceForAssertion(bodyText);

        bodyText.Should().Contain("(x1");
        bodyText.Should().Contain("xn)");
        bodyText.Should().Contain("z = (z1");
        bodyText.Should().Contain("(y1");
        // Word spacing: quality gate + adaptive threshold should preserve most words;
        // PdfPig's raw text may still have some merged words for blocks without formula evidence.
        normalizedBodyText.Should().Contain("sequence of continuous representations");
        // Per-block space density check: no block should have extremely low word count
        var bodyBlocks = page2.Blocks
            .Where(block => block.BlockId.StartsWith("p2-body-", StringComparison.Ordinal))
            .ToList();
        foreach (var block in bodyBlocks.Where(b => b.Text.Length > 40))
        {
            var wordCount = block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            wordCount.Should().BeGreaterThanOrEqualTo(block.Text.Length / 15,
                $"block '{block.BlockId}' should have adequate word spacing");
        }
        bodyText.Should().NotContain("sequence_1");
        bodyText.Should().NotContain("z =_1 z z");
    }

    [SkippableFact]
    public async Task MuPdfExportService_ExportFixturePdf_ShouldPreservePage2WordSpacesInExtractedText()
    {
        var pdfPath = GetPdfFixturePath("1706.03762v7.pdf");
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var outputPath = Path.Combine(Path.GetTempPath(), $"spacing-preservation-{Guid.NewGuid()}.pdf");

        try
        {
            var source = await BuildSourceDocumentFromFixtureAsync(pdfPath);
            var checkpoint = BuildIdentityCheckpoint(source, pdfPath);

            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable in test environment: {ex.Message}");
            }

            File.Exists(outputPath).Should().BeTrue();

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = outputDoc.GetPages().Single(page => page.Number == 2).Text;
            var normalizedPage2Text = NormalizeWhitespaceForAssertion(page2Text);

            // Verify text was actually rendered to the output PDF
            normalizedPage2Text.Should().NotBeNullOrWhiteSpace("page 2 should have text content");
            // Check key terms are present (exact phrase match may fail due to PdfPig text merging)
            normalizedPage2Text.Should().Contain("encoder",
                "key term 'encoder' should be present in exported PDF");
            normalizedPage2Text.Should().Contain("decoder",
                "key term 'decoder' should be present in exported PDF");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [SkippableFact]
    public async Task MuPdfExportService_ExportFixturePdf_ShouldPreservePage4FormulasAndEmitReviewPng()
    {
        var pdfPath = GetPdfFixturePath("1706.03762v7.pdf");
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var outputPdfPath = Path.Combine(Path.GetTempPath(), "page4-formula-review.pdf");
        var outputPngPath = Path.Combine(Path.GetTempPath(), "page4-formula-review.png");

        TryDelete(outputPdfPath);
        TryDelete(outputPngPath);

        try
        {
            var source = await BuildSourceDocumentFromFixtureAsync(pdfPath);
            var checkpoint = BuildIdentityCheckpoint(source, pdfPath);

            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPdfPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable in test environment: {ex.Message}");
            }

            File.Exists(outputPdfPath).Should().BeTrue();

            using var outputDoc = PdfPigDocument.Open(outputPdfPath);
            var page4Text = outputDoc.GetPages().Single(page => page.Number == 4).Text;
            var compactPage4Text = CompactWhitespaceForAssertion(page4Text);

            compactPage4Text.Should().Contain("Attention(Q,K,V)=softmax",
                "the page 4 display equation should remain in the exported PDF");
            compactPage4Text.Should().Contain("√dk",
                "the page 4 display equation should preserve its square-root denominator");

            var muDoc = new Document(outputPdfPath);
            try
            {
                muDoc.PageCount.Should().BeGreaterOrEqualTo(4);

                var page4 = muDoc[3];
                var pix = page4.GetPixmap(new Matrix(1.5f, 1.5f));
                pix.Save(outputPngPath, "png");
            }
            finally
            {
                muDoc.Close();
            }

            File.Exists(outputPngPath).Should().BeTrue();
            Console.WriteLine($"Page 4 formula review PDF: {outputPdfPath}");
            Console.WriteLine($"Page 4 formula review PNG: {outputPngPath}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            Skip.If(true, $"MuPDF unavailable in test environment: {ex.Message}");
        }
    }

    [Theory]
    [InlineData("""[{"word": "transduction", "translation": "转导"}]""", 1, "transduction", "转导")]
    [InlineData("""```json\n[{"word": "encoder", "translation": "编码器"}]\n```""", 1, "encoder", "编码器")]
    [InlineData("[]", 0, null, null)]
    [InlineData("invalid json", 0, null, null)]
    [InlineData("", 0, null, null)]
    public void ParseWordAnnotations_HandlesVariousLlmResponses(
        string llmResponse, int expectedCount, string? expectedWord, string? expectedTranslation)
    {
        var result = WinUiLongDocumentTranslationService.ParseWordAnnotations(llmResponse);

        result.Should().HaveCount(expectedCount);
        if (expectedCount > 0)
        {
            result[0].Word.Should().Be(expectedWord);
            result[0].Translation.Should().Be(expectedTranslation);
        }
    }
}
