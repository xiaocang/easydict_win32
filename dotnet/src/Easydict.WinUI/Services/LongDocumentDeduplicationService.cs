using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

public sealed class LongDocumentDeduplicationService : IDisposable
{
    private readonly string _indexFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public LongDocumentDeduplicationService()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict");
        Directory.CreateDirectory(baseDir);
        _indexFilePath = Path.Combine(baseDir, "longdoc_dedup_index.json");
    }

    public async Task<string> CreateDedupKeyAsync(
        LongDocumentInputMode mode,
        string input,
        string serviceId,
        Language from,
        Language to,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var inputHash = await ComputeInputHashAsync(mode, input, cancellationToken);
        var canonical = $"v1|{mode}|{serviceId}|{from.ToCode()}|{to.ToCode()}|{inputHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public async Task<string?> TryGetExistingOutputPathAsync(string dedupKey, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var index = await ReadIndexCoreAsync(cancellationToken);
            if (!index.TryGetValue(dedupKey, out var entry))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(entry.OutputPath) || !File.Exists(entry.OutputPath))
            {
                index.Remove(dedupKey);
                await WriteIndexCoreAsync(index, cancellationToken);
                return null;
            }

            entry.LastUsedUtc = DateTime.UtcNow;
            await WriteIndexCoreAsync(index, cancellationToken);
            return entry.OutputPath;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RegisterOutputAsync(string dedupKey, string outputPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var index = await ReadIndexCoreAsync(cancellationToken);
            index[dedupKey] = new DedupEntry
            {
                OutputPath = outputPath,
                CreatedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow
            };
            await WriteIndexCoreAsync(index, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<string> ComputeInputHashAsync(
        LongDocumentInputMode mode,
        string input,
        CancellationToken cancellationToken)
    {
        if (mode == LongDocumentInputMode.Pdf)
        {
            if (!File.Exists(input))
            {
                throw new FileNotFoundException("PDF file not found.", input);
            }

            await using var stream = File.OpenRead(input);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }

        var bytes = Encoding.UTF8.GetBytes(input.Trim());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private async Task<Dictionary<string, DedupEntry>> ReadIndexCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexFilePath))
        {
            return new Dictionary<string, DedupEntry>(StringComparer.OrdinalIgnoreCase);
        }

        await using var fs = File.OpenRead(_indexFilePath);
        var index = await JsonSerializer.DeserializeAsync<Dictionary<string, DedupEntry>>(fs, cancellationToken: cancellationToken);
        return index ?? new Dictionary<string, DedupEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteIndexCoreAsync(Dictionary<string, DedupEntry> index, CancellationToken cancellationToken)
    {
        await using var fs = File.Create(_indexFilePath);
        await JsonSerializer.SerializeAsync(fs, index, cancellationToken: cancellationToken);
    }


    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private sealed class DedupEntry
    {
        public string OutputPath { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }
}
