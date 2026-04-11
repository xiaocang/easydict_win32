using System.Diagnostics;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages downloading and caching of ONNX Runtime native library, DocLayout-YOLO
/// (stage 1 layout detector), and Microsoft Table Transformer (TATR, stage 2 table
/// structure recognizer). All artifacts are stored under
/// <c>%LocalAppData%\Easydict\Models\</c>.
/// Downloads auto-select the fastest source from multiple mirrors.
/// </summary>
public sealed class LayoutModelDownloadService : IDisposable
{
    private const string ModelsSubDir = "Models";
    private const string OnnxRuntimeFileName = "onnxruntime.dll";
    private const string ModelFileName = "doclayout_yolo.onnx";
    private const string TatrModelFileName = "tatr_structure.onnx";

    // Minimum valid file sizes to detect truncated downloads or HTML error pages
    private const long MinRuntimeFileSize = 5 * 1024 * 1024;   // 5 MB (actual ~10 MB)
    private const long MinModelFileSize = 20 * 1024 * 1024;     // 20 MB (actual ~50 MB)
    private const long MinTatrFileSize = 60 * 1024 * 1024;      // 60 MB (actual ~116 MB fp32)

    // ONNX Runtime 1.21.0 - win-x64 native library
    private static readonly string[] OnnxRuntimeUrls =
    [
        "https://github.com/microsoft/onnxruntime/releases/download/v1.21.0/onnxruntime-win-x64-1.21.0.zip",
    ];

    // DocLayout-YOLO model — HuggingFace, HF Mirror (China), ModelScope (China)
    private static readonly string[] ModelUrls =
    [
        "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx",
        "https://hf-mirror.com/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx",
        "https://www.modelscope.cn/models/AI-ModelScope/DocLayout-YOLO-DocStructBench-onnx/resolve/master/doclayout_yolo_docstructbench_imgsz1024.onnx",
    ];

    // TATR structure recognition model — Xenova's fp32 ONNX export of
    // microsoft/table-transformer-structure-recognition (v1.0, MIT license).
    // Outputs row / column / spanning-cell bounding boxes inside a table crop.
    // fp32 (not fp16) because ORT .NET's Float16 tensor ergonomics are poor and
    // the extra ~58 MB is a one-time download cost.
    private static readonly string[] TatrModelUrls =
    [
        "https://huggingface.co/Xenova/table-transformer-structure-recognition/resolve/main/onnx/model.onnx",
        "https://hf-mirror.com/Xenova/table-transformer-structure-recognition/resolve/main/onnx/model.onnx",
    ];

    // Path within the ONNX Runtime zip to the native DLL
    private const string OnnxRuntimeZipEntryPath = "onnxruntime-win-x64-1.21.0/lib/onnxruntime.dll";

