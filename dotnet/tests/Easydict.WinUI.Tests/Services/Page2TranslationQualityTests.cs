using System.Reflection;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class Page2TranslationQualityTests
{
    private const string PdfFixture = "1706.03762v7.pdf";
    private const string IntroPhrase = "Most competitive neural sequence transduction models";
    private const string ContinuousPhrase = "sequence of continuous representations";

    private static string GetPdfFixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "Pdf", PdfFixture);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    [SkippableFact]
    public async Task Page2_SourceExtraction_ShouldPreserveWordSpacingAndFormulas()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (_, page2Blocks) = await BuildPage2SourceBlocksAsync(pdfPath);
        var bodyBlocks = page2Blocks
            .Where(b => b.BlockId.StartsWith("p2-body-", StringComparison.Ordinal))
            .ToList();

        bodyBlocks.Should().NotBeEmpty();

        var bodyText = string.Join("\n", bodyBlocks.Select(b => b.Text));
        var normalized = Normalize(bodyText);

        normalized.Should().Contain(IntroPhrase);
        normalized.Should().Contain(ContinuousPhrase);
        bodyText.Should().Contain("(x1");
        bodyText.Should().Contain("xn)");
        bodyText.Should().Contain("z = (z1");
        bodyText.Should().Contain("(y1");
        bodyText.Should().NotContain("sequence_1");
        bodyText.Should().NotContain("z =_1");
    }

    [SkippableFact]
    public async Task Page2_SourceFallback_ShouldPreferFallbackTextOverOriginalChunkText()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: true);

        checkpoint.FailedChunkIndexes.Should().HaveCount(1);

        var failedIndex = checkpoint.FailedChunkIndexes.First();
        var metadata = checkpoint.ChunkMetadata[failedIndex];

        metadata.FallbackText.Should().NotBeNullOrWhiteSpace();
        metadata.DetectedFontNames.Should().NotBeNullOrEmpty();
        checkpoint.TranslatedChunks.Should().NotContainKey(failedIndex);

        var found = PdfExportCheckpointTextResolver.TryGetRenderableText(
            checkpoint,
            failedIndex,
            out var renderText,
            out var usesSourceFallback);

        found.Should().BeTrue();
        usesSourceFallback.Should().BeTrue();
        renderText.Should().Be(metadata.FallbackText);
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldRenderFallbackBlocksWithoutBrokenLatinSpacing()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: true);

        var failedIndex = checkpoint.FailedChunkIndexes.First();
        var metadata = checkpoint.ChunkMetadata[failedIndex];

        // The failed block should have FallbackText (different from its Text)
        // or at minimum have meaningful source text for fallback rendering.
        var fallbackOrSource = metadata.FallbackText ?? checkpoint.SourceChunks[failedIndex];
        fallbackOrSource.Should().NotBeNullOrWhiteSpace();

        var outputPath = Path.Combine(Path.GetTempPath(), $"page2-fallback-{Guid.NewGuid():N}.pdf");
        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);

            // The exported page should have text content (not empty)
            page2Text.Should().NotBeNullOrWhiteSpace();

            // The fallback block's source text should appear in the exported PDF
            var expectedSnippet = Normalize(fallbackOrSource).Split(' ').Take(3).ToArray();
            if (expectedSnippet.Length >= 3)
                page2Text.Should().Contain(string.Join(" ", expectedSnippet));

            // Latin terms in fallback blocks should not have broken spacing
            page2Text.Should().NotContain("Tr ansf or mer");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SkippableFact]
    public async Task Page2_MuPdfExport_ShouldPreserveInlineLatinTermsInTranslatedBlocks()
    {
        var pdfPath = GetPdfFixturePath();
        Skip.IfNot(File.Exists(pdfPath), $"PDF fixture not found: {pdfPath}");

        var (source, _) = await BuildPage2SourceBlocksAsync(pdfPath);
        var checkpoint = BuildMockTranslationCheckpoint(source, pdfPath, failOneBlock: false);

        OverrideTranslatedChunkForNeedle(
            checkpoint,
            "ConvS2S",
            "\u8FD9\u4E9B\u6A21\u578B\u5305\u62EC ConvS2S\uFF0C\u5E76\u80FD\u591F\u5E76\u884C\u8BA1\u7B97\u6240\u6709\u8F93\u5165\u4E0E\u8F93\u51FA\u4F4D\u7F6E\u3002");
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            "ByteNet",
            "\u76F8\u5173\u5DE5\u4F5C\u5982 ByteNet \u4E0E ConvS2S \u4E5F\u91C7\u7528\u4E86\u7C7B\u4F3C\u7ED3\u6784\u3002");
        OverrideTranslatedChunkForNeedle(
            checkpoint,
            "Transformer",
            "\u800C\u5728 Transformer \u4E2D\uFF0C\u8FD9\u4E00\u64CD\u4F5C\u88AB\u51CF\u5C11\u5230\u56FA\u5B9A\u6B21\u6570\u3002");

        var outputPath = Path.Combine(Path.GetTempPath(), $"page2-mixed-latin-{Guid.NewGuid():N}.pdf");
        try
        {
            try
            {
                var exportService = new MuPdfExportService();
                exportService.Export(checkpoint, pdfPath, outputPath, DocumentOutputMode.Monolingual);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                Skip.If(true, $"MuPDF unavailable: {ex.Message}");
            }

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var page2Text = Normalize(outputDoc.GetPages().Single(p => p.Number == 2).Text);

            page2Text.Should().Contain("Transformer");
            page2Text.Should().Contain("ByteNet");
            page2Text.Should().Contain("ConvS2S");
            page2Text.Should().NotContain("Tr ansf or mer");
            page2Text.Should().NotContain("Byt eNet");
            page2Text.Should().NotContain("Conv S2S");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static async Task<(SourceDocument source, IReadOnlyList<SourceDocumentBlock> page2Blocks)>
        BuildPage2SourceBlocksAsync(string pdfPath)
    {
        using var service = new Easydict.WinUI.Services.LongDocumentTranslationService();
        var method = typeof(Easydict.WinUI.Services.LongDocumentTranslationService)
            .GetMethod("BuildSourceDocumentAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = (Task<SourceDocument>)method!.Invoke(service,
        [
            LongDocumentInputMode.Pdf,
            pdfPath,
            LayoutDetectionMode.Auto,
            null, null, null, null,
            CancellationToken.None,
            null
        ])!;

        var source = await task;
        var page2 = source.Pages.Single(p => p.PageNumber == 2);
        return (source, page2.Blocks);
    }

    private static LongDocumentTranslationCheckpoint BuildMockTranslationCheckpoint(
        SourceDocument source,
        string pdfPath,
        bool failOneBlock)
    {
        var sourceChunks = new List<string>();
        var chunkMetadata = new List<LongDocumentChunkMetadata>();
        var translatedChunks = new Dictionary<int, string>();
        var failedIndexes = new HashSet<int>();
        var chunkIndex = 0;
        var failedBlockId = failOneBlock ? SelectFailedBlockId(source) : null;

        foreach (var page in source.Pages.OrderBy(p => p.PageNumber))
        {
            for (var order = 0; order < page.Blocks.Count; order++)
            {
                var block = page.Blocks[order];
                sourceChunks.Add(block.Text);

                chunkMetadata.Add(new LongDocumentChunkMetadata
                {
                    ChunkIndex = chunkIndex,
                    PageNumber = page.PageNumber,
                    SourceBlockId = block.BlockId,
                    SourceBlockType = block.BlockType,
                    IsFormulaLike = block.IsFormulaLike,
                    OrderInPage = order,
                    RegionType = LayoutRegionType.Body,
                    RegionConfidence = 1,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = page.Blocks.Count <= 1
                        ? 1
                        : 1 - order / (double)(page.Blocks.Count - 1),
                    BoundingBox = block.BoundingBox,
                    TextStyle = block.TextStyle,
                    FormulaCharacters = block.FormulaCharacters,
                    FallbackText = block.FallbackText,
                    DetectedFontNames = block.DetectedFontNames
                });

                var isFailedFallbackTarget = failOneBlock
                    && page.PageNumber == 2
                    && string.Equals(block.BlockId, failedBlockId, StringComparison.Ordinal);

                if (isFailedFallbackTarget)
                {
                    failedIndexes.Add(chunkIndex);
                }
                else
                {
                    translatedChunks[chunkIndex] = block.IsFormulaLike || block.BlockType == SourceBlockType.Formula
                        ? block.Text
                        : MockTranslate(block.Text);
                }

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
            FailedChunkIndexes = failedIndexes
        };
    }

    private static string SelectFailedBlockId(SourceDocument source)
    {
        var page2Blocks = source.Pages
            .Single(p => p.PageNumber == 2)
            .Blocks
            .Where(b => b.BlockId.StartsWith("p2-body-", StringComparison.Ordinal))
            .ToList();

        page2Blocks.Should().NotBeEmpty();

        // Prefer a block with FallbackText (meaning letter-reconstruction differs from PdfPig),
        // otherwise pick the longest body block as a reasonable fallback target.
        var preferred = page2Blocks.FirstOrDefault(
                b => !string.IsNullOrWhiteSpace(b.FallbackText))
            ?? page2Blocks.OrderByDescending(b => b.Text.Length).First();

        return preferred.BlockId;
    }

    private static void OverrideTranslatedChunkForNeedle(
        LongDocumentTranslationCheckpoint checkpoint,
        string needle,
        string translatedText)
    {
        var chunkIndex = checkpoint.ChunkMetadata
            .Where(m => m.PageNumber == 2)
            .Select(m => m.ChunkIndex)
            .FirstOrDefault(i => checkpoint.SourceChunks[i].Contains(needle, StringComparison.Ordinal));

        checkpoint.SourceChunks[chunkIndex].Should().Contain(needle);
        checkpoint.TranslatedChunks[chunkIndex] = translatedText;
    }

    private static string MockTranslate(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        var placeholders = Regex.Matches(source, @"\{v\d+\}")
            .Cast<Match>()
            .Select(match => match.Value);

        return $"translated-{new string('x', Math.Max(6, source.Length / 4))}{string.Concat(placeholders)}";
    }
}
