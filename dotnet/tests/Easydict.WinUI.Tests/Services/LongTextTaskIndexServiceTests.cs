using System.Security.Cryptography;
using System.Text;
using Easydict.WinUI.Services;
using FluentAssertions;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class LongTextTaskIndexServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public LongTextTaskIndexServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "easydict-longtext-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task ComputeFileHashAsync_UsesStreamingHashAndReturnsSha256()
    {
        var filePath = Path.Combine(_tempRoot, "large-input.txt");
        var payload = string.Concat(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz0123456789", 4096));
        await File.WriteAllTextAsync(filePath, payload, Encoding.UTF8);

        var computed = await LongTextTaskIndexService.ComputeFileHashAsync(filePath);

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        computed.Should().Be(expected);
    }

    [Fact]
    public void BuildDedupKey_ContainsAllRequiredParts()
    {
        var key = LongTextTaskIndexService.BuildDedupKey("hash123", "google", "en", "zh", "v3");

        key.Should().Be("hash123|google|en|zh|v3");
    }

    [Fact]
    public async Task GetEnqueueDecision_WhenCompletedEntryExists_ReturnsReuseMessage()
    {
        var indexPath = Path.Combine(_tempRoot, "index.json");
        var service = new LongTextTaskIndexService(indexPath);
        var dedupKey = LongTextTaskIndexService.BuildDedupKey("hash-a", "google", "en", "zh", "v1");

        await service.UpsertAsync(new LongTextTaskEntry
        {
            DedupKey = dedupKey,
            FileHash = "hash-a",
            ServiceId = "google",
            FromLang = "en",
            ToLang = "zh",
            PipelineVersion = "v1",
            OutputPath = "out/result.txt",
            Status = LongTextTaskStatus.Completed
        });

        var decision = service.GetEnqueueDecision(dedupKey);

        decision.Action.Should().Be(LongTextEnqueueAction.ReuseCompletedOutput);
        decision.Prompt.Should().Contain("复用历史输出");
    }

    [Fact]
    public async Task GetEnqueueDecision_WhenPartialWithCheckpointExists_ReturnsResumeAction()
    {
        var indexPath = Path.Combine(_tempRoot, "index-partial.json");
        var service = new LongTextTaskIndexService(indexPath);
        var dedupKey = LongTextTaskIndexService.BuildDedupKey("hash-b", "deepseek", "ja", "zh", "v2");

        await service.UpsertAsync(new LongTextTaskEntry
        {
            DedupKey = dedupKey,
            FileHash = "hash-b",
            ServiceId = "deepseek",
            FromLang = "ja",
            ToLang = "zh",
            PipelineVersion = "v2",
            OutputPath = "out/partial.txt",
            CheckpointPath = "checkpoints/partial.ckpt.json",
            Status = LongTextTaskStatus.Partial
        });

        var decision = service.GetEnqueueDecision(dedupKey);

        decision.Action.Should().Be(LongTextEnqueueAction.ResumeFromCheckpoint);
        decision.Prompt.Should().Contain("checkpoint");
        decision.ExistingEntry.Should().NotBeNull();
        decision.ExistingEntry!.CheckpointPath.Should().Be("checkpoints/partial.ckpt.json");
    }

    [Fact]
    public async Task UpsertAsync_PersistsEntriesToLocalJsonIndex()
    {
        var indexPath = Path.Combine(_tempRoot, "persist-index.json");
        var dedupKey = LongTextTaskIndexService.BuildDedupKey("hash-c", "ollama", "auto", "en", "v5");

        var first = new LongTextTaskIndexService(indexPath);
        await first.UpsertAsync(new LongTextTaskEntry
        {
            DedupKey = dedupKey,
            FileHash = "hash-c",
            ServiceId = "ollama",
            FromLang = "auto",
            ToLang = "en",
            PipelineVersion = "v5",
            OutputPath = "out/final.md",
            Status = LongTextTaskStatus.Completed
        });

        var reloaded = new LongTextTaskIndexService(indexPath);
        var found = reloaded.TryGetEntry(dedupKey, out var entry);

        found.Should().BeTrue();
        entry.Should().NotBeNull();
        entry!.OutputPath.Should().Be("out/final.md");
        entry.Status.Should().Be(LongTextTaskStatus.Completed);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
