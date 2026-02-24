using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.WinUI.Services;

/// <summary>
/// A single layout detection result from DocLayout-YOLO inference.
/// </summary>
public readonly record struct LayoutDetection(
    LayoutRegionType RegionType,
    float Confidence,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// Runs DocLayout-YOLO ONNX model inference for PDF page layout detection.
/// Native ONNX Runtime DLL is loaded dynamically from the download directory.
/// </summary>
public sealed class DocLayoutYoloService : IDisposable
{
    private const int ModelInputSize = 1024;

    // DocLayout-YOLO DocStructBench class names (10 classes)
    internal static readonly string[] ClassNames =
    [
        "title",
        "plain text",
        "abandon",
        "figure",
        "figure_caption",
        "table",
        "table_caption",
        "table_footnote",
        "isolate_formula",
        "formula_caption"
    ];

    // Map class index → LayoutRegionType
    internal static readonly LayoutRegionType[] ClassToRegionType =
    [
        LayoutRegionType.Title,           // 0: title
        LayoutRegionType.Body,            // 1: plain text
        LayoutRegionType.Figure,          // 2: abandon (figures/decorations to skip)
        LayoutRegionType.Figure,          // 3: figure
        LayoutRegionType.Caption,         // 4: figure_caption
        LayoutRegionType.Table,           // 5: table
        LayoutRegionType.Caption,         // 6: table_caption
        LayoutRegionType.Caption,         // 7: table_footnote
        LayoutRegionType.IsolatedFormula, // 8: isolate_formula
        LayoutRegionType.Caption,         // 9: formula_caption
    ];

    private readonly LayoutModelDownloadService _downloadService;
    private InferenceSession? _session;
    private bool _disposed;
    private static bool _nativeResolverRegistered;
    private static readonly object _resolverLock = new();

    public DocLayoutYoloService(LayoutModelDownloadService downloadService)
    {
        _downloadService = downloadService;
    }

    /// <summary>Whether the ONNX session is loaded and ready for inference.</summary>
    public bool IsInitialized => _session is not null;

    /// <summary>
    /// Initialize the ONNX session. Downloads runtime and model if needed.
    /// </summary>
    public async Task InitializeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_session is not null)
            return;

        await _downloadService.EnsureAvailableAsync(progress, ct);

        RegisterNativeLibraryResolver();

        var modelPath = _downloadService.GetModelPath()
                        ?? throw new InvalidOperationException("Model file not available after download.");

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        // Use CPU execution provider
        _session = new InferenceSession(modelPath, options);
        Debug.WriteLine($"[DocLayoutYolo] Model loaded: {modelPath}");
    }

    /// <summary>
    /// Detect layout regions in a page image.
    /// </summary>
    /// <param name="imagePixels">BGRA8 pixel data (4 bytes per pixel).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="confidenceThreshold">Minimum confidence to include a detection.</param>
    /// <returns>List of detected layout regions.</returns>
    public List<LayoutDetection> Detect(
        byte[] imagePixels,
        int width,
        int height,
        float confidenceThreshold = 0.25f)
    {
        ThrowIfDisposed();

        if (_session is null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        // Preprocess: BGRA8 → letterbox-resized RGB float tensor [1, 3, 1024, 1024]
        var (inputTensor, scaleX, scaleY, padX, padY) = PreprocessImage(imagePixels, width, height);

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("images", inputTensor)
        };

        using var results = _session.Run(inputs);

        // Parse output: YOLO format [1, numClasses+4, numDetections]
        // The output tensor shape is [1, 14, 8400] where 14 = 4 (bbox) + 10 (classes)
        var outputTensor = results.First().AsTensor<float>();
        return ParseDetections(outputTensor, scaleX, scaleY, padX, padY, width, height, confidenceThreshold);
    }

    /// <summary>
    /// Maps a class index to the corresponding <see cref="LayoutRegionType"/>.
    /// </summary>
    internal static LayoutRegionType MapClassToRegionType(int classIndex)
    {
        return classIndex >= 0 && classIndex < ClassToRegionType.Length
            ? ClassToRegionType[classIndex]
            : LayoutRegionType.Unknown;
    }

    /// <summary>
    /// Preprocess image: convert BGRA8 to RGB float tensor with letterbox resize to ModelInputSize.
    /// </summary>
    internal static (DenseTensor<float> Tensor, double ScaleX, double ScaleY, int PadX, int PadY)
        PreprocessImage(byte[] bgra, int width, int height)
    {
        // Calculate letterbox dimensions
        var scale = Math.Min((double)ModelInputSize / width, (double)ModelInputSize / height);
        var newW = (int)Math.Round(width * scale);
        var newH = (int)Math.Round(height * scale);
        var padX = (ModelInputSize - newW) / 2;
        var padY = (ModelInputSize - newH) / 2;

        var tensor = new DenseTensor<float>([1, 3, ModelInputSize, ModelInputSize]);

        // Fill with gray (114/255) for padding
        var grayValue = 114f / 255f;
        for (var c = 0; c < 3; c++)
            for (var y = 0; y < ModelInputSize; y++)
                for (var x = 0; x < ModelInputSize; x++)
                    tensor[0, c, y, x] = grayValue;

        // Bilinear resize and fill the tensor
        for (var y = 0; y < newH; y++)
        {
            for (var x = 0; x < newW; x++)
            {
                // Map back to original image coordinates
                var srcX = x / scale;
                var srcY = y / scale;

                // Nearest-neighbor sampling for simplicity
                var sx = Math.Min((int)srcX, width - 1);
                var sy = Math.Min((int)srcY, height - 1);

                var srcIdx = (sy * width + sx) * 4; // BGRA8 format
                if (srcIdx + 2 >= bgra.Length) continue;

                var b = bgra[srcIdx] / 255f;
                var g = bgra[srcIdx + 1] / 255f;
                var r = bgra[srcIdx + 2] / 255f;

                tensor[0, 0, y + padY, x + padX] = r;
                tensor[0, 1, y + padY, x + padX] = g;
                tensor[0, 2, y + padY, x + padX] = b;
            }
        }

        return (tensor, scale, scale, padX, padY);
    }

    /// <summary>
    /// Parse YOLO detection output tensor into LayoutDetection list.
    /// Output shape: [1, numClasses+4, numDetections] where bbox format is (cx, cy, w, h).
    /// </summary>
    internal static List<LayoutDetection> ParseDetections(
        Tensor<float> output,
        double scaleX,
        double scaleY,
        int padX,
        int padY,
        int originalWidth,
        int originalHeight,
        float confidenceThreshold)
    {
        var results = new List<LayoutDetection>();
        var dims = output.Dimensions;

        // Expected shape: [1, 4+numClasses, numDetections]
        if (dims.Length != 3) return results;

        var numFeatures = dims[1]; // 4 (bbox) + numClasses
        var numDetections = dims[2];
        var numClasses = numFeatures - 4;

        if (numClasses <= 0) return results;

        for (var i = 0; i < numDetections; i++)
        {
            // Find best class
            var bestClassIdx = -1;
            var bestClassScore = float.MinValue;
            for (var c = 0; c < numClasses; c++)
            {
                var score = output[0, 4 + c, i];
                if (score > bestClassScore)
                {
                    bestClassScore = score;
                    bestClassIdx = c;
                }
            }

            if (bestClassScore < confidenceThreshold) continue;

            // Get bbox (cx, cy, w, h) in model space
            var cx = output[0, 0, i];
            var cy = output[0, 1, i];
            var w = output[0, 2, i];
            var h = output[0, 3, i];

            // Convert from model space to original image space
            var x1 = (cx - w / 2 - padX) / scaleX;
            var y1 = (cy - h / 2 - padY) / scaleY;
            var bw = w / scaleX;
            var bh = h / scaleY;

            // Clip to image bounds
            x1 = Math.Max(0, Math.Min(x1, originalWidth));
            y1 = Math.Max(0, Math.Min(y1, originalHeight));
            bw = Math.Min(bw, originalWidth - x1);
            bh = Math.Min(bh, originalHeight - y1);

            if (bw <= 0 || bh <= 0) continue;

            var regionType = MapClassToRegionType(bestClassIdx);

            results.Add(new LayoutDetection(regionType, bestClassScore, x1, y1, bw, bh));
        }

        // Apply NMS (Non-Maximum Suppression) per class
        return ApplyNms(results, 0.45f);
    }

    /// <summary>
    /// Non-Maximum Suppression: remove overlapping detections of the same class.
    /// </summary>
    internal static List<LayoutDetection> ApplyNms(List<LayoutDetection> detections, float iouThreshold)
    {
        var result = new List<LayoutDetection>();
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        var suppressed = new bool[sorted.Count];

        for (var i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            result.Add(sorted[i]);

            for (var j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed[j]) continue;
                if (sorted[i].RegionType != sorted[j].RegionType) continue;

                var iou = ComputeIoU(sorted[i], sorted[j]);
                if (iou > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Compute Intersection over Union between two detections.
    /// </summary>
    internal static float ComputeIoU(LayoutDetection a, LayoutDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        var interW = Math.Max(0, x2 - x1);
        var interH = Math.Max(0, y2 - y1);
        var interArea = interW * interH;

        var aArea = a.Width * a.Height;
        var bArea = b.Width * b.Height;
        var unionArea = aArea + bArea - interArea;

        return unionArea > 0 ? (float)(interArea / unionArea) : 0f;
    }

    /// <summary>
    /// Register a native library resolver to load onnxruntime.dll from the download directory.
    /// </summary>
    private void RegisterNativeLibraryResolver()
    {
        lock (_resolverLock)
        {
            if (_nativeResolverRegistered) return;

            var nativeDir = _downloadService.GetNativeLibraryDir();
            if (nativeDir is null) return;

            NativeLibrary.SetDllImportResolver(
                typeof(InferenceSession).Assembly,
                (libraryName, assembly, searchPath) =>
                {
                    if (libraryName == "onnxruntime")
                    {
                        var path = Path.Combine(nativeDir, OnnxRuntimeFileName);
                        if (NativeLibrary.TryLoad(path, out var handle))
                        {
                            Debug.WriteLine($"[DocLayoutYolo] Loaded native library from {path}");
                            return handle;
                        }
                    }

                    return IntPtr.Zero;
                });

            _nativeResolverRegistered = true;
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
        _session?.Dispose();
        _session = null;
    }
}
