using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Easydict.WinUI.Services;

/// <summary>
/// Progress report for model/runtime downloads.
/// </summary>
public sealed record ModelDownloadProgress(
    string Stage,
    long BytesDownloaded,
    long TotalBytes,
    double Percentage);

/// <summary>
/// Manages downloading and caching of ONNX Runtime native library and DocLayout-YOLO model.
/// All artifacts are stored under <c>%LocalAppData%\Easydict\Models\</c>.
/// </summary>
public sealed class LayoutModelDownloadService : IDisposable
{
    private const string ModelsSubDir = "Models";
    private const string OnnxRuntimeFileName = "onnxruntime.dll";
    private const string ModelFileName = "doclayout_yolo.onnx";

    // ONNX Runtime 1.21.0 - win-x64 native library
    // Download URLs: primary (GitHub Release), fallback (NuGet extract)
    private static readonly string[] OnnxRuntimeUrls =
    [
        "https://github.com/microsoft/onnxruntime/releases/download/v1.21.0/onnxruntime-win-x64-1.21.0.zip",
    ];

    // DocLayout-YOLO model - primary (HuggingFace), fallback (GitHub Releases)
    private static readonly string[] ModelUrls =
    [
        "https://huggingface.co/juliozhao/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx",
        "https://github.com/opendatalab/DocLayout-YOLO/releases/download/v0.0.1/doclayout_yolo_docstructbench_imgsz1024.onnx",
    ];

    // Path within the ONNX Runtime zip to the native DLL
    private const string OnnxRuntimeZipEntryPath = "onnxruntime-win-x64-1.21.0/lib/onnxruntime.dll";

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    private readonly string _modelsDir;
    private readonly string _nativeLibPath;
    private readonly string _modelPath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private bool _disposed;

    public LayoutModelDownloadService() : this(null) { }

    public LayoutModelDownloadService(HttpClient? httpClient)
    {
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict", ModelsSubDir);
        Directory.CreateDirectory(_modelsDir);

        _nativeLibPath = Path.Combine(_modelsDir, OnnxRuntimeFileName);
        _modelPath = Path.Combine(_modelsDir, ModelFileName);
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    /// <summary>Whether both native runtime and model file are present and valid.</summary>
    public bool IsReady => File.Exists(_nativeLibPath) && File.Exists(_modelPath);

    /// <summary>Whether the ONNX native runtime is downloaded.</summary>
    public bool IsRuntimeReady => File.Exists(_nativeLibPath);

    /// <summary>Whether the ONNX model file is downloaded.</summary>
    public bool IsModelReady => File.Exists(_modelPath);

    /// <summary>Gets the path to the ONNX model file, or null if not downloaded.</summary>
    public string? GetModelPath() => File.Exists(_modelPath) ? _modelPath : null;

    /// <summary>Gets the directory containing the native ONNX Runtime library.</summary>
    public string? GetNativeLibraryDir() => File.Exists(_nativeLibPath) ? _modelsDir : null;

    /// <summary>Gets the full path to the native ONNX Runtime library.</summary>
    public string? GetNativeLibraryPath() => File.Exists(_nativeLibPath) ? _nativeLibPath : null;

    /// <summary>
    /// Ensures both ONNX Runtime and model are downloaded and available.
    /// Downloads missing files with progress reporting and retry logic.
    /// </summary>
    public async Task EnsureAvailableAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_nativeLibPath))
            {
                await DownloadOnnxRuntimeAsync(progress, ct);
            }

            if (!File.Exists(_modelPath))
            {
                await DownloadModelAsync(progress, ct);
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task DownloadOnnxRuntimeAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Debug.WriteLine("[LayoutModelDownload] Downloading ONNX Runtime native library...");

        var tempZipPath = Path.Combine(_modelsDir, "onnxruntime_temp.zip");
        try
        {
            await DownloadWithRetryAsync(OnnxRuntimeUrls, tempZipPath, "runtime", progress, ct);

            // Extract the native DLL from the zip
            using var archive = System.IO.Compression.ZipFile.OpenRead(tempZipPath);
            var entry = archive.GetEntry(OnnxRuntimeZipEntryPath)
                        ?? throw new InvalidOperationException(
                            $"Entry '{OnnxRuntimeZipEntryPath}' not found in ONNX Runtime zip.");

            using var entryStream = entry.Open();
            using var fileStream = File.Create(_nativeLibPath);
            await entryStream.CopyToAsync(fileStream, ct);

            Debug.WriteLine($"[LayoutModelDownload] ONNX Runtime extracted to {_nativeLibPath}");
        }
        finally
        {
            TryDeleteFile(tempZipPath);
        }
    }

    private async Task DownloadModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Debug.WriteLine("[LayoutModelDownload] Downloading DocLayout-YOLO model...");
        await DownloadWithRetryAsync(ModelUrls, _modelPath, "model", progress, ct);
        Debug.WriteLine($"[LayoutModelDownload] Model downloaded to {_modelPath}");
    }

    private async Task DownloadWithRetryAsync(
        string[] urls,
        string outputPath,
        string stage,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var tempPath = outputPath + ".tmp";
        Exception? lastException = null;

        foreach (var url in urls)
        {
            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                        Debug.WriteLine($"[LayoutModelDownload] Retry {attempt}/{MaxRetries} after {delay.TotalSeconds}s for {url}");
                        await Task.Delay(delay, ct);
                    }

                    await DownloadFileAsync(url, tempPath, stage, progress, ct);

                    // Move temp to final location atomically
                    File.Move(tempPath, outputPath, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDeleteFile(tempPath);
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.WriteLine($"[LayoutModelDownload] Download failed: {ex.Message}");
                    TryDeleteFile(tempPath);
                }
            }

            Debug.WriteLine($"[LayoutModelDownload] All retries exhausted for {url}, trying next source...");
        }

        throw new InvalidOperationException(
            $"Failed to download {stage} from all sources.", lastException);
    }

    private async Task DownloadFileAsync(
        string url,
        string outputPath,
        string stage,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        long bytesDownloaded = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(outputPath);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            bytesDownloaded += bytesRead;

            var percentage = totalBytes > 0 ? (double)bytesDownloaded / totalBytes * 100 : -1;
            progress?.Report(new ModelDownloadProgress(stage, bytesDownloaded, totalBytes, percentage));
        }
    }

    /// <summary>
    /// Deletes all downloaded model files to free disk space.
    /// </summary>
    public void DeleteAll()
    {
        ThrowIfDisposed();
        TryDeleteFile(_nativeLibPath);
        TryDeleteFile(_modelPath);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* ignore cleanup errors */ }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler();
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Easydict-Win32/1.0");
        return client;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _downloadLock.Dispose();
    }
}
