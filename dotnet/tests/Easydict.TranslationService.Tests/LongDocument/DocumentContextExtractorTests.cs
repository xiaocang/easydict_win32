using System.Collections.Concurrent;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

public class DocumentContextExtractorTests
{
    private static readonly LongDocumentTranslationOptions DefaultOptions = new()
    {
        ToLanguage = Language.SimplifiedChinese,
        ServiceId = "fake",
        MaxConcurrency = 4,
    };

    [Fact]
    public async Task ExtractAsync_ParsesSummaryGlossaryAndHints()
    {
        var json = """
            {
              "summary": "A paper about Transformers and self-attention.",
              "glossary": {"Transformer": "Transformer", "self-attention": "自注意力"},
              "preservation_hints": ["BLEU 28.4", "Vaswani et al."]
            }
            """;
        var sut = BuildExtractor(_ => json);
        var ir = BuildIr([(1, "Some content about Transformers")]);

        var ctx = await sut.ExtractAsync(ir, DefaultOptions);

        ctx.Summary.Should().Contain("Transformers");
        ctx.Glossary.Should().HaveCount(2);
        ctx.Glossary["Transformer"].Should().Be("Transformer");
        ctx.Glossary["self-attention"].Should().Be("自注意力");
        ctx.PreservationHints.Should().Contain("BLEU 28.4");
        ctx.PreservationHints.Should().Contain("Vaswani et al.");
    }

    [Fact]
    public async Task ExtractAsync_DegradesGracefullyOnInvalidJson()
    {
        var sut = BuildExtractor(_ => "this is not json at all");
        var ir = BuildIr([(1, "page 1 text")]);

        var ctx = await sut.ExtractAsync(ir, DefaultOptions);

        ctx.Should().NotBeNull();
        ctx.Summary.Should().BeEmpty();
        ctx.Glossary.Should().BeEmpty();
        ctx.PreservationHints.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_StripsCodeFenceWrapper()
    {
        var fenced = "```json\n{\"summary\":\"hi\",\"glossary\":{},\"preservation_hints\":[]}\n```";
        var sut = BuildExtractor(_ => fenced);
        var ir = BuildIr([(1, "x")]);

        var ctx = await sut.ExtractAsync(ir, DefaultOptions);

        ctx.Summary.Should().Be("hi");
    }

    [Fact]
    public async Task ExtractAsync_MapsOneCallPerPage()
    {
        var calls = new ConcurrentBag<(string Text, string CustomPrompt)>();
        var sut = BuildExtractor(req =>
        {
            calls.Add((req.Text, req.CustomPrompt ?? string.Empty));
            return """{"summary":"x","glossary":{},"preservation_hints":[]}""";
        });

        var ir = BuildIr(
        [
            (1, "page one text"),
            (1, "page one second block"),
            (2, "page two text"),
            (3, "page three text"),
            (4, "page four text"),
            (5, "page five text"),
        ]);

        await sut.ExtractAsync(ir, DefaultOptions with { MaxConcurrency = 4 });

        // 5 unique pages → 5 map calls + 1 reduce call (because >1 non-empty summary).
        calls.Count.Should().Be(6);

        var mapCalls = calls.Where(c => c.CustomPrompt == DocumentContextExtractor.MapPagePrompt).ToList();
        mapCalls.Should().HaveCount(5);

        // Page 1's map call concatenates both blocks; no map call should mix pages.
        mapCalls.Should().Contain(c => c.Text.Contains("page one text") && c.Text.Contains("page one second block"));
        mapCalls.Should().Contain(c => c.Text.Trim() == "page two text");
        mapCalls.Should().NotContain(c => c.Text.Contains("page one text") && c.Text.Contains("page two text"));

        var reduceCalls = calls.Where(c => c.CustomPrompt == DocumentContextExtractor.ReduceSummaryPrompt).ToList();
        reduceCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExtractAsync_MapsRespectMaxConcurrency()
    {
        var inFlight = 0;
        var peakObserved = 0;
        var lockObj = new object();

        var sut = new DocumentContextExtractor(async (req, _, ct) =>
        {
            // Skip the reduce call — only meter map fan-out.
            var isMap = req.CustomPrompt == DocumentContextExtractor.MapPagePrompt;
            if (isMap)
            {
                lock (lockObj)
                {
                    inFlight++;
                    if (inFlight > peakObserved) peakObserved = inFlight;
                }
            }
            try
            {
                await Task.Delay(50, ct);
            }
            finally
            {
                if (isMap)
                {
                    lock (lockObj) inFlight--;
                }
            }
            return new TranslationResult
            {
                OriginalText = req.Text,
                TranslatedText = """{"summary":"x","glossary":{},"preservation_hints":[]}""",
                ServiceName = "fake",
                TargetLanguage = req.ToLanguage,
            };
        });

        var ir = BuildIr(Enumerable.Range(1, 8).Select(p => (p, $"page {p}")).ToArray());

        await sut.ExtractAsync(ir, DefaultOptions with { MaxConcurrency = 2 });

        peakObserved.Should().BeGreaterThan(1, "extractor should run map calls in parallel");
        peakObserved.Should().BeLessThanOrEqualTo(2, "should not exceed MaxConcurrency = 2");
    }

    [Fact]
    public async Task ExtractAsync_ReducesPartialSummariesIntoOne()
    {
        var seenReduceText = string.Empty;
        var counter = 0;
        var sut = BuildExtractor(req =>
        {
            if (req.CustomPrompt == DocumentContextExtractor.ReduceSummaryPrompt)
            {
                seenReduceText = req.Text;
                return "Final merged summary about three pages.";
            }
            counter++;
            return $$"""{"summary":"Summary for page {{counter}}","glossary":{},"preservation_hints":[]}""";
        });

        var ir = BuildIr([(1, "p1"), (2, "p2"), (3, "p3")]);

        var ctx = await sut.ExtractAsync(ir, DefaultOptions);

        ctx.Summary.Should().Be("Final merged summary about three pages.");
        seenReduceText.Should().Contain("1.");
        seenReduceText.Should().Contain("2.");
        seenReduceText.Should().Contain("3.");
    }

    [Fact]
    public async Task ExtractAsync_MergesGlossariesAcrossPages_PrefersMostFrequent()
    {
        var pageCounter = 0;
        var sut = BuildExtractor(req =>
        {
            if (req.CustomPrompt == DocumentContextExtractor.ReduceSummaryPrompt)
                return "merged";

            pageCounter++;
            // Page 1 + page 2 say Transformer→Transformer; page 3 says Transformer→变压器.
            // Majority should win.
            return pageCounter switch
            {
                1 => """{"summary":"s1","glossary":{"Transformer":"Transformer","BERT":"BERT"},"preservation_hints":[]}""",
                2 => """{"summary":"s2","glossary":{"Transformer":"Transformer"},"preservation_hints":[]}""",
                3 => """{"summary":"s3","glossary":{"Transformer":"变压器"},"preservation_hints":[]}""",
                _ => """{"summary":"","glossary":{},"preservation_hints":[]}"""
            };
        });

        var ir = BuildIr([(1, "p1"), (2, "p2"), (3, "p3")]);

        var ctx = await sut.ExtractAsync(ir, DefaultOptions);

        ctx.Glossary["Transformer"].Should().Be("Transformer", "two pages voted Transformer vs one for 变压器");
        ctx.Glossary.Should().ContainKey("BERT");
    }

    [Fact]
    public async Task ExtractAsync_ContinuesWhenSinglePageFails()
    {
        var pageCounter = 0;
        var sut = new DocumentContextExtractor((req, _, _) =>
        {
            if (req.CustomPrompt == DocumentContextExtractor.ReduceSummaryPrompt)
            {
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = req.Text,
                    TranslatedText = "merged-after-failure",
                    ServiceName = "fake",
                    TargetLanguage = req.ToLanguage,
                });
            }

            var n = Interlocked.Increment(ref pageCounter);
            if (n == 2)
            {
                throw new InvalidOperationException("Simulated page failure");
            }
            return Task.FromResult(new TranslationResult
            {
                OriginalText = req.Text,
                TranslatedText = $$"""{"summary":"page {{n}}","glossary":{"k{{n}}":"v{{n}}"},"preservation_hints":["hint-{{n}}"]}""",
                ServiceName = "fake",
                TargetLanguage = req.ToLanguage,
            });
        });

        var ir = BuildIr([(1, "p1"), (2, "p2"), (3, "p3")]);

        var ctx = await sut.ExtractAsync(ir, DefaultOptions with { MaxConcurrency = 1 });

        // Page 2 failed, so its glossary/hints contribution is missing, but the others survive.
        ctx.Glossary.Should().HaveCount(2);
        ctx.PreservationHints.Should().HaveCount(2);
        ctx.PreservationHints.Should().NotContain("hint-2");
        ctx.Summary.Should().Be("merged-after-failure");
    }

