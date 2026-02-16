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
        result.Ir.Blocks.Single(b => b.SourceBlockId == "text").ProtectedText.Should().Contain("[[FORMULA_");
        result.Pages.SelectMany(p => p.Blocks).Single(b => b.SourceBlockId == "text").TranslatedText.Should().Contain("$E = mc^2$");
    }


    [Fact]
    public async Task TranslateAsync_ShouldRestoreInlineFormulaTokensAfterTranslation()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"ZH:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = Language.SimplifiedChinese
            }));

        var source = new SourceDocument
        {
            DocumentId = "doc-inline-formula",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "text-inline",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "The equation $a^2+b^2=c^2$ is important."
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

        var translated = result.Pages.SelectMany(p => p.Blocks).Single().TranslatedText;
        translated.Should().Contain("$a^2+b^2=c^2$");
        translated.Should().NotContain("[[FORMULA_");
    }

    [Fact]
    public async Task TranslateAsync_ShouldSkipTranslationWhenBlockContainsOnlyFormulaTokens()
    {
        var calls = 0;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            calls++;
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                ServiceName = "fake",
                TargetLanguage = Language.English
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-formula-only-inline",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "formula-inline-only",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "$x+y=z$"
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            EnableFormulaProtection = true
        });

        calls.Should().Be(0);
        result.Pages[0].Blocks[0].TranslationSkipped.Should().BeTrue();
        result.Pages[0].Blocks[0].TranslatedText.Should().Be("$x+y=z$");
    }


    [Fact]
    public async Task TranslateAsync_ShouldFallbackToOriginalWhenFormulaDelimitersBecomeUnbalanced()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = "x",
                TranslatedText = "ZH:[[FORMULA_0_ABCDEF12]](",
                ServiceName = "fake",
                TargetLanguage = Language.SimplifiedChinese
            }));

        var source = new SourceDocument
        {
            DocumentId = "doc-formula-restore-validation",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "text-inline-restore",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "The equation $a^2+b^2=c^2$ is important."
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

        var translated = result.Pages.SelectMany(p => p.Blocks).Single().TranslatedText;
        translated.Should().Be("The equation $a^2+b^2=c^2$ is important.");
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

    [Fact]
    public async Task TranslateAsync_ShouldPropagateCancellation()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, cancellationToken) =>
            Task.FromCanceled<TranslationResult>(cancellationToken));

        var source = BuildSourceDocument();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            MaxRetriesPerBlock = 3
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TranslateAsync_ShouldCapRetryCountWhenAllAttemptsFail()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, _) =>
            throw new InvalidOperationException("always fail"));

        var source = new SourceDocument
        {
            DocumentId = "doc-fail-retry",
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
                            Text = "failing text"
                        }
                    ]
                }
            ]
        };

        const int maxRetries = 2;
        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            MaxRetriesPerBlock = maxRetries
        });

        result.Pages[0].Blocks[0].RetryCount.Should().Be(maxRetries);
        result.Pages[0].Blocks[0].LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TranslateAsync_ShouldPreserveInputBlockOrderInOutput()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);

        var source = new SourceDocument
        {
            DocumentId = "doc-order",
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
                            Text = "one"
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "b10",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "ten"
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "b2",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "two"
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google"
        });

        result.Pages[0].Blocks.Select(b => b.SourceBlockId)
            .Should().ContainInOrder("b1", "b10", "b2");
    }

    [Fact]
    public async Task TranslateAsync_ShouldThrowForNegativeMaxRetries()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);

        var act = () => sut.TranslateAsync(BuildSourceDocument(), new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            MaxRetriesPerBlock = -1
        });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("MaxRetriesPerBlock");
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
