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
    private const string OnnxRuntimeFileName = "onnxruntime.dll";

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

        // Parse output: see ParseDetections for the two supported output shapes.
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

        // Direct span access is ~3-5x faster than the multidim indexer on this hot path.
        var span = tensor.Buffer.Span;
        span.Fill(114f / 255f);  // gray letterbox padding

        var channelStride = ModelInputSize * ModelInputSize;
        var rBase = 0;
        var gBase = channelStride;
        var bBase = 2 * channelStride;
        var invScale = 1.0 / scale;

        // Nearest-neighbor resample into the non-padded rectangle.
        for (var y = 0; y < newH; y++)
        {
            var sy = Math.Min((int)(y * invScale), height - 1);
            var srcRowStart = sy * width;
            var dstRow = (y + padY) * ModelInputSize + padX;

            for (var x = 0; x < newW; x++)
            {
                var sx = Math.Min((int)(x * invScale), width - 1);
                var srcIdx = (srcRowStart + sx) * 4; // BGRA8
                if (srcIdx + 2 >= bgra.Length) continue;

                var dst = dstRow + x;
                span[rBase + dst] = bgra[srcIdx + 2] / 255f;
                span[gBase + dst] = bgra[srcIdx + 1] / 255f;
                span[bBase + dst] = bgra[srcIdx] / 255f;
            }
        }

        return (tensor, scale, scale, padX, padY);
    }

    /// <summary>
    /// Parse YOLO detection output tensor into LayoutDetection list.
    /// Supports TWO output layouts depending on how the model was exported:
    ///
    /// A) End-to-end with NMS baked in — <c>[1, N, 6]</c>. Each row is
    ///    <c>[x1, y1, x2, y2, confidence, class_id]</c> in model space (pre-NMS).
    ///    This is the format produced by the wybxc/DocLayout-YOLO-DocStructBench
    ///    Hugging Face export that we currently download.
    ///
    /// B) Classic raw anchor output — <c>[1, 4+numClasses, numAnchors]</c> with
    ///    <c>(cx, cy, w, h, score0, …, scoreK-1)</c>. Used by vanilla YOLOv8/v10
    ///    ONNX exports without bundled NMS.
    ///
    /// Both formats get the same letterbox-unmapping + image-bound clipping and
    /// go through the post-filter NMS (which is a no-op on format A but harmless).
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

        if (dims.Length != 3) return results;

        // Disambiguate format A vs B.
        // Format A: [1, N, 6] with N typically 300, always dims[2] == 6.
        // Format B: [1, 4+numClasses, numAnchors] with dims[1] == 14 for 10 classes
        //           and dims[2] typically in the thousands.
        var isEndToEnd = dims[2] == 6 && dims[1] >= 1 && dims[1] != 14;

        if (isEndToEnd)
        {
            var numDetections = dims[1];
            for (var i = 0; i < numDetections; i++)
            {
                var x1Raw = output[0, i, 0];
                var y1Raw = output[0, i, 1];
                var x2Raw = output[0, i, 2];
                var y2Raw = output[0, i, 3];
                var confidence = output[0, i, 4];
                var classIdxFloat = output[0, i, 5];

                if (confidence < confidenceThreshold) continue;

                var classIdx = (int)Math.Round(classIdxFloat);
                var regionType = MapClassToRegionType(classIdx);

                // Convert from model space to original image space.
                var x1 = (x1Raw - padX) / scaleX;
                var y1 = (y1Raw - padY) / scaleY;
                var x2 = (x2Raw - padX) / scaleX;
                var y2 = (y2Raw - padY) / scaleY;

                var bw = x2 - x1;
                var bh = y2 - y1;

                // Clip to image bounds.
                x1 = Math.Max(0, Math.Min(x1, originalWidth));
                y1 = Math.Max(0, Math.Min(y1, originalHeight));
                bw = Math.Min(bw, originalWidth - x1);
                bh = Math.Min(bh, originalHeight - y1);

                if (bw <= 0 || bh <= 0) continue;

                results.Add(new LayoutDetection(regionType, confidence, x1, y1, bw, bh));
            }

            // Format A already has NMS baked in by the model export, but we
            // still run our NMS as a defensive dedup — cheap on ≤300 boxes.
            return ApplyNms(results, 0.45f);
        }

        // Format B: classic YOLOv8/v10 raw output.
        var numFeatures = dims[1]; // 4 (bbox) + numClasses
        var rawNumDetections = dims[2];
        var numClasses = numFeatures - 4;

        if (numClasses <= 0) return results;

        for (var i = 0; i < rawNumDetections; i++)
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
    internal static float ComputeIoU(LayoutDetection a, LayoutDetection b) =>
        ComputeRectIoU(a.X, a.Y, a.Width, a.Height, b.X, b.Y, b.Width, b.Height);

    /// <summary>
    /// Compute Intersection over Union between two axis-aligned rectangles.
    /// Shared by <see cref="DocLayoutYoloService"/> NMS and TATR row/column
    /// deduplication — both operate in the same (pixel) coordinate space.
    /// </summary>
    internal static float ComputeRectIoU(
        double ax, double ay, double aw, double ah,
        double bx, double by, double bw, double bh)
    {
        var x1 = Math.Max(ax, bx);
        var y1 = Math.Max(ay, by);
        var x2 = Math.Min(ax + aw, bx + bw);
        var y2 = Math.Min(ay + ah, by + bh);

        var interW = Math.Max(0, x2 - x1);
        var interH = Math.Max(0, y2 - y1);
        var interArea = interW * interH;

        var aArea = aw * ah;
        var bArea = bw * bh;
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