    [Fact]
    public void TryParsePagePartial_AcceptsLooseJsonWithSurroundingProse()
    {
        var raw = "Sure, here is the analysis:\n\n{\"summary\":\"hi\",\"glossary\":{},\"preservation_hints\":[\"alpha\",\"beta\"]}\n\nThanks!";

        var p = DocumentContextExtractor.TryParsePagePartial(raw, 1);

        p.Should().NotBeNull();
        p!.Summary.Should().Be("hi");
        p.Hints.Should().Contain("alpha");
        p.Hints.Should().Contain("beta");
    }

    [Fact]
    public void TryParsePagePartial_RejectsHintsShorterThanThreeChars()
    {
        var raw = """{"summary":"","glossary":{},"preservation_hints":["a","ab","abc","abcd"]}""";

        var p = DocumentContextExtractor.TryParsePagePartial(raw, 1);

        p.Should().NotBeNull();
        p!.Hints.Should().NotContain("a");
        p.Hints.Should().NotContain("ab");
        p.Hints.Should().Contain("abc");
        p.Hints.Should().Contain("abcd");
    }

    // ---- Test helpers ----

    private static DocumentContextExtractor BuildExtractor(Func<TranslationRequest, string> respond)
    {
        return new DocumentContextExtractor((req, _, _) => Task.FromResult(new TranslationResult
        {
            OriginalText = req.Text,
            TranslatedText = respond(req),
            ServiceName = "fake",
            TargetLanguage = req.ToLanguage,
        }));
    }

    private static IReadOnlyList<DocumentBlockIr> BuildIr(params (int PageNumber, string Text)[] blocks)
    {
        return blocks
            .Select((b, i) => new DocumentBlockIr
            {
                IrBlockId = $"ir-{i}",
                PageNumber = b.PageNumber,
                SourceBlockId = $"src-{i}",
                BlockType = BlockType.Paragraph,
                OriginalText = b.Text,
                ProtectedText = b.Text,
                SourceHash = $"hash-{i}",
            })
            .ToList();
    }
}
