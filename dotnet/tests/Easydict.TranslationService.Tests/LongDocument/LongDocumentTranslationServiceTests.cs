using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.FormulaProtection;
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
        result.Ir.Blocks.Single(b => b.SourceBlockId == "text").ProtectedText.Should().Contain("{v");
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
        translated.Should().NotContain("{v");
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
                TranslatedText = "ZH:{v0}(",
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

    [Fact]
    public async Task TranslateAsync_ShouldUseNumericPlaceholdersForMultipleFormulas()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                ServiceName = "fake",
                TargetLanguage = Language.English
            }));

        var source = new SourceDocument
        {
            DocumentId = "doc-multiple-formulas",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "text-multi",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "The equations $a^2+b^2=c^2$ and $E=mc^2$ are famous."
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

        var protectedText = result.Ir.Blocks.Single(b => b.SourceBlockId == "text-multi").ProtectedText;
        protectedText.Should().Contain("{v0}");
        protectedText.Should().Contain("{v1}");
        protectedText.Should().NotContain("$a^2+b^2=c^2$");
        protectedText.Should().NotContain("$E=mc^2$");
    }

    [Fact]
    public async Task TranslateAsync_ShouldRestoreNumericPlaceholdersInCorrectOrder()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                ServiceName = "fake",
                TargetLanguage = Language.English
            }));

        var source = new SourceDocument
        {
            DocumentId = "doc-restore-order",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "text-order",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "First $x=1$ then $y=2$ finally $z=3$."
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

        var translated = result.Pages.SelectMany(p => p.Blocks).Single().TranslatedText;
        translated.Should().Be("First $x=1$ then $y=2$ finally $z=3$.");
    }

    [Fact]
    public async Task TranslateAsync_ShouldHandleMixedFormulaAndText()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = "x",
                TranslatedText = "The {v0} represents energy in physics.",
                ServiceName = "fake",
                TargetLanguage = Language.English
            }));

        var source = new SourceDocument
        {
            DocumentId = "doc-mixed",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "text-mixed",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "The $E=mc^2$ represents energy in physics."
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

        var translated = result.Pages.SelectMany(p => p.Blocks).Single().TranslatedText;
        translated.Should().Be("The $E=mc^2$ represents energy in physics.");
    }

    [Fact]
    public async Task TranslateAsync_QualityFeedbackRetry_PartialRestoreTriggersRetry()
    {
        // The protected block will have 3 hard placeholders (3 Greek letters).
        // Call 1: LLM returns a translation that contains only {v0} and {v1} (drops {v2}) → PartialRestore.
        // Call 2: LLM returns a translation that contains all three placeholders → FullRestore.
        var callCount = 0;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            callCount++;
            var text = callCount == 1
                ? "Tr1 {v0} and {v1} only"
                : "Tr2 {v0} and {v1} and {v2}";
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = text,
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-qfr",
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
                            Text = "Use \\alpha and \\beta and \\gamma."
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            MaxRetriesPerBlock = 2,
            EnableQualityFeedbackRetry = true
        });

        callCount.Should().Be(2);
        var block = result.Pages[0].Blocks.Single();
        block.TranslatedText.Should().Contain("\\alpha");
        block.TranslatedText.Should().Contain("\\beta");
        block.TranslatedText.Should().Contain("\\gamma");
        block.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task TranslateAsync_QualityFeedbackRetry_DisabledDoesNotRetry()
    {
        var callCount = 0;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            callCount++;
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = "Tr {v0} and {v1} only", // drops {v2}
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-qfr-off",
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
                            Text = "Use \\alpha and \\beta and \\gamma."
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            MaxRetriesPerBlock = 2
            // EnableQualityFeedbackRetry = false (default)
        });

        callCount.Should().Be(1);
        result.Pages[0].Blocks.Single().RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task TranslateAsync_ShouldStripSyntheticDelimitersForExactSoftSpans()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (_, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = "x",
                TranslatedText = "Translated text keeps $(x1, ..., xn)$ and $z = (z1, ..., zn)$ intact.",
                ServiceName = "fake",
                TargetLanguage = Language.SimplifiedChinese
            }));

        var source = new SourceDocument
        {
            DocumentId = "doc-soft-strip",
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
                            Text = "Most competitive models use an encoder-decoder structure, with input sequence (x1, ..., xn) and continuous representations z = (z1, ..., zn)."
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

        var translated = result.Pages[0].Blocks.Single().TranslatedText;
        translated.Should().Contain("(x1, ..., xn)");
        translated.Should().Contain("z = (z1, ..., zn)");
        translated.Should().NotContain("$(x1, ..., xn)$");
        translated.Should().NotContain("$z = (z1, ..., zn)$");
    }

    [Fact]
    public async Task TranslateAsync_QualityFeedbackRetry_ExactSoftSpanMutationFallsBackToOriginalAndTagsLastError()
    {
        var callCount = 0;
        var prompts = new List<string?>();
        const string originalText =
            "Most competitive neural sequence transduction models have an encoder-decoder structure. " +
            "The encoder maps the input sequence (x1, ..., xn) to continuous representations z = (z1, ..., zn).";

        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            callCount++;
            prompts.Add(request.CustomPrompt);
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = callCount == 1
                    ? "First attempt rewrites the input sequence as sequence1 and the continuous representation as sequence2."
                    : "Second attempt still rewrites the input sequence as sequence1 and the continuous representation as sequence2.",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-soft-fallback",
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
                            Text = originalText
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true,
            EnableQualityFeedbackRetry = true,
            MaxRetriesPerBlock = 1
        });

        callCount.Should().Be(2);
        prompts.Should().HaveCount(2);
        prompts[1].Should().Contain("Copy every technical symbol sequence inside synthetic $...$ verbatim");
        prompts[1].Should().Contain("do not keep the synthetic $ delimiters");

        var block = result.Pages[0].Blocks.Single();
        block.TranslatedText.Should().Be(originalText);
        block.RetryCount.Should().Be(1);
        block.LastError.Should().Contain("quality-feedback:");
        block.LastError.Should().Contain("soft=Failed");
    }

    [Fact]
    public async Task TranslateAsync_QualityFeedbackRetry_WithCharacterLevelEvidenceAndExactSoftCandidates_BypassesCharacterPathAndFallsBack()
    {
        var callCount = 0;
        var prompts = new List<string?>();
        var protectedRequests = new List<string>();
        const string originalText =
            "Most competitive neural sequence transduction models have an encoder-decoder structure. " +
            "The encoder maps the input sequence (x1, ..., xn) to continuous representations z = (z1, ..., zn).";

        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            callCount++;
            prompts.Add(request.CustomPrompt);
            protectedRequests.Add(request.Text);
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = callCount == 1
                    ? "First attempt rewrites the input sequence as sequence1 and the continuous representation as sequence2."
                    : "Second attempt still rewrites the input sequence as sequence1 and the continuous representation as sequence2.",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-soft-fallback-char-level",
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
                            Text = originalText,
                            CharacterLevelProtectedText =
                                "Most competitive neural sequence transduction models have an encoder-decoder structure. " +
                                "The encoder maps the input sequence {v0} to continuous representations {v1}.",
                            CharacterLevelTokens =
                            [
                                new FormulaToken(FormulaTokenType.InlineMath, "(x1, ..., xn)", "{v0}", "(x1, ..., xn)"),
                                new FormulaToken(FormulaTokenType.InlineEquation, "z = (z1, ..., zn)", "{v1}", "z = (z1, ..., zn)")
                            ]
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true,
            EnableQualityFeedbackRetry = true,
            MaxRetriesPerBlock = 1
        });

        callCount.Should().Be(2);
        protectedRequests.Should().HaveCount(2);
        protectedRequests[0].Should().Contain("$(x1, ..., xn)$");
        protectedRequests[0].Should().Contain("$z = (z1, ..., zn)$");
        protectedRequests[0].Should().NotContain("{v0}");
        protectedRequests[0].Should().NotContain("{v1}");
        prompts[1].Should().Contain("Copy every technical symbol sequence inside synthetic $...$ verbatim");
        prompts[1].Should().Contain("do not keep the synthetic $ delimiters");

        var block = result.Pages[0].Blocks.Single();
        block.TranslatedText.Should().Be(originalText);
        block.RetryCount.Should().Be(1);
        block.LastError.Should().Contain("quality-feedback:");
        block.LastError.Should().Contain("soft=Failed");
    }

    [Fact]
    public async Task TranslateSingleBlock_ShouldRetryWithFallbackText_WhenTranslationFails()
    {
        var callCount = 0;
        var requestTexts = new List<string>();

        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            callCount++;
            requestTexts.Add(request.Text);
            if (callCount == 1)
                throw new TranslationException("Network error") { ErrorCode = TranslationErrorCode.NetworkError };
            if (callCount == 2)
                throw new TranslationException("Network error again") { ErrorCode = TranslationErrorCode.NetworkError };

            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"translated:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-fallback",
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
                            Text = "Mostcompetitiveneural sequencetransduction",
                            FallbackText = "Most competitive neural sequence transduction",
                            BoundingBox = new BlockRect(10, 20, 400, 40)
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ServiceId = "fake",
            ToLanguage = Language.SimplifiedChinese,
            MaxRetriesPerBlock = 1
        });

        callCount.Should().BeGreaterThanOrEqualTo(3,
            "should retry with original text, then with FallbackText");
        requestTexts.Last().Should().Contain("Most competitive neural",
            "the final successful request should use the FallbackText with correct spacing");

        var block = result.Pages[0].Blocks.Single();
        block.TranslatedText.Should().StartWith("translated:");
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
