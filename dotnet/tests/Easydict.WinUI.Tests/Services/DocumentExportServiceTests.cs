using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Pdf;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class DocumentExportServiceTests
{
    private static LongDocumentTranslationCheckpoint CreateTestCheckpoint(
        LongDocumentInputMode mode = LongDocumentInputMode.PlainText,
        string? sourceFilePath = null)
    {
        return new LongDocumentTranslationCheckpoint
        {
            InputMode = mode,
            SourceFilePath = sourceFilePath,
            SourceChunks = ["Hello world.", "This is a test.", "Goodbye."],
            ChunkMetadata =
            [
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 0, PageNumber = 1, SourceBlockId = "p1-body-b1",
                    SourceBlockType = SourceBlockType.Paragraph, OrderInPage = 0,
                    RegionType = LayoutRegionType.Body, RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 1
                },
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 1, PageNumber = 1, SourceBlockId = "p1-body-b2",
                    SourceBlockType = SourceBlockType.Paragraph, OrderInPage = 1,
                    RegionType = LayoutRegionType.Body, RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 0.5
                },
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 2, PageNumber = 1, SourceBlockId = "p1-body-b3",
                    SourceBlockType = SourceBlockType.Paragraph, OrderInPage = 2,
                    RegionType = LayoutRegionType.Body, RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 0
                }
            ],
            TranslatedChunks = new Dictionary<int, string>
            {
                [0] = "你好世界。",
                [1] = "这是一个测试。",
                [2] = "再见。"
            },
            FailedChunkIndexes = []
        };
    }

    private static LongDocumentTranslationCheckpoint CreateFailedFallbackCheckpoint(
        LongDocumentInputMode mode,
        string? sourceFilePath = null)
    {
        return new LongDocumentTranslationCheckpoint
        {
            InputMode = mode,
            SourceFilePath = sourceFilePath,
            TargetLanguage = Language.English,
            SourceChunks = ["Fa llback source block."],
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
                    RegionConfidence = 0.9,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = 1,
                    BoundingBox = new BlockRect(60, 680, 220, 40),
                    TextStyle = new BlockTextStyle
                    {
                        FontSize = 12,
                        RotationAngle = 0,
                        Alignment = TextAlignment.Left,
                        LineSpacing = 14
                    },
                    FallbackText = "Fallback source block.",
                    DetectedFontNames = ["TimesNewRomanPSMT"]
                }
            ],
            TranslatedChunks = [],
            FailedChunkIndexes = [0]
        };
    }

    #region PlainTextExportService

    [Fact]
    public void PlainTextExportService_SupportedExtensions_ContainsTxt()
    {
        var service = new PlainTextExportService();
        service.SupportedExtensions.Should().Contain(".txt");
    }

    [Fact]
    public void PlainTextExportService_Export_Monolingual_ProducesSingleFile()
    {
        var service = new PlainTextExportService();
        var checkpoint = CreateTestCheckpoint();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test-mono-{Guid.NewGuid()}.txt");

        try
        {
            var result = service.Export(checkpoint, "dummy.txt", outputPath, DocumentOutputMode.Monolingual);

            result.OutputPath.Should().Be(outputPath);
            result.BilingualOutputPath.Should().BeNull();
            File.Exists(outputPath).Should().BeTrue();

            var content = File.ReadAllText(outputPath);
            content.Should().Contain("你好世界。");
            content.Should().Contain("这是一个测试。");
            content.Should().Contain("再见。");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void PlainTextExportService_Export_Bilingual_ProducesInterleaved()
    {
        var service = new PlainTextExportService();
        var checkpoint = CreateTestCheckpoint();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test-bi-{Guid.NewGuid()}.txt");
        DocumentExportResult? result = null;

        try
        {
            result = service.Export(checkpoint, "dummy.txt", outputPath, DocumentOutputMode.Bilingual);

            // Bilingual-only mode: monolingual file deleted, output is bilingual path
            File.Exists(outputPath).Should().BeFalse();
            File.Exists(result.OutputPath).Should().BeTrue();

            var content = File.ReadAllText(result.OutputPath);
            // Should contain both original and translated
            content.Should().Contain("Hello world.");
            content.Should().Contain("你好世界。");
            content.Should().Contain("---");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (result != null && File.Exists(result.OutputPath)) File.Delete(result.OutputPath);
        }
    }

    [Fact]
    public void PlainTextExportService_Export_Both_ProducesTwoFiles()
    {
        var service = new PlainTextExportService();
        var checkpoint = CreateTestCheckpoint();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test-both-{Guid.NewGuid()}.txt");
        DocumentExportResult? result = null;

        try
        {
            result = service.Export(checkpoint, "dummy.txt", outputPath, DocumentOutputMode.Both);

            result.OutputPath.Should().Be(outputPath);
            result.BilingualOutputPath.Should().NotBeNull();
            File.Exists(outputPath).Should().BeTrue();
            File.Exists(result.BilingualOutputPath!).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (result?.BilingualOutputPath != null && File.Exists(result.BilingualOutputPath)) File.Delete(result.BilingualOutputPath);
        }
    }

    [Fact]
    public void PlainTextExportService_BuildBilingualOutputPath_VariousPaths()
    {
        PlainTextExportService.BuildBilingualOutputPath("/tmp/doc.txt")
            .Should().EndWith("doc-bilingual.txt");

        PlainTextExportService.BuildBilingualOutputPath("/tmp/my file.txt")
            .Should().EndWith("my file-bilingual.txt");
    }

    [Fact]
    public void PlainTextExportService_ComposeMonolingualText_FailedFallbackChunkStillShowsFailureMarker()
    {
        var checkpoint = CreateFailedFallbackCheckpoint(LongDocumentInputMode.PlainText);

        var content = PlainTextExportService.ComposeMonolingualText(checkpoint);

        content.Should().Contain("[Chunk 1 translation failed.]");
        content.Should().NotContain("Fallback source block.");
    }

    #endregion

    #region MarkdownExportService

    [Fact]
    public void MarkdownExportService_SupportedExtensions_ContainsMd()
    {
        var service = new MarkdownExportService();
        service.SupportedExtensions.Should().Contain(".md");
    }

    [Fact]
    public void MarkdownExportService_Export_Monolingual_ProducesSingleFile()
    {
        var service = new MarkdownExportService();
        var checkpoint = CreateTestCheckpoint(LongDocumentInputMode.Markdown);
        var outputPath = Path.Combine(Path.GetTempPath(), $"test-mono-{Guid.NewGuid()}.md");

        try
        {
            var result = service.Export(checkpoint, "dummy.md", outputPath, DocumentOutputMode.Monolingual);

            result.OutputPath.Should().Be(outputPath);
            result.BilingualOutputPath.Should().BeNull();
            File.Exists(outputPath).Should().BeTrue();

            var content = File.ReadAllText(outputPath);
            content.Should().Contain("你好世界。");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void MarkdownExportService_Export_Bilingual_HasBlockquotes()
    {
        var service = new MarkdownExportService();
        var checkpoint = CreateTestCheckpoint(LongDocumentInputMode.Markdown);
        var outputPath = Path.Combine(Path.GetTempPath(), $"test-bi-{Guid.NewGuid()}.md");
        DocumentExportResult? result = null;

        try
        {
            result = service.Export(checkpoint, "dummy.md", outputPath, DocumentOutputMode.Bilingual);

            File.Exists(result.OutputPath).Should().BeTrue();
            var content = File.ReadAllText(result.OutputPath);
            // Bilingual markdown uses blockquotes for original text
            content.Should().Contain("> Hello world.");
            content.Should().Contain("你好世界。");
            content.Should().Contain("---");
        }
        finally
        {
            if (result != null && File.Exists(result.OutputPath)) File.Delete(result.OutputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void MarkdownExportService_Export_Both_ProducesTwoFiles()
    {
        var service = new MarkdownExportService();
        var checkpoint = CreateTestCheckpoint(LongDocumentInputMode.Markdown);
        var outputPath = Path.Combine(Path.GetTempPath(), $"test-both-{Guid.NewGuid()}.md");
        DocumentExportResult? result = null;

        try
        {
            result = service.Export(checkpoint, "dummy.md", outputPath, DocumentOutputMode.Both);

            result.OutputPath.Should().Be(outputPath);
            result.BilingualOutputPath.Should().NotBeNull();
            File.Exists(outputPath).Should().BeTrue();
            File.Exists(result.BilingualOutputPath!).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (result?.BilingualOutputPath != null && File.Exists(result.BilingualOutputPath)) File.Delete(result.BilingualOutputPath);
        }
    }

    [Fact]
    public void MarkdownExportService_Heading_PreservesMarkdownFormat()
    {
        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Markdown,
            SourceChunks = ["Introduction"],
            ChunkMetadata =
            [
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 0, PageNumber = 1, SourceBlockId = "p1-body-b1",
                    SourceBlockType = SourceBlockType.Heading, OrderInPage = 0,
                    RegionType = LayoutRegionType.Body, RegionConfidence = 0.72,
                    RegionSource = LayoutRegionSource.BlockIdFallback, ReadingOrderScore = 1
                }
            ],
            TranslatedChunks = new Dictionary<int, string> { [0] = "介绍" },
            FailedChunkIndexes = []
        };

        var monolingual = MarkdownExportService.ComposeMonolingualMarkdown(checkpoint);
        monolingual.Should().Contain("### 介绍");
    }

    [Fact]
    public void MarkdownExportService_BuildBilingualOutputPath_VariousPaths()
    {
        MarkdownExportService.BuildBilingualOutputPath("/tmp/doc.md")
            .Should().EndWith("doc-bilingual.md");
    }

    [Fact]
    public void MarkdownExportService_ComposeMonolingualMarkdown_FailedFallbackChunkStillShowsFailureMarker()
    {
        var checkpoint = CreateFailedFallbackCheckpoint(LongDocumentInputMode.Markdown);

        var content = MarkdownExportService.ComposeMonolingualMarkdown(checkpoint);

        content.Should().Contain("> *[Chunk 1 translation failed.]*");
        content.Should().NotContain("Fallback source block.");
    }

    #endregion

    #region PdfExportService

    [Fact]
    public void PdfExportService_SupportedExtensions_ContainsPdf()
    {
        var service = new PdfExportService();
        service.SupportedExtensions.Should().Contain(".pdf");
    }

    [Fact]
    public void PdfExportService_BuildBilingualOutputPath_VariousPaths()
    {
        PdfExportService.BuildBilingualOutputPath("/tmp/doc.pdf")
            .Should().EndWith("doc-bilingual.pdf");

        PdfExportService.BuildBilingualOutputPath("/tmp/my document.pdf")
            .Should().EndWith("my document-bilingual.pdf");
    }

    [Fact]
    public void PdfExportCheckpointTextResolver_TryGetRenderableText_UsesSourceFallbackForFailedChunk()
    {
        var checkpoint = CreateFailedFallbackCheckpoint(LongDocumentInputMode.Pdf, "dummy.pdf");

        var found = PdfExportCheckpointTextResolver.TryGetRenderableText(
            checkpoint,
            0,
            out var text,
            out var usesSourceFallback);

        found.Should().BeTrue();
        usesSourceFallback.Should().BeTrue();
        text.Should().Be("Fallback source block.");
        checkpoint.TranslatedChunks.Should().BeEmpty();
        checkpoint.FailedChunkIndexes.Should().Contain(0);
    }

    [Fact]
    public void MuPdfExportService_BuildTranslatedBlockLookup_IncludesFailedSourceFallbackChunk()
    {
        var checkpoint = CreateFailedFallbackCheckpoint(LongDocumentInputMode.Pdf, "dummy.pdf");

        var lookup = MuPdfExportService.BuildTranslatedBlockLookup(checkpoint);

        lookup.Should().ContainKey(1);
        lookup[1].Should().ContainSingle();
        lookup[1][0].TranslatedText.Should().Be("Fallback source block.");
        lookup[1][0].TranslationSkipped.Should().BeFalse();
        lookup[1][0].UsesSourceFallback.Should().BeTrue();
        lookup[1][0].DetectedFontNames.Should().Contain("TimesNewRomanPSMT");
    }

    [Fact]
    public void PdfExportService_ExportPdfWithCoordinateBackfill_RendersFailedSourceFallbackWithoutMissingTranslationIssue()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid()}.pdf");
        var outputPath = Path.Combine(Path.GetTempPath(), $"output-{Guid.NewGuid()}.pdf");

        try
        {
            using (var sourceDoc = new PdfDocument())
            {
                var page = sourceDoc.AddPage();
                page.Width = 612;
                page.Height = 792;
                sourceDoc.Save(sourcePath);
            }

            var checkpoint = CreateFailedFallbackCheckpoint(LongDocumentInputMode.Pdf, sourcePath);

            var metrics = PdfExportService.ExportPdfWithCoordinateBackfill(checkpoint, sourcePath, outputPath);

            File.Exists(outputPath).Should().BeTrue();
            metrics.CandidateBlocks.Should().Be(1);
            metrics.RenderedBlocks.Should().Be(1);
            metrics.BlockIssues.Should().NotBeNull();
            metrics.BlockIssues!.Select(issue => issue.Kind).Should().Contain("rendered-source-fallback");
            metrics.BlockIssues!.Select(issue => issue.Kind).Should().NotContain("missing-translation");
            checkpoint.TranslatedChunks.Should().BeEmpty();
            checkpoint.FailedChunkIndexes.Should().Contain(0);
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    #endregion
}
