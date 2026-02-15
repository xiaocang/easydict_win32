using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

public class LongDocumentTranslationServiceTests
{
    [Fact]
    public async Task TranslateAsync_ShouldRetainIrMetadata()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
        var source = BuildSourceDocument();

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google"
        });

        result.Ir.Blocks.Should().HaveCount(2);
        result.Ir.Blocks[0].PageNumber.Should().Be(1);
        result.Ir.Blocks[0].SourceBlockId.Should().Be("p1-b1");
        result.Ir.Blocks[0].BoundingBox.Should().NotBeNull();
        result.Ir.Blocks[0].SourceHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TranslateAsync_ShouldProtectFormulaAndSkipFormulaBlockTranslation()
    {
        var calls = 0;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            calls++;
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-formula",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "formula",
                            BlockType = SourceBlockType.Formula,
                            Text = "E = mc^2"
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "text",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Energy is represented as $E = mc^2$."
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true
        });

        calls.Should().Be(1);
        result.Pages.SelectMany(p => p.Blocks).Single(b => b.SourceBlockId == "formula").TranslationSkipped.Should().BeTrue();
        result.Ir.Blocks.Single(b => b.SourceBlockId == "text").ProtectedText.Should().Contain("[[FORMULA:");
    }

    [Fact]
    public async Task TranslateAsync_ShouldUseOcrFallbackForScannedPage()
    {
        var sut = new LongDocumentTranslationService(
            translateWithService: FakeTranslate,
            ocrExtractor: (_, _) => Task.FromResult<string?>("OCR recovered text"));

        var source = new SourceDocument
        {
            DocumentId = "doc-ocr",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    IsScanned = true,
                    Blocks = []
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            EnableOcrFallback = true
        });

        result.Ir.Blocks.Should().ContainSingle();
        result.Ir.Blocks[0].OriginalText.Should().Be("OCR recovered text");
    }

    [Fact]
    public async Task TranslateAsync_ShouldApplyGlossary()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, _) => Task.FromResult(new TranslationResult
        {
            OriginalText = "hello",
            TranslatedText = "The model term appears here.",
            ServiceName = "fake",
            TargetLanguage = Language.English
        }));

        var result = await sut.TranslateAsync(BuildSourceDocument(), new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            Glossary = new Dictionary<string, string>
            {
                ["model"] = "engine"
            }
        });

        var translated = result.Pages.SelectMany(p => p.Blocks).First(b => !b.TranslationSkipped).TranslatedText;
        translated.Should().Contain("engine");
        translated.Should().NotContain("model");
    }

    [Fact]
    public async Task TranslateAsync_ShouldRetryFailedBlocksAndCollectQualityReport()
    {
        var attempts = 0;
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, _) =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.FromResult(new TranslationResult
            {
                OriginalText = "x",
                TranslatedText = "ok",
                ServiceName = "fake",
                TargetLanguage = Language.English
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-retry",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "retry me"
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            MaxRetriesPerBlock = 3
        });

        attempts.Should().Be(3);
        result.QualityReport.FailedBlocks.Should().BeEmpty();
        result.QualityReport.StageTimingsMs.Keys.Should().Contain(new[]
        {
            "ingest", "build-ir", "formula-protection", "translate", "structured-layout-output"
        });
        result.Pages[0].Blocks[0].RetryCount.Should().Be(2);
    }

    private static Task<TranslationResult> FakeTranslate(TranslationRequest request, string _, CancellationToken __)
    {
        return Task.FromResult(new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = $"translated:{request.Text}",
            ServiceName = "fake",
            TargetLanguage = request.ToLanguage
        });
    }

    private static SourceDocument BuildSourceDocument()
    {
        return new SourceDocument
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
                            BlockId = "p1-b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Hello world",
                            BoundingBox = new BlockRect(10, 20, 100, 40)
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-b2",
                            BlockType = SourceBlockType.Caption,
                            Text = "Figure caption",
                            ParentBlockId = "p1-b1"
                        }
                    ]
                }
            ]
        };
    }
}
