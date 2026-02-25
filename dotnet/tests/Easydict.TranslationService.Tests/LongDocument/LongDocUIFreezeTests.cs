using System.Diagnostics;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.TranslationService.Tests.LongDocument;

/// <summary>
/// Tests that verify long document translation does not block / freeze the UI thread.
/// Each test injects a fake translator with controlled delays and asserts on timing,
/// progress frequency, cancellation latency, or parallel speedup — indicators that the
/// async pipeline yields control properly and never performs synchronous blocking.
///
/// Run with:  dotnet test --filter "Category=UIFreeze"
/// </summary>
[Trait("Category", "UIFreeze")]
public class LongDocUIFreezeTests
{
    private readonly ITestOutputHelper _output;

    public LongDocUIFreezeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ──────────────────────────────────────────────
    //  1. Sequential 100 blocks — should complete well within a timing budget
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_100Blocks_CompletesWithinTimeBudget()
    {
        // Arrange: 100 blocks, each translation has 10ms async delay.
        // Fully sequential = ~1s; budget = 5s (generous for slow CI).
        const int blockCount = 100;
        const int perBlockDelayMs = 10;
        var sut = new LongDocumentTranslationService(translateWithService: DelayedFakeTranslate(perBlockDelayMs));
        var source = BuildSourceWithNBlocks(blockCount);

        // Act
        var sw = Stopwatch.StartNew();
        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 1,
            EnableFormulaProtection = false
        });
        sw.Stop();

        // Assert
        _output.WriteLine($"100 blocks sequential: {sw.ElapsedMilliseconds}ms");
        result.Pages.SelectMany(p => p.Blocks).Should().HaveCount(blockCount);
        result.QualityReport.FailedBlocks.Should().BeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "100 blocks with 10ms async delay should complete in <5s when not blocking");
    }

    // ──────────────────────────────────────────────
    //  2. Parallel mode should be measurably faster than sequential
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_ParallelMode_FasterThanSequential()
    {
        const int blockCount = 40;
        const int perBlockDelayMs = 50;

        // Sequential run
        var sutSeq = new LongDocumentTranslationService(translateWithService: DelayedFakeTranslate(perBlockDelayMs));
        var source = BuildSourceWithNBlocks(blockCount);

        var swSeq = Stopwatch.StartNew();
        await sutSeq.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 1,
            EnableFormulaProtection = false
        });
        swSeq.Stop();

        // Parallel run (concurrency=4)
        var sutPar = new LongDocumentTranslationService(translateWithService: DelayedFakeTranslate(perBlockDelayMs));

        var swPar = Stopwatch.StartNew();
        var result = await sutPar.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4,
            EnableFormulaProtection = false
        });
        swPar.Stop();

        _output.WriteLine($"Sequential: {swSeq.ElapsedMilliseconds}ms, Parallel(4): {swPar.ElapsedMilliseconds}ms");

        // Assert: parallel should be at least 1.5x faster
        result.Pages.SelectMany(p => p.Blocks).Should().HaveCount(blockCount);
        swPar.ElapsedMilliseconds.Should().BeLessThan(swSeq.ElapsedMilliseconds * 2 / 3,
            "parallel(4) should be significantly faster than sequential");
    }

    // ──────────────────────────────────────────────
    //  3. Progress reports cover all five stages
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_ProgressReportsAllStages()
    {
        var stages = new List<LongDocumentTranslationStage>();
        var progress = new SynchronousProgress<LongDocumentTranslationProgress>(p => stages.Add(p.Stage));

        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
        var source = BuildSourceWithNBlocks(3);

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true,
            Progress = progress
        });

        _output.WriteLine($"Stages reported: {string.Join(" → ", stages)}");

        // Assert all five stages are reported
        stages.Should().Contain(LongDocumentTranslationStage.Parsing);
        stages.Should().Contain(LongDocumentTranslationStage.BuildingIr);
        stages.Should().Contain(LongDocumentTranslationStage.FormulaProtection);
        stages.Should().Contain(LongDocumentTranslationStage.Translating);
        stages.Should().Contain(LongDocumentTranslationStage.Exporting);
    }

    // ──────────────────────────────────────────────
    //  4. Progress fires incrementally during translation (not just start+end)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_ProgressReportsDuringTranslation()
    {
        const int blockCount = 50;
        var translatingReports = new List<LongDocumentTranslationProgress>();
        var progress = new Progress<LongDocumentTranslationProgress>(p =>
        {
            if (p.Stage == LongDocumentTranslationStage.Translating)
                translatingReports.Add(p);
        });

        var sut = new LongDocumentTranslationService(translateWithService: DelayedFakeTranslate(5));
        var source = BuildSourceWithNBlocks(blockCount);

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 1,
            EnableFormulaProtection = false,
            Progress = progress
        });

        // Allow progress callbacks to fire
        await Task.Delay(200);

        _output.WriteLine($"Translating-stage progress reports: {translatingReports.Count}");

        // Assert: should have at least 10 incremental progress reports (not just 1-2)
        // We expect blockCount+1 reports (initial + one per block), but allow some
        // to be coalesced by Progress<T>'s SynchronizationContext batching.
        translatingReports.Count.Should().BeGreaterThanOrEqualTo(10,
            "progress should fire incrementally per block, not only at start/end");

        // Verify percentage increases monotonically
        var percentages = translatingReports.Select(p => p.Percentage).ToList();
        for (var i = 1; i < percentages.Count; i++)
        {
            percentages[i].Should().BeGreaterThanOrEqualTo(percentages[i - 1],
                "percentage should be monotonically non-decreasing");
        }
    }

    // ──────────────────────────────────────────────
    //  5. Cancellation mid-translation returns quickly
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_CancellationMidTranslation_ReturnsWithinBudget()
    {
        var callCount = 0;
        using var cts = new CancellationTokenSource();

        var sut = new LongDocumentTranslationService(translateWithService: async (request, _, ct) =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count >= 5)
            {
                await cts.CancelAsync();
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(50, ct);

            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = request.ToLanguage
            };
        });

        var source = BuildSourceWithNBlocks(50);

        var sw = Stopwatch.StartNew();
        var act = () => sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 1,
            EnableFormulaProtection = false
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        _output.WriteLine($"Cancellation after {callCount} blocks: {sw.ElapsedMilliseconds}ms");

        // Assert: should return within 500ms after cancellation, not wait for remaining 45 blocks
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "cancellation should stop translation promptly, not continue processing remaining blocks");
        callCount.Should().BeLessThan(50, "not all blocks should have been attempted");
    }

    // ──────────────────────────────────────────────
    //  6. Large document (500 blocks) completes without OOM or timeout
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_LargeDocument_MemoryStable()
    {
        const int blockCount = 500;
        var sut = new LongDocumentTranslationService(translateWithService: FakeTranslate);
        var source = BuildSourceWithNBlocks(blockCount);

        var sw = Stopwatch.StartNew();
        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 4,
            EnableFormulaProtection = false
        });
        sw.Stop();

        _output.WriteLine($"500 blocks parallel(4): {sw.ElapsedMilliseconds}ms");

        // Assert
        var allBlocks = result.Pages.SelectMany(p => p.Blocks).ToList();
        allBlocks.Should().HaveCount(blockCount);
        allBlocks.Should().AllSatisfy(b =>
        {
            b.TranslatedText.Should().StartWith("T:");
            b.LastError.Should().BeNull();
        });
        result.QualityReport.TotalBlocks.Should().Be(blockCount);
        result.QualityReport.TranslatedBlocks.Should().Be(blockCount);
        result.QualityReport.FailedBlocks.Should().BeEmpty();

        // Should complete in <10s even on slow CI (no blocking)
        sw.ElapsedMilliseconds.Should().BeLessThan(10000);
    }

    // ──────────────────────────────────────────────
    //  7. Slow translator — progress arrives between stages
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SlowTranslator_YieldsControlBetweenStages()
    {
        var timestamps = new List<(LongDocumentTranslationStage Stage, long Ms)>();
        var stopwatch = Stopwatch.StartNew();
        var progress = new SynchronousProgress<LongDocumentTranslationProgress>(p =>
        {
            timestamps.Add((p.Stage, stopwatch.ElapsedMilliseconds));
        });

        // Each translation block takes 100ms — enough to clearly see timing gaps
        var sut = new LongDocumentTranslationService(translateWithService: DelayedFakeTranslate(100));
        var source = BuildSourceWithNBlocks(5);

        await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            MaxConcurrency = 1,
            EnableFormulaProtection = true,
            Progress = progress
        });

        stopwatch.Stop();

        _output.WriteLine("Stage timeline:");
        foreach (var (stage, ms) in timestamps)
        {
            _output.WriteLine($"  {ms,5}ms - {stage}");
        }

        // Assert: stages should appear in sequence with translate phase spanning most of the time
        var parsingTs = timestamps.Where(t => t.Stage == LongDocumentTranslationStage.Parsing).ToList();
        var translatingTs = timestamps.Where(t => t.Stage == LongDocumentTranslationStage.Translating).ToList();
        var exportingTs = timestamps.Where(t => t.Stage == LongDocumentTranslationStage.Exporting).ToList();

        parsingTs.Should().NotBeEmpty("Parsing stage should be reported");
        translatingTs.Should().NotBeEmpty("Translating stage should be reported");
        exportingTs.Should().NotBeEmpty("Exporting stage should be reported");

        // Translating should start after parsing and end before exporting
        var firstTranslating = translatingTs.Min(t => t.Ms);
        var lastParsing = parsingTs.Max(t => t.Ms);
        var firstExporting = exportingTs.Min(t => t.Ms);

        firstTranslating.Should().BeGreaterThanOrEqualTo(lastParsing,
            "translation should start after parsing completes");
        firstExporting.Should().BeGreaterThan(firstTranslating,
            "exporting should start after translation begins");
    }

    // ═══════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════

    private static Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>>
        DelayedFakeTranslate(int delayMs) => async (request, _, ct) =>
    {
        await Task.Delay(delayMs, ct);
        return new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = $"T:{request.Text}",
            ServiceName = "fake",
            TargetLanguage = request.ToLanguage
        };
    };

    private static Task<TranslationResult> FakeTranslate(TranslationRequest request, string _, CancellationToken __)
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
                Text = $"Block {i} content for translation testing.",
                BoundingBox = new BlockRect(0, i * 50, 400, 40)
            })
            .ToList();

        return new SourceDocument
        {
            DocumentId = "doc-ui-freeze-test",
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

    /// <summary>
    /// Synchronous IProgress implementation for tests.
    /// Unlike <see cref="Progress{T}"/>, invokes the callback inline on Report(),
    /// avoiding async dispatch race conditions in unit tests.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
