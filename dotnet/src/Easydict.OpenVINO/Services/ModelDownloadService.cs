using System.Diagnostics;
using System.Security.Cryptography;
using Easydict.OpenVINO.Models;

namespace Easydict.OpenVINO.Services;

/// <summary>
/// Resolves the per-user cache directory for the NLLB-200 ONNX bundle and
/// downloads missing files from HuggingFace on first use.
///
/// Layout (Windows):
///   %LOCALAPPDATA%\Easydict\models\nllb-200-distilled-600M\
///       encoder_model_quantized.onnx
///       decoder_model_merged_quantized.onnx
///       sentencepiece.bpe.model
///       tokenizer.json
///       config.json
///       .complete           &lt;-- written only after all files succeed
///
/// Tests can inject an alternate <see cref="HttpClient"/> and cache root.
/// </summary>
public sealed class ModelDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;

    public ModelDownloadService()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(15) }, DefaultCacheRoot())
    {
    }

    internal ModelDownloadService(HttpClient httpClient, string cacheRoot)
    {
        _httpClient = httpClient;
        _cacheRoot = cacheRoot;
    }

    /// <summary>
    /// Directory where the NLLB-200 bundle is (or will be) cached, e.g.
    /// <c>C:\Users\foo\AppData\Local\Easydict\models\nllb-200-distilled-600M</c>.
    /// </summary>
    public string ModelDirectory =>
        Path.Combine(_cacheRoot, ModelManifest.CacheDirectoryName);

    /// <summary>
    /// True if every manifest file is present and the completion sentinel
    /// was written. False on a fresh or partial cache.
    /// </summary>
    public bool IsModelInstalled()
    {
        var sentinel = Path.Combine(ModelDirectory, ModelManifest.CompletionSentinel);
        if (!File.Exists(sentinel))
        {
            return false;
        }

        foreach (var file in ModelManifest.Files)
        {
            var path = Path.Combine(ModelDirectory, file.LocalFileName);
            if (!File.Exists(path))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Downloads every missing file in <see cref="ModelManifest.Files"/>. Existing
    /// files are skipped (no checksum check yet — re-download by deleting the dir).
    /// Throws on network failures so callers can map to a user-facing error.
    /// </summary>
    public async Task DownloadAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelDirectory);

        // Make the unpinned/unverified state loud at runtime so it can't slip
        // past code review or QA. Flagged by the PR reviewer; flipping to
        // `false` is paired with (a) pinning ModelManifest.Revision to an
        // immutable commit SHA and (b) populating every ModelFileEntry.Sha256
        // with the matching upstream LFS hash.
        var isMutableRevision = string.Equals(ModelManifest.Revision, "main", StringComparison.Ordinal);
        var hasUnverifiedFiles = ModelManifest.Files.Any(f => string.IsNullOrEmpty(f.Sha256));
        if (isMutableRevision || hasUnverifiedFiles)
        {
            Debug.WriteLine(
                "[ModelDownloadService] WARNING: model bundle is fetched from a mutable ref " +
                $"(Revision='{ModelManifest.Revision}') and/or has files without SHA-256 verification. " +
                "Installs are not reproducible. Before the OpenVINO provider ships as stable, " +
                "pin Revision to an immutable commit SHA and populate ModelFileEntry.Sha256 for every file.");
        }

        var totalBytes = ModelManifest.Files.Sum(f => f.ApproximateBytes);
        long bytesDoneAcrossFiles = 0;

        foreach (var file in ModelManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localPath = Path.Combine(ModelDirectory, file.LocalFileName);
            if (File.Exists(localPath))
            {
                // Skip already-downloaded; treat its full size as done for progress.
                bytesDoneAcrossFiles += file.ApproximateBytes;
                progress?.Report(new ModelDownloadProgress(
                    file.LocalFileName,
                    file.ApproximateBytes,
                    file.ApproximateBytes,
                    Percent(bytesDoneAcrossFiles, totalBytes)));
                continue;
            }

            Debug.WriteLine($"[ModelDownloadService] Downloading {file.LocalFileName}…");
            await DownloadFileAsync(
                file,
                localPath,
                progress,
                bytesDoneAcrossFiles,
                totalBytes,
                cancellationToken);

            bytesDoneAcrossFiles += file.ApproximateBytes;
        }

        // Write sentinel last — if anything above threw, IsModelInstalled stays false
        // and the next attempt resumes correctly.
        var sentinel = Path.Combine(ModelDirectory, ModelManifest.CompletionSentinel);
        await File.WriteAllTextAsync(sentinel, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    /// <summary>
    /// Wipes the cache directory. Useful when the user wants to force a re-download
    /// after a corrupt or interrupted previous attempt.
    /// </summary>
    public void DeleteCache()
    {
        if (Directory.Exists(ModelDirectory))
        {
            Directory.Delete(ModelDirectory, recursive: true);
        }
    }

    private async Task DownloadFileAsync(
        ModelFileEntry file,
        string localPath,
        IProgress<ModelDownloadProgress>? progress,
        long bytesDoneAcrossFiles,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var url = ModelManifest.GetDownloadUrl(file.RemoteRelativePath);

        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var fileTotalBytes = response.Content.Headers.ContentLength;
        var tmpPath = localPath + ".part";
        var moved = false;

        try
        {
            long fileBytesDone = 0;
            await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file_ = File.Create(tmpPath))
            {
                var buffer = new byte[81_920];
                int read;
                while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await file_.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    fileBytesDone += read;

                    progress?.Report(new ModelDownloadProgress(
                        file.LocalFileName,
                        fileBytesDone,
                        fileTotalBytes,
                        Percent(bytesDoneAcrossFiles + fileBytesDone, totalBytes)));
                }
            }

            // Truncated-transfer check: when the server advertised Content-Length
            // we MUST receive that many bytes. A short read indicates the
            // connection was closed mid-stream; without this check (and with
            // Sha256 null, see ModelManifest), a partial ONNX file would be
            // moved into place and treated as a successful install.
            if (fileTotalBytes is { } expectedBytes && fileBytesDone != expectedBytes)
            {
                throw new EndOfStreamException(
                    $"Truncated download for '{file.LocalFileName}': " +
                    $"expected {expectedBytes} bytes (Content-Length), got {fileBytesDone}. " +
                    "Retry the download.");
            }

            // Integrity check before publishing: when the manifest declares an
            // expected SHA-256, recompute it from the .part file and fail loudly
            // on mismatch. The .part is left in the finally block's delete path
            // so a corrupted download doesn't survive to be treated as the cached
            // model. Skipped when Sha256 is null (current default while Revision
            // is "main" — see ModelManifest XML doc).
            if (!string.IsNullOrEmpty(file.Sha256))
            {
                var actual = await ComputeSha256HexAsync(tmpPath, cancellationToken);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SHA-256 mismatch for '{file.LocalFileName}'. " +
                        $"Expected {file.Sha256}, got {actual}. " +
                        $"The downloaded file may be corrupt or the upstream model has changed.");
                }
            }

            // Atomic publish so a crash mid-write doesn't leave a half-file that
            // IsModelInstalled would treat as valid.
            File.Move(tmpPath, localPath, overwrite: true);
            moved = true;
        }
        finally
        {
            // On cancel / network failure / disk-full, remove the partial file so
            // the cache directory doesn't accumulate ".part" debris across retries.
            // We only delete when File.Move didn't run (otherwise the .part is gone).
            if (!moved && File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (IOException) { /* file may be locked by AV scanner; best-effort */ }
                catch (UnauthorizedAccessException) { /* same */ }
            }
        }
    }

    private static double Percent(long done, long total)
    {
        if (total <= 0)
        {
            return 0.0;
        }
        return Math.Clamp(done * 100.0 / total, 0.0, 100.0);
    }

    /// <summary>
    /// Streams the file through SHA-256 and returns the lowercase hex digest.
    /// Streaming (not <see cref="File.ReadAllBytes(string)"/>) keeps memory flat
    /// for the ~165 MB encoder model.
    /// </summary>
    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string DefaultCacheRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Easydict", "models");
    }
}
