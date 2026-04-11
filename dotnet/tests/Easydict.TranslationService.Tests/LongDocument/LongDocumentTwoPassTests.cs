using System.Collections.Concurrent;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

/// <summary>
/// Integration tests for the Pass 1 (DocumentContext) → Pass 2 (block translation) flow:
///   * Pass 1 results are prepended to every block's CustomPrompt
///   * Pass 1 preservation hints rewrite the IR so matched blocks are skipped+preserved
///   * EnableDocumentContextPass = false leaves the original behavior intact
///   * ApplyPreservationHints (the IR rewriter) handles equals / contained-in / contains rules
/// </summary>
public class LongDocumentTwoPassTests
{
    [Fact]
    public async Task TranslateAsync_PrependsDocumentContextToBlockPrompts()
    {
        var seenPrompts = new ConcurrentBag<string?>();
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            // Pass 1 sends 1 page-map call + (since 1 page) 0 reduce calls; pass 2 sends N block calls.
            // The page-map call uses the canonical MapPagePrompt — capture every CustomPrompt to inspect both.
            seenPrompts.Add(request.CustomPrompt);

            if (request.CustomPrompt == DocumentContextExtractor.MapPagePrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = """
                        {"summary":"A short paper.","glossary":{"Hello":"你好","World":"世界"},"preservation_hints":[]}
                        """,
                    ServiceName = "fake",
                    TargetLanguage = request.ToLanguage,
                });
            }

            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        // Source text uses the SAME casing as glossary keys ("Hello", "World") so
        // the page-scoped case-sensitive filter matches. Both terms appear on page 1,
        // so every page-1 block should see both entries regardless of which one
        // actually mentions which term.
        var source = BuildSourceWith("Hello World.", "Another paragraph here.");

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = true,
        });

        // Block prompts (i.e. those NOT equal to the MapPagePrompt) must contain Summary + Glossary lines.
        var blockPrompts = seenPrompts
            .Where(p => p is not null && p != DocumentContextExtractor.MapPagePrompt)
            .ToList();
        blockPrompts.Should().NotBeEmpty();
        blockPrompts.Should().AllSatisfy(p =>
        {
            p.Should().Contain("Document summary: A short paper.");
            p.Should().Contain("Hello → 你好");
            p.Should().Contain("World → 世界");
        });
    }

    [Fact]
    public async Task TranslateAsync_PreservationHintsSkipMatchedBlocks()
    {
        var blockTranslateCount = 0;
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            if (request.CustomPrompt == DocumentContextExtractor.MapPagePrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = """
                        {"summary":"x","glossary":{},"preservation_hints":["Should not translate this exact text."]}
                        """,
                    ServiceName = "fake",
                    TargetLanguage = request.ToLanguage,
                });
            }

            Interlocked.Increment(ref blockTranslateCount);
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        var source = BuildSourceWith(
            "First paragraph that should be translated.",
            "Should not translate this exact text.",
            "Third paragraph that should also be translated.");

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = true,
        });

        // Only 2 of 3 blocks should hit the per-block translate path; the matched block stays verbatim.
        blockTranslateCount.Should().Be(2);

        var blocks = result.Pages[0].Blocks;
        blocks.Should().HaveCount(3);
        blocks[1].TranslationSkipped.Should().BeTrue();
        blocks[1].TranslatedText.Should().Be("Should not translate this exact text.");
        blocks[1].PreserveOriginalTextInPdfExport.Should().BeTrue();
        blocks[0].TranslatedText.Should().StartWith("T:");
        blocks[2].TranslatedText.Should().StartWith("T:");
    }

    [Fact]
    public async Task TranslateAsync_DocumentContextDisabled_NoExtraction()
    {
        var seenPrompts = new ConcurrentBag<string?>();
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            seenPrompts.Add(request.CustomPrompt);
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        var source = BuildSourceWith("Hello world.", "Second paragraph.");

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = false,
        });

        // No call may use the MapPagePrompt; no block prompt may contain "Document summary".
        seenPrompts.Should().NotContain(p => p == DocumentContextExtractor.MapPagePrompt);
        seenPrompts.Should().AllSatisfy(p =>
            (p ?? string.Empty).Should().NotContain("Document summary:"));
    }

    [Fact]
    public void ApplyPreservationHints_MatchesEquals()
    {
        var ir = BuildIr("BLEU 28.4", "regular paragraph");
        var rewritten = LongDocumentTranslationService.ApplyPreservationHints(ir, new[] { "BLEU 28.4" });

        rewritten.Blocks[0].TranslationSkipped.Should().BeTrue();
        rewritten.Blocks[0].PreserveOriginalTextInPdfExport.Should().BeTrue();
        rewritten.Blocks[1].TranslationSkipped.Should().BeFalse();
    }

    [Fact]
    public void ApplyPreservationHints_MatchesBlockContainedInHint()
    {
        // Hint is a long line; one block is a verbatim sub-cell.
        var ir = BuildIr("Transformer (base model)", "regular paragraph");
        var rewritten = LongDocumentTranslationService.ApplyPreservationHints(ir,
            new[] { "Transformer (base model) 65M 27.3 38.1 5e18" });

        rewritten.Blocks[0].TranslationSkipped.Should().BeTrue();
        rewritten.Blocks[1].TranslationSkipped.Should().BeFalse();
    }

    [Fact]
    public void ApplyPreservationHints_DoesNotPreserveProseContainingHintSubstring()
    {
        // Long prose paragraph that mentions a 25+ char identifier the LLM may have
        // returned as a hint. The paragraph must STILL be translated — only blocks
        // that ARE the hint (rule 1) or are sub-cells of the hint (rule 2) are preserved.
        // This locks in the fix for the regression on p3 of the 1706.03762v7 sample
        // where LayerNorm / multi-head substrings were swallowing whole paragraphs.
        var prose = "The encoder is composed of a stack of N = 6 identical layers. " +
                    "Each layer applies LayerNorm(x + Sublayer(x)) where Sublayer(x) is the " +
                    "function implemented by the sub-layer itself.";
        var ir = BuildIr(prose, "LayerNorm(x + Sublayer(x))");

        var rewritten = LongDocumentTranslationService.ApplyPreservationHints(
            ir, new[] { "LayerNorm(x + Sublayer(x))" });

        rewritten.Blocks[0].TranslationSkipped.Should().BeFalse(
            "long prose containing a hint substring must remain translatable");
        rewritten.Blocks[1].TranslationSkipped.Should().BeTrue(
            "a standalone block that EQUALS the hint is still preserved");
    }

    [Fact]
    public void ApplyPreservationHints_NoOpForEmptyHints()
    {
        var ir = BuildIr("paragraph");
        LongDocumentTranslationService.ApplyPreservationHints(ir, Array.Empty<string>())
            .Should().BeSameAs(ir);
    }

    // ---- Content-matched glossary injection (page-scoped filter) ----

    [Fact]
    public async Task TranslateAsync_GlossaryInjection_OnlyIncludesTermsAppearingOnPage()
    {
        // Page 1 has "Hello there"; page 2 has "World peace". Glossary covers three
        // terms; only Hello appears on page 1 and only World appears on page 2.
        var promptsByText = new ConcurrentDictionary<string, string?>();
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            if (request.CustomPrompt == DocumentContextExtractor.MapPagePrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = """
                        {"summary":"test doc","glossary":{"Hello":"你好","World":"世界","Foo":"酒吧"},"preservation_hints":[]}
                        """,
                    ServiceName = "fake",
                    TargetLanguage = request.ToLanguage,
                });
            }
            if (request.CustomPrompt != DocumentContextExtractor.ReduceSummaryPrompt)
            {
                promptsByText[request.Text] = request.CustomPrompt;
            }
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        var source = new SourceDocument
        {
            DocumentId = "doc-glossary-pages",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "b0",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Hello there",
                            BoundingBox = new BlockRect(10, 20, 400, 25),
                        }
                    ]
                },
                new SourceDocumentPage
                {
                    PageNumber = 2,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "World peace",
                            BoundingBox = new BlockRect(10, 20, 400, 25),
                        }
                    ]
                }
            ]
        };

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = true,
        });

        var page1Prompt = promptsByText["Hello there"];
        var page2Prompt = promptsByText["World peace"];

        page1Prompt.Should().Contain("Hello → 你好");
        page1Prompt.Should().NotContain("World → 世界");
        page1Prompt.Should().NotContain("Foo → 酒吧");

        page2Prompt.Should().Contain("World → 世界");
        page2Prompt.Should().NotContain("Hello → 你好");
        page2Prompt.Should().NotContain("Foo → 酒吧");
    }

    [Fact]
    public async Task TranslateAsync_GlossaryInjection_SamePageBlocksShareSameGlossary()
    {
        // Two blocks on page 1 — one says "Hello", the other says "Goodbye". Both
        // terms appear SOMEWHERE on page 1, so both blocks should get both entries
        // regardless of which individual block mentions which term. "Foo" appears
        // nowhere, so it's dropped.
        var promptsByText = new ConcurrentDictionary<string, string?>();
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            if (request.CustomPrompt == DocumentContextExtractor.MapPagePrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = """
                        {"summary":"","glossary":{"Hello":"你好","Goodbye":"再见","Foo":"酒吧"},"preservation_hints":[]}
                        """,
                    ServiceName = "fake",
                    TargetLanguage = request.ToLanguage,
                });
            }
            if (request.CustomPrompt != DocumentContextExtractor.ReduceSummaryPrompt)
            {
                promptsByText[request.Text] = request.CustomPrompt;
            }
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        var source = BuildSourceWith("Hello", "Goodbye");

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = true,
        });

        foreach (var blockText in new[] { "Hello", "Goodbye" })
        {
            var prompt = promptsByText[blockText];
            prompt.Should().Contain("Hello → 你好",
                $"'{blockText}' block should see the page-scoped glossary entry for Hello");
            prompt.Should().Contain("Goodbye → 再见",
                $"'{blockText}' block should see the page-scoped glossary entry for Goodbye");
            prompt.Should().NotContain("Foo → 酒吧",
                "Foo is not on the page so it must be filtered out");
        }
    }

    [Fact]
    public async Task TranslateAsync_GlossaryInjection_MatchesMultiWordTerms()
    {
        // Multi-word glossary keys must work because .Contains is a substring
        // match, not a word-split match. This test pins that behavior so a future
        // "improvement" that adds word-boundary matching doesn't regress it.
        var promptsByText = new ConcurrentDictionary<string, string?>();
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            if (request.CustomPrompt == DocumentContextExtractor.MapPagePrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = """
                        {"summary":"","glossary":{"self-attention mechanism":"自注意力机制"},"preservation_hints":[]}
                        """,
                    ServiceName = "fake",
                    TargetLanguage = request.ToLanguage,
                });
            }
            if (request.CustomPrompt != DocumentContextExtractor.ReduceSummaryPrompt)
            {
                promptsByText[request.Text] = request.CustomPrompt;
            }
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        var source = BuildSourceWith("The encoder uses a self-attention mechanism.");

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = true,
        });

        var prompt = promptsByText["The encoder uses a self-attention mechanism."];
        prompt.Should().Contain("self-attention mechanism → 自注意力机制");
    }

    [Fact]
    public async Task TranslateAsync_GlossaryInjection_OmitsGlossaryBlockWhenPageHasNoMatches()
    {
        // Glossary says "Transformer"; the block says "encoder". Nothing matches.
        // The glossary block header should NOT appear, but the summary should.
        var promptsByText = new ConcurrentDictionary<string, string?>();
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
        {
            if (request.CustomPrompt == DocumentContextExtractor.MapPagePrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = """
                        {"summary":"This is a test paper.","glossary":{"Transformer":"Transformer"},"preservation_hints":[]}
                        """,
                    ServiceName = "fake",
                    TargetLanguage = request.ToLanguage,
                });
            }
            if (request.CustomPrompt != DocumentContextExtractor.ReduceSummaryPrompt)
            {
                promptsByText[request.Text] = request.CustomPrompt;
            }
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage,
            });
        });

        var source = BuildSourceWith("The encoder processes input sequences.");

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "fake",
            EnableFormulaProtection = false,
            EnableDocumentContextPass = true,
        });

        var prompt = promptsByText["The encoder processes input sequences."];
        prompt.Should().Contain("Document summary: This is a test paper.",
            "summary is always prepended regardless of glossary match");
        prompt.Should().NotContain("Use these term translations consistently",
            "no glossary block when nothing on the page matches");
        prompt.Should().NotContain("Transformer → Transformer",
            "non-matching glossary entries must not appear in the prompt");
    }

    // ---- helpers ----

    private static SourceDocument BuildSourceWith(params string[] paragraphs)
    {
        var blocks = paragraphs
            .Select((text, i) => new SourceDocumentBlock
            {
                BlockId = $"b{i}",
                BlockType = SourceBlockType.Paragraph,
                Text = text,
                BoundingBox = new BlockRect(10, 20 + i * 30, 400, 25),
            })
            .ToList();
        return new SourceDocument
        {
            DocumentId = "doc-twopass",
            Pages =
            [
                new SourceDocumentPage { PageNumber = 1, Blocks = blocks }
            ]
        };
    }

    private static DocumentIr BuildIr(params string[] paragraphs)
    {
        var blocks = paragraphs
            .Select((text, i) => new DocumentBlockIr
            {
                IrBlockId = $"ir-{i}",
                PageNumber = 1,
                SourceBlockId = $"src-{i}",
                BlockType = BlockType.Paragraph,
                OriginalText = text,
                ProtectedText = text,
                SourceHash = $"hash-{i}",
            })
            .ToList();
        return new DocumentIr
        {
            DocumentId = "doc-rewrite",
            Blocks = blocks,
        };
    }
}
