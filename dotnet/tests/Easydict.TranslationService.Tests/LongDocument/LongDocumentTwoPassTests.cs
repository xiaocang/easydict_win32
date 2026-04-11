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

        var source = BuildSourceWith("Hello world.", "Another paragraph here.");

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
