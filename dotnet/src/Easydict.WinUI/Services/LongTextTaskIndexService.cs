using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Easydict.WinUI.Services;

public enum LongTextTaskStatus
{
    InProgress,
    Partial,
    Completed,
    Failed
}

public enum LongTextEnqueueAction
{
    EnqueueNew,
    ReuseCompletedOutput,
    ResumeFromCheckpoint,
    ForceRetranslate
}

public sealed record LongTextTaskEntry
{
    public required string DedupKey { get; init; }
    public required string FileHash { get; init; }
    public required string ServiceId { get; init; }
    public required string FromLang { get; init; }
    public required string ToLang { get; init; }
    public required string PipelineVersion { get; init; }
    public required string OutputPath { get; init; }
    public string? CheckpointPath { get; init; }
    public LongTextTaskStatus Status { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LongTextEnqueueDecision(LongTextEnqueueAction Action, string Prompt, LongTextTaskEntry? ExistingEntry);

public sealed class LongTextTaskIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, LongTextTaskEntry> _entries;

    public LongTextTaskIndexService(string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath))
        {
            throw new ArgumentException("Index path cannot be null or empty.", nameof(indexPath));
        }

        _indexPath = indexPath;
        _entries = new ConcurrentDictionary<string, LongTextTaskEntry>(LoadEntries(indexPath));
    }

    public static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        const int bufferSize = 1024 * 1024;
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            useAsync: true);

        var buffer = new byte[bufferSize];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    public static string BuildDedupKey(string fileHash, string serviceId, string fromLang, string toLang, string pipelineVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromLang);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLang);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineVersion);

        return string.Join('|', fileHash, serviceId, fromLang, toLang, pipelineVersion);
    }

    public LongTextEnqueueDecision GetEnqueueDecision(string dedupKey, bool forceRetranslate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

        if (forceRetranslate)
        {
            return new LongTextEnqueueDecision(
                LongTextEnqueueAction.ForceRetranslate,
                "强制重新翻译：将忽略历史记录并重新入队。",
                _entries.TryGetValue(dedupKey, out var forceEntry) ? forceEntry : null);
        }

        if (!_entries.TryGetValue(dedupKey, out var existingEntry))
        {
            return new LongTextEnqueueDecision(LongTextEnqueueAction.EnqueueNew, "未命中历史记录，创建新翻译任务。", null);
        }

        if (existingEntry.Status == LongTextTaskStatus.Completed)
        {
            return new LongTextEnqueueDecision(
                LongTextEnqueueAction.ReuseCompletedOutput,
                "命中已完成任务：可复用历史输出，或选择强制重新翻译。",
                existingEntry);
        }

        if (existingEntry.Status == LongTextTaskStatus.Partial && !string.IsNullOrWhiteSpace(existingEntry.CheckpointPath))
        {
            return new LongTextEnqueueDecision(
                LongTextEnqueueAction.ResumeFromCheckpoint,
                "命中部分成功任务：可直接加载 checkpoint 并继续重试。",
                existingEntry);
        }

        return new LongTextEnqueueDecision(LongTextEnqueueAction.EnqueueNew, "历史记录不可复用，创建新翻译任务。", existingEntry);
    }

    public bool TryGetEntry(string dedupKey, out LongTextTaskEntry? entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);
        return _entries.TryGetValue(dedupKey, out entry);
    }

    public async Task UpsertAsync(LongTextTaskEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries[entry.DedupKey] = entry with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        await SaveAsync(cancellationToken);
    }

    private IReadOnlyDictionary<string, LongTextTaskEntry> LoadEntries(string indexPath)
    {
        if (!File.Exists(indexPath))
        {
            return new Dictionary<string, LongTextTaskEntry>();
        }

        var json = File.ReadAllText(indexPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, LongTextTaskEntry>();
        }

        var loaded = JsonSerializer.Deserialize<Dictionary<string, LongTextTaskEntry>>(json, JsonOptions);
        return loaded ?? new Dictionary<string, LongTextTaskEntry>();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, LongTextTaskEntry> snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        }

        var directory = Path.GetDirectoryName(_indexPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(_indexPath, json, Encoding.UTF8, cancellationToken);
    }
}