    private readonly string _modelsDir;
    private readonly string _nativeLibPath;
    private readonly string _modelPath;
    private readonly string _tatrModelPath;
    private readonly ModelDownloadClient _client;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly SemaphoreSlim _tatrDownloadLock = new(1, 1);
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
        _tatrModelPath = Path.Combine(_modelsDir, TatrModelFileName);
        _client = new ModelDownloadClient(httpClient);
    }

    /// <summary>Whether both native runtime and model file are present and valid.</summary>
    public bool IsReady => IsRuntimeReady && IsModelReady;

    /// <summary>Whether the ONNX native runtime is downloaded and valid.</summary>
    public bool IsRuntimeReady => ModelDownloadClient.IsFileValid(_nativeLibPath, MinRuntimeFileSize);

    /// <summary>Whether the ONNX model file is downloaded and valid.</summary>
    public bool IsModelReady => ModelDownloadClient.IsFileValid(_modelPath, MinModelFileSize);

    /// <summary>Whether the TATR table-structure model file is downloaded and valid.</summary>
    public bool IsTatrModelReady => ModelDownloadClient.IsFileValid(_tatrModelPath, MinTatrFileSize);

    /// <summary>Gets the path to the ONNX model file, or null if not downloaded/valid.</summary>
    public string? GetModelPath() => IsModelReady ? _modelPath : null;

    /// <summary>Gets the path to the TATR model file, or null if not downloaded/valid.</summary>
    public string? GetTatrModelPath() => IsTatrModelReady ? _tatrModelPath : null;

    /// <summary>Gets the directory containing the native ONNX Runtime library.</summary>
    public string? GetNativeLibraryDir() => IsRuntimeReady ? _modelsDir : null;

    /// <summary>Gets the full path to the native ONNX Runtime library.</summary>
    public string? GetNativeLibraryPath() => IsRuntimeReady ? _nativeLibPath : null;

    /// <summary>
    /// Ensures both ONNX Runtime and model are downloaded and available.
    /// Downloads missing files with progress reporting and retry logic.
    /// Auto-selects the fastest source from multiple mirrors.
    /// </summary>
    public async Task EnsureAvailableAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _downloadLock.WaitAsync(ct);
        try
        {
            // Clean up invalid files from previous failed/truncated downloads
            CleanupInvalidFiles();

            if (!IsRuntimeReady)
            {
                await DownloadOnnxRuntimeAsync(progress, ct);
            }

            if (!IsModelReady)
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
            await _client.DownloadWithRetryAsync(OnnxRuntimeUrls, tempZipPath, "runtime", progress, ct);

            // Extract the native DLL from the zip
            using var archive = System.IO.Compression.ZipFile.OpenRead(tempZipPath);
            var entry = archive.GetEntry(OnnxRuntimeZipEntryPath)
                        ?? throw new InvalidOperationException(
                            $"Entry '{OnnxRuntimeZipEntryPath}' not found in ONNX Runtime zip.");

            using var entryStream = entry.Open();
            using var fileStream = File.Create(_nativeLibPath);
            await entryStream.CopyToAsync(fileStream, ct);

            Debug.WriteLine($"[LayoutModelDownload] ONNX Runtime extracted to {_nativeLibPath}");

            // Validate extracted file
            if (!ModelDownloadClient.IsFileValid(_nativeLibPath, MinRuntimeFileSize))
            {
                ModelDownloadClient.TryDeleteFile(_nativeLibPath);
                throw new InvalidOperationException(
                    "Extracted runtime file is too small, likely corrupted.");
            }
        }
        finally
        {
            ModelDownloadClient.TryDeleteFile(tempZipPath);
        }
    }

    private async Task DownloadModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Debug.WriteLine("[LayoutModelDownload] Downloading DocLayout-YOLO model...");

        // Auto-select fastest source
        var orderedUrls = await _client.GetOrderedUrlsAsync(ModelUrls, ct);
        await _client.DownloadWithRetryAsync(orderedUrls, _modelPath, "model", progress, ct);

        // Validate downloaded file
        if (!ModelDownloadClient.IsFileValid(_modelPath, MinModelFileSize))
        {
            ModelDownloadClient.TryDeleteFile(_modelPath);
            throw new InvalidOperationException(
                "Downloaded model file is too small, likely corrupted or an error page.");
        }

        Debug.WriteLine($"[LayoutModelDownload] Model downloaded to {_modelPath}");
    }

    /// <summary>
    /// Ensures the TATR table-structure model is downloaded. Separate from
    /// <see cref="EnsureAvailableAsync"/> because TATR is an optional stage-2
    /// model: the app works without it (tables fall back to single-block
    /// preservation). Callers should invoke this lazily on first use.
    /// </summary>
    public async Task EnsureTatrAvailableAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _tatrDownloadLock.WaitAsync(ct);
        try
        {
            // Clean up invalid file from a previous failed/truncated download.
            if (File.Exists(_tatrModelPath) && !ModelDownloadClient.IsFileValid(_tatrModelPath, MinTatrFileSize))
            {
                Debug.WriteLine($"[LayoutModelDownload] Cleaning up invalid TATR file ({new FileInfo(_tatrModelPath).Length} bytes)");
                ModelDownloadClient.TryDeleteFile(_tatrModelPath);
            }

            if (IsTatrModelReady) return;

            Debug.WriteLine("[LayoutModelDownload] Downloading TATR table-structure model...");
            var orderedUrls = await _client.GetOrderedUrlsAsync(TatrModelUrls, ct);
            await _client.DownloadWithRetryAsync(orderedUrls, _tatrModelPath, "tatr", progress, ct);

            if (!ModelDownloadClient.IsFileValid(_tatrModelPath, MinTatrFileSize))
            {
                ModelDownloadClient.TryDeleteFile(_tatrModelPath);
                throw new InvalidOperationException(
                    "Downloaded TATR model file is too small, likely corrupted or an error page.");
            }

            Debug.WriteLine($"[LayoutModelDownload] TATR model downloaded to {_tatrModelPath}");
        }
        finally
        {
            _tatrDownloadLock.Release();
        }
    }

    /// <summary>
    /// Deletes all downloaded model files to free disk space.
    /// </summary>
    public void DeleteAll()
    {
        ThrowIfDisposed();
        ModelDownloadClient.TryDeleteFile(_nativeLibPath);
        ModelDownloadClient.TryDeleteFile(_modelPath);
        ModelDownloadClient.TryDeleteFile(_tatrModelPath);
    }

    /// <summary>
    /// Removes files that exist but are too small (truncated or error page downloads).
    /// </summary>
    private void CleanupInvalidFiles()
    {
        if (File.Exists(_nativeLibPath) && !ModelDownloadClient.IsFileValid(_nativeLibPath, MinRuntimeFileSize))
        {
            Debug.WriteLine($"[LayoutModelDownload] Cleaning up invalid runtime file ({new FileInfo(_nativeLibPath).Length} bytes)");
            ModelDownloadClient.TryDeleteFile(_nativeLibPath);
        }

        if (File.Exists(_modelPath) && !ModelDownloadClient.IsFileValid(_modelPath, MinModelFileSize))
        {
            Debug.WriteLine($"[LayoutModelDownload] Cleaning up invalid model file ({new FileInfo(_modelPath).Length} bytes)");
            ModelDownloadClient.TryDeleteFile(_modelPath);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _downloadLock.Dispose();
        _tatrDownloadLock.Dispose();
    }
}
