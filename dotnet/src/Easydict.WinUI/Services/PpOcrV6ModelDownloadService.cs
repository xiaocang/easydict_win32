using System.Collections.Concurrent;
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
        var stagingDirectory = Path.Combine(
            _store.RootDirectory,
            $".{model.Id}.{Guid.NewGuid():N}.staging");
        string? publishedDirectory = null;
        await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_store.GetStateBySize(model.Id) == PpOcrV6ModelState.Installed)
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
                var downloadedSize = new FileInfo(targetPath).Length;
                if (downloadedSize != artifact.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"Unexpected size for {artifact.FileName}: expected {artifact.SizeBytes}, got {downloadedSize}.");
                }
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
            var publishedDirectoryMoved = false;
            try
            {
                if (Directory.Exists(finalPaths.Directory))
                {
                    Directory.Move(finalPaths.Directory, backupDirectory);
                    oldDirectoryMoved = true;
                }

                Directory.Move(publishedDirectory, finalPaths.Directory);
                publishedDirectoryMoved = true;
                if (oldDirectoryMoved)
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
            catch
            {
                if (publishedDirectoryMoved)
                {
                    TryDeleteDirectory(finalPaths.Directory);
                }

                if (oldDirectoryMoved && Directory.Exists(backupDirectory))
                {
                    if (Directory.Exists(finalPaths.Directory))
                    {
                        TryDeleteDirectory(finalPaths.Directory);
                    }

                    Directory.Move(backupDirectory, finalPaths.Directory);
                }

                throw;
            }

            return _store.GetStateBySize(model.Id);
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

    public async Task RemoveAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = PpOcrV6ModelCatalog.Get(modelId);
        var lockKey = Path.Combine(_store.RootDirectory, model.Id);
        var downloadLock = DownloadLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = _store.GetPaths(model.Id);
            if (Directory.Exists(paths.Directory))
            {
                Directory.Delete(paths.Directory, recursive: true);
            }
        }
        finally
        {
            downloadLock.Release();
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
