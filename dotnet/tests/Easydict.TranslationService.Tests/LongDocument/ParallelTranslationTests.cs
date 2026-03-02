using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

public class ParallelTranslationTests
{
    [Fact]
    public async Task TranslateAsync_WithConcurrency4_TranslatesAllBlocks()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
        var source = BuildSourceWithNBlocks(20);

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4
        });

        result.Pages.Should().HaveCount(1);
        var translatedBlocks = result.Pages[0].Blocks;
        translatedBlocks.Should().HaveCount(20);
        translatedBlocks.Should().AllSatisfy(b =>
        {
            b.TranslatedText.Should().StartWith("T:");
            b.TranslationSkipped.Should().BeFalse();
            b.LastError.Should().BeNull();
        });
    }

    [Fact]
    public async Task TranslateAsync_WithConcurrency1_SequentialBehavior()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
        var source = BuildSourceWithNBlocks(5);

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 1
        });

        result.Pages[0].Blocks.Should().HaveCount(5);
        result.Pages[0].Blocks.Should().AllSatisfy(b =>
            b.TranslatedText.Should().StartWith("T:"));
    }

    [Fact]
    public async Task TranslateAsync_RespectsMaxConcurrency()
    {
        var currentConcurrency = 0;
        var maxObserved = 0;
        var lockObj = new object();

        var sut = new LongDocumentTranslationService(translateWithService: async (request, _, ct) =>
        {
            lock (lockObj) { currentConcurrency++; maxObserved = Math.Max(maxObserved, currentConcurrency); }
            await Task.Delay(50, ct); // Simulate API latency
            lock (lockObj) { currentConcurrency--; }
            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            };
        });

        var source = BuildSourceWithNBlocks(20);

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4
        });

        maxObserved.Should().BeGreaterThan(1, "parallel execution should have >1 concurrent request");
        maxObserved.Should().BeLessThanOrEqualTo(4, "should not exceed MaxConcurrency=4");
    }

    [Fact]
    public async Task TranslateAsync_CancellationRespected()
    {
        using var cts = new CancellationTokenSource();
        var callCount = 0;

        var sut = new LongDocumentTranslationService(translateWithService: async (request, _, ct) =>
        {
            Interlocked.Increment(ref callCount);
            if (callCount >= 3)
            {
                await cts.CancelAsync();
            }
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            };
        });

        var source = BuildSourceWithNBlocks(50);

        var act = async () => await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TranslateAsync_MaxConcurrencyLessThan1_Throws()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
        var source = BuildSourceWithNBlocks(1);

        var act = async () => await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 0
        });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("MaxConcurrency");
    }

    [Fact]
    public async Task TranslateAsync_SkippedBlocks_NotAffectedByConcurrency()
    {
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
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
                        new SourceDocumentBlock { BlockId = "b1", BlockType = SourceBlockType.Paragraph, Text = "Hello" },
                        new SourceDocumentBlock { BlockId = "b2", BlockType = SourceBlockType.Formula, Text = "E=mc^2" },
                        new SourceDocumentBlock { BlockId = "b3", BlockType = SourceBlockType.Paragraph, Text = "World" },
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4
        });

        var blocks = result.Pages[0].Blocks;
        blocks[0].TranslationSkipped.Should().BeFalse();
        blocks[0].TranslatedText.Should().StartWith("T:");
        blocks[1].TranslationSkipped.Should().BeTrue();
        blocks[1].TranslatedText.Should().Be("E=mc^2"); // Preserved
        blocks[2].TranslationSkipped.Should().BeFalse();
        blocks[2].TranslatedText.Should().StartWith("T:");
    }

    [Fact]
    public async Task TranslateAsync_ParallelWithRetries_HandlesErrorsPerBlock()
    {
        var callCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        var sut = new LongDocumentTranslationService(translateWithService: (request, _, ct) =>
        {
            var count = callCounts.AddOrUpdate(request.Text, 1, (_, c) => c + 1);
            if (request.Text == "FailFirst" && count == 1)
            {
                throw new InvalidOperationException("Simulated failure");
            }
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
            DocumentId = "doc-retry",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock { BlockId = "b1", BlockType = SourceBlockType.Paragraph, Text = "OK" },
                        new SourceDocumentBlock { BlockId = "b2", BlockType = SourceBlockType.Paragraph, Text = "FailFirst" },
                        new SourceDocumentBlock { BlockId = "b3", BlockType = SourceBlockType.Paragraph, Text = "Also OK" },
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4,
            MaxRetriesPerBlock = 1
        });

        var blocks = result.Pages[0].Blocks;
        blocks[0].TranslatedText.Should().Be("T:OK");
        blocks[1].TranslatedText.Should().Be("T:FailFirst"); // Succeeded on retry
        blocks[2].TranslatedText.Should().Be("T:Also OK");
    }

    private static Task<TranslationResult> FakeTranslate(TranslationRequest request, string serviceId, CancellationToken ct)
    {
        return Task.FromResult(new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = $"T:{request.Text}",
            ServiceName = "fake",
            TargetLanguage = request.ToLanguage
        });
    }

    private static SourceDocument BuildSourceWithNBlocks(int n)
    {
        var blocks = Enumerable.Range(0, n)
            .Select(i => new SourceDocumentBlock
            {
                BlockId = $"b{i}",
                BlockType = SourceBlockType.Paragraph,
                Text = $"Block {i} content",
                BoundingBox = new BlockRect(0, i * 50, 400, 40)
            })
            .ToList();

        return new SourceDocument
        {
            DocumentId = "doc-parallel",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks = blocks
                }
            ]
        };
    }
}
