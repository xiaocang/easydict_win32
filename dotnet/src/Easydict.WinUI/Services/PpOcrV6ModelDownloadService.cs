using System.Collections.Concurrent;
using System.Security.Cryptography;
using Easydict.SidecarClient.Protocol;

namespace Easydict.WinUI.Services;

public sealed record PpOcrV6DownloadProgress(
    string FileName,
    long BytesDownloaded,
    long TotalBytes,
    double Percentage);

public sealed class PpOcrV6ModelDownloadService : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ModelDownloadClient _client;
    private readonly PpOcrV6ModelStore _store;
    private bool _disposed;

    public PpOcrV6ModelDownloadService(
        HttpClient? httpClient = null,
        PpOcrV6ModelStore? store = null)
    {
        _client = new ModelDownloadClient(httpClient);
        _store = store ?? new PpOcrV6ModelStore();
    }

    public PpOcrV6ModelStore Store => _store;

    public async Task<PpOcrV6ModelState> DownloadAsync(
        string modelId,
        IProgress<PpOcrV6DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = PpOcrV6ModelCatalog.Get(modelId);
        var lockKey = Path.Combine(_store.RootDirectory, model.Id);
        var downloadLock = DownloadLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stagingDirectory = Path.Combine(
            _store.RootDirectory,
            $".{model.Id}.{Guid.NewGuid():N}.staging");
        string? publishedDirectory = null;

        try
        {
            if (await _store.ValidateAsync(model.Id, cancellationToken).ConfigureAwait(false)
                == PpOcrV6ModelState.Installed)
            {
                return PpOcrV6ModelState.Installed;
            }

            Directory.CreateDirectory(stagingDirectory);
            long completedBytes = 0;
            foreach (var artifact in model.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(stagingDirectory, artifact.FileName);
                var stage = $"PP-OCRv6 {model.Id} {artifact.FileName}";
                var artifactProgress = new Progress<ModelDownloadProgress>(value =>
                {
                    progress?.Report(new PpOcrV6DownloadProgress(
                        artifact.FileName,
                        completedBytes + Math.Max(0, value.BytesDownloaded),
                        model.DownloadSizeBytes,
                        model.DownloadSizeBytes > 0
                            ? (completedBytes + Math.Max(0, value.BytesDownloaded)) * 100.0 / model.DownloadSizeBytes
                            : -1));
                });

                await _client.DownloadWithRetryAsync(
                    [artifact.Url],
                    targetPath,
                    stage,
                    artifactProgress,
                    cancellationToken).ConfigureAwait(false);
                await ValidateArtifactAsync(artifact, targetPath, cancellationToken)
                    .ConfigureAwait(false);
                completedBytes += artifact.SizeBytes;
            }

            var finalPaths = _store.GetPaths(model.Id);
            publishedDirectory = $"{finalPaths.Directory}.{Guid.NewGuid():N}.published";
            Directory.CreateDirectory(publishedDirectory);
            foreach (var artifact in model.Artifacts)
            {
                File.Move(
                    Path.Combine(stagingDirectory, artifact.FileName),
                    Path.Combine(publishedDirectory, artifact.FileName),
                    overwrite: true);
            }

            await File.WriteAllTextAsync(
                Path.Combine(publishedDirectory, PpOcrV6ModelStore.CompletionSentinelName),
                $"{model.Id}{Environment.NewLine}{DateTimeOffset.UtcNow:O}",
                cancellationToken).ConfigureAwait(false);

            var backupDirectory = $"{finalPaths.Directory}.{Guid.NewGuid():N}.backup";
            var oldDirectoryMoved = false;
            try
            {
                if (Directory.Exists(finalPaths.Directory))
                {
                    Directory.Move(finalPaths.Directory, backupDirectory);
                    oldDirectoryMoved = true;
                }

                Directory.Move(publishedDirectory, finalPaths.Directory);
                if (oldDirectoryMoved)
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
            catch
            {
                TryDeleteDirectory(finalPaths.Directory);
                if (oldDirectoryMoved && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, finalPaths.Directory);
                }
                throw;
            }

            return await _store.ValidateAsync(model.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            if (publishedDirectory is not null)
            {
                TryDeleteDirectory(publishedDirectory);
            }
            downloadLock.Release();
        }
    }

    private static async Task ValidateArtifactAsync(
        PpOcrV6Artifact artifact,
        string path,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length != artifact.SizeBytes)
        {
            throw new InvalidDataException(
                $"Unexpected size for {artifact.FileName}: expected {artifact.SizeBytes}, got {fileInfo.Length}.");
        }

        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var actualHash = Convert.ToHexString(
            await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch for {artifact.FileName}: expected {artifact.Sha256}, got {actualHash}.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }
}
