using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.WinUI.Services;

/// <summary>
/// A single DETR detection inside a TATR table crop. Coordinates are in
/// <strong>page-image pixel space</strong> (top-left origin), already translated
/// back from the resized crop the model saw.
/// </summary>
public readonly record struct TableSubDetection(
    TableElementClass Class,
    float Confidence,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>TATR v1.0 output classes (6 total). Matches the id2label in the
/// Xenova preprocessor_config.json. The "no-object" sentinel (index 6) is
/// filtered out during parsing and is not part of this enum.</summary>
public enum TableElementClass
{
    Table = 0,
    Column = 1,
    Row = 2,
    ColumnHeader = 3,
    ProjectedRowHeader = 4,
    SpanningCell = 5,
}

/// <summary>
/// Cell bounds derived from intersecting TATR rows with TATR columns.
/// Coordinates are page-image pixel space (top-left origin).
/// </summary>
public readonly record struct TableCellBounds(
    int RowIndex,
    int ColumnIndex,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// Full table structure produced by TATR: row / column / spanning-cell bboxes
/// plus a derived cell grid. All coordinates in page-image pixel space.
/// </summary>
public sealed record TableStructure
{
    public required IReadOnlyList<TableSubDetection> Rows { get; init; }
    public required IReadOnlyList<TableSubDetection> Columns { get; init; }
    public required IReadOnlyList<TableSubDetection> SpanningCells { get; init; }
    public required IReadOnlyList<TableCellBounds> Cells { get; init; }
}

/// <summary>
/// Runs Microsoft Table Transformer (TATR) v1.0 ONNX inference to extract
/// row / column / spanning-cell structure inside a table region already
/// detected by DocLayout-YOLO.
///
/// Pipeline:
///   (a) crop the page BGRA8 buffer to the table bbox
///   (b) letterbox-resize to TATR's expected input (shortest edge 800,
///       longest edge ≤ 1000, aspect preserved)
///   (c) ImageNet-normalize and feed to the DETR session
///   (d) parse the 125-query output to row / column / spanning cell detections
///   (e) build a cell grid by intersecting rows with columns
///   (f) translate all coordinates back to page-image pixel space
///
/// All failures degrade to <c>null</c> — the caller falls back to
/// current single-block table preservation behavior.
/// </summary>
public sealed class TableStructureRecognitionService : IDisposable
{
    // TATR v1.0 preprocessor config (Xenova/table-transformer-structure-recognition).
    internal const int ShortestEdge = 800;
    internal const int LongestEdge = 1000;
    internal static readonly float[] ImageMean = [0.485f, 0.456f, 0.406f];
    internal static readonly float[] ImageStd = [0.229f, 0.224f, 0.225f];

    // DETR output: 125 object queries, 6 classes + 1 no-object sentinel.
    internal const int NumQueries = 125;
    internal const int NumClasses = 6;
    internal const int NoObjectClassIndex = 6;

    // Confidence threshold for keeping a detection. TATR is calibrated; 0.5 is
    // the published recommendation but we use 0.3 because our crops are rescaled
    // from lower-DPI page renders (1024 max on the long edge vs. the model's
    // original 800-1000 training range), so confidence is typically lower.
    internal const float DefaultConfidenceThreshold = 0.3f;

    // Deduplication: two rows/columns with IoU above this are merged (kept the
    // higher-confidence one). DETR occasionally emits near-duplicate queries.
    internal const float DuplicateIoUThreshold = 0.8f;

    // A cell with fewer than this many rendered-image pixels on either side is
    // discarded — usually fragments from numerical overflow at the edge.
    internal const double MinCellSidePx = 4.0;

    // Hard cap on cells per table — anything higher is almost certainly a mis-
    // parse and the caller should fall back to single-block behavior.
    internal const int MaxCellsPerTable = 400;

    private readonly LayoutModelDownloadService _downloadService;
    private InferenceSession? _session;
    private string? _inputName;
    private bool _disposed;

    public TableStructureRecognitionService(LayoutModelDownloadService downloadService)
    {
        _downloadService = downloadService;
    }

    /// <summary>Whether the ONNX session is loaded and ready for inference.</summary>
    public bool IsInitialized => _session is not null;

    /// <summary>
    /// Initialize the ONNX session. Downloads the TATR model if needed.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public async Task InitializeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_session is not null) return;

        await _downloadService.EnsureTatrAvailableAsync(progress, ct).ConfigureAwait(false);

        var modelPath = _downloadService.GetTatrModelPath()
                        ?? throw new InvalidOperationException("TATR model file not available after download.");

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        _session = new InferenceSession(modelPath, options);

        // Discover actual input name + shape — DETR ONNX exports sometimes name
        // the input "pixel_values", "input", or "images" depending on exporter.
        var inputMetaSummary = string.Join(",",
            _session.InputMetadata.Select(kv => $"{kv.Key}[{string.Join("x", kv.Value.Dimensions)}]"));
        var outputMetaSummary = string.Join(",",
            _session.OutputMetadata.Select(kv => $"{kv.Key}[{string.Join("x", kv.Value.Dimensions)}]"));
        Debug.WriteLine($"[TATR] Model loaded: {modelPath}");
        Debug.WriteLine($"[TATR] Inputs: {inputMetaSummary}");
        Debug.WriteLine($"[TATR] Outputs: {outputMetaSummary}");

        if (_session.InputMetadata.Count > 0)
        {
            _inputName = _session.InputMetadata.Keys.First();
        }
    }

    /// <summary>
    /// Recognize the internal structure of one table region.
    /// </summary>
    /// <param name="pageBgra8">Full page BGRA8 pixels from LayoutDetectionStrategy.RenderPdfPageAsync.</param>
    /// <param name="pageWidth">Page image width in pixels.</param>
    /// <param name="pageHeight">Page image height in pixels.</param>
    /// <param name="tableX">Table bbox X in page-image pixel space (top-left origin).</param>
    /// <param name="tableY">Table bbox Y in page-image pixel space.</param>
    /// <param name="tableWidth">Table bbox width in pixels.</param>
    /// <param name="tableHeight">Table bbox height in pixels.</param>
    /// <param name="confidenceThreshold">Detection confidence floor. Default 0.5.</param>
    /// <returns>Table structure with page-space cell bounds, or <c>null</c> on any failure.</returns>
    public TableStructure? Recognize(
        byte[] pageBgra8,
        int pageWidth,
        int pageHeight,
        double tableX,
        double tableY,
        double tableWidth,
        double tableHeight,
        float confidenceThreshold = DefaultConfidenceThreshold)
    {
        ThrowIfDisposed();

        if (_session is null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");

        // Clamp table bbox to page bounds.
        var clampedX = Math.Max(0, Math.Min(tableX, pageWidth - 1));
        var clampedY = Math.Max(0, Math.Min(tableY, pageHeight - 1));
        var clampedW = Math.Max(0, Math.Min(tableWidth, pageWidth - clampedX));
        var clampedH = Math.Max(0, Math.Min(tableHeight, pageHeight - clampedY));

        // Very small crops produce no meaningful cells — bail early.
        if (clampedW < 32 || clampedH < 32)
        {
            Debug.WriteLine($"[TATR] Recognize: crop too small ({clampedW:F0}x{clampedH:F0}), skipping");
            return null;
        }

        // Preprocess: crop → RGB float tensor at (newH, newW) after aspect-preserving resize.
        var (inputTensor, newW, newH) = PreprocessCrop(
            pageBgra8, pageWidth, pageHeight,
            (int)Math.Round(clampedX), (int)Math.Round(clampedY),
            (int)Math.Round(clampedW), (int)Math.Round(clampedH));

        Debug.WriteLine($"[TATR] Recognize: crop=({clampedX:F0},{clampedY:F0},{clampedW:F0}x{clampedH:F0}) → tensor={newH}x{newW}, threshold={confidenceThreshold}");

        // Run inference. Input name discovered at init time (stored in _inputName).
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName ?? "pixel_values", inputTensor)
        };

        List<TableSubDetection> parsed;
        try
        {
            using var results = _session.Run(inputs);
            Tensor<float>? logits = null;
            Tensor<float>? predBoxes = null;
            foreach (var r in results)
            {
                if (r.Name == "logits") logits = r.AsTensor<float>();
                else if (r.Name == "pred_boxes") predBoxes = r.AsTensor<float>();
            }
            if (logits is null || predBoxes is null)
            {
                var names = string.Join(",", results.Select(r => $"{r.Name}{r.AsTensor<float>().Dimensions.ToArray().Aggregate("", (a, d) => a + "x" + d)}"));
                Debug.WriteLine($"[TATR] Missing logits or pred_boxes output. Got: {names}");
                return null;
            }

            parsed = ParseDetrOutput(logits, predBoxes, confidenceThreshold);
            Debug.WriteLine($"[TATR] Raw parsed detections: {parsed.Count} (logits dims={string.Join(",", logits.Dimensions.ToArray())}, boxes dims={string.Join(",", predBoxes.Dimensions.ToArray())})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TATR] Inference failed: {ex.Message}");
            return null;
        }

        if (parsed.Count == 0)
        {
            Debug.WriteLine($"[TATR] ParseDetrOutput returned empty — all queries below threshold {confidenceThreshold}");
            return null;
        }

        // Breakdown by class for diagnostics.
        var classCounts = parsed.GroupBy(d => d.Class).ToDictionary(g => g.Key, g => g.Count());
        Debug.WriteLine($"[TATR] Class breakdown: {string.Join(", ", classCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");

        // Translate normalized crop-space coords ([0,1] × cropInputW × cropInputH) to
        // page-image pixel coords. Normalized boxes are relative to the resized crop,
        // which preserved the crop aspect ratio — so multiply by cropW/cropH and add
        // the crop origin.
        var pageSpace = new List<TableSubDetection>(parsed.Count);
        foreach (var d in parsed)
        {
            var px = clampedX + d.X * clampedW;
            var py = clampedY + d.Y * clampedH;
            var pw = d.Width * clampedW;
            var ph = d.Height * clampedH;
            pageSpace.Add(new TableSubDetection(d.Class, d.Confidence, px, py, pw, ph));
        }

        // Deduplicate rows and columns (DETR can emit near-identical queries).
        var rows = DeduplicateByIoU(
            pageSpace.Where(d => d.Class == TableElementClass.Row).ToList(),
            DuplicateIoUThreshold);
        var columns = DeduplicateByIoU(
            pageSpace.Where(d => d.Class == TableElementClass.Column).ToList(),
            DuplicateIoUThreshold);
        var spanning = pageSpace.Where(d => d.Class == TableElementClass.SpanningCell).ToList();

        if (rows.Count == 0 || columns.Count == 0) return null;

        var cells = BuildCellGrid(rows, columns, clampedX, clampedY, clampedW, clampedH);
        if (cells.Count == 0 || cells.Count > MaxCellsPerTable) return null;

        return new TableStructure
        {
            Rows = rows,
            Columns = columns,
            SpanningCells = spanning,
            Cells = cells,
        };
    }

    // ------------------------------------------------------------------
    // Static helpers — unit-testable without an InferenceSession
    // ------------------------------------------------------------------

    /// <summary>
    /// Crop a BGRA8 page buffer to the given table bbox, then letterbox-resize
    /// (aspect preserving) so the shortest edge is <see cref="ShortestEdge"/>
    /// (clamped so the longest edge does not exceed <see cref="LongestEdge"/>),
    /// and ImageNet-normalize into an RGB float tensor [1, 3, newH, newW].
    /// Returns the tensor plus the resized dimensions so the caller can un-map
    /// output bboxes.
    /// </summary>
    internal static (DenseTensor<float> Tensor, int NewW, int NewH) PreprocessCrop(
        byte[] pageBgra, int pageWidth, int pageHeight,
        int cropX, int cropY, int cropW, int cropH)
    {
        // Aspect-preserving resize: scale so the SHORT side hits ShortestEdge,
        // then if the LONG side overshoots LongestEdge, scale down.
        var shortSide = Math.Min(cropW, cropH);
        var longSide = Math.Max(cropW, cropH);
        var scale = (double)ShortestEdge / shortSide;
        if (longSide * scale > LongestEdge)
            scale = (double)LongestEdge / longSide;
        var newW = Math.Max(1, (int)Math.Round(cropW * scale));
        var newH = Math.Max(1, (int)Math.Round(cropH * scale));

        var tensor = new DenseTensor<float>([1, 3, newH, newW]);

        // Direct span access is ~3-5x faster than the multidim indexer on this hot path.
        var span = tensor.Buffer.Span;
        var channelStride = newH * newW;
        var rBase = 0;
        var gBase = channelStride;
        var bBase = 2 * channelStride;

        var rInvStd = 1f / ImageStd[0];
        var gInvStd = 1f / ImageStd[1];
        var bInvStd = 1f / ImageStd[2];
        var rMean = ImageMean[0];
        var gMean = ImageMean[1];
        var bMean = ImageMean[2];
        var invScale = 1.0 / scale;

        // Nearest-neighbor resample.
        for (var y = 0; y < newH; y++)
        {
            var srcY = cropY + (int)Math.Min(cropH - 1, y * invScale);
            if (srcY < 0) srcY = 0;
            if (srcY >= pageHeight) srcY = pageHeight - 1;
            var rowStart = srcY * pageWidth;
            var dstRowOffset = y * newW;

            for (var x = 0; x < newW; x++)
            {
                var srcX = cropX + (int)Math.Min(cropW - 1, x * invScale);
                if (srcX < 0) srcX = 0;
                if (srcX >= pageWidth) srcX = pageWidth - 1;

                var srcIdx = (rowStart + srcX) * 4;
                if (srcIdx + 2 >= pageBgra.Length) continue;

                var b = pageBgra[srcIdx] / 255f;
                var g = pageBgra[srcIdx + 1] / 255f;
                var r = pageBgra[srcIdx + 2] / 255f;

                var dst = dstRowOffset + x;
                span[rBase + dst] = (r - rMean) * rInvStd;
                span[gBase + dst] = (g - gMean) * gInvStd;
                span[bBase + dst] = (b - bMean) * bInvStd;
            }
        }

        return (tensor, newW, newH);
    }

    /// <summary>
    /// Parse DETR output tensors into filtered detections with normalized
    /// bboxes. The returned X/Y/W/H are in normalized <c>[0, 1]</c> space
    /// relative to the resized crop — the caller is responsible for scaling
    /// them back to page-image pixels.
    /// </summary>
    internal static List<TableSubDetection> ParseDetrOutput(
        Tensor<float> logits,
        Tensor<float> predBoxes,
        float confidenceThreshold)
    {
        var results = new List<TableSubDetection>();
        var logDims = logits.Dimensions;
        var boxDims = predBoxes.Dimensions;
        if (logDims.Length != 3 || boxDims.Length != 3) return results;

        var numQueries = logDims[1];
        var numClassesPlusOne = logDims[2];
        if (numQueries != boxDims[1] || boxDims[2] != 4) return results;

        var numClasses = numClassesPlusOne - 1; // last class is "no-object"

        for (var q = 0; q < numQueries; q++)
        {
            // Softmax over classes. For numerical stability subtract max.
            var maxLogit = float.MinValue;
            for (var c = 0; c < numClassesPlusOne; c++)
            {
                var v = logits[0, q, c];
                if (v > maxLogit) maxLogit = v;
            }

            double denom = 0;
            for (var c = 0; c < numClassesPlusOne; c++)
                denom += Math.Exp(logits[0, q, c] - maxLogit);

            // Find best class excluding no-object.
            var bestClass = -1;
            var bestScore = 0.0;
            for (var c = 0; c < numClasses; c++)
            {
                var p = Math.Exp(logits[0, q, c] - maxLogit) / denom;
                if (p > bestScore)
                {
                    bestScore = p;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || bestScore < confidenceThreshold) continue;

            // DETR boxes are (cx, cy, w, h) normalized to [0, 1].
            var cx = predBoxes[0, q, 0];
            var cy = predBoxes[0, q, 1];
            var w = predBoxes[0, q, 2];
            var h = predBoxes[0, q, 3];

            var x1 = cx - w / 2;
            var y1 = cy - h / 2;

            // Clamp to [0, 1].
            if (x1 < 0) { w += x1; x1 = 0; }
            if (y1 < 0) { h += y1; y1 = 0; }
            if (x1 + w > 1) w = 1 - x1;
            if (y1 + h > 1) h = 1 - y1;
            if (w <= 0 || h <= 0) continue;

            results.Add(new TableSubDetection(
                (TableElementClass)bestClass,
                (float)bestScore,
                x1, y1, w, h));
        }

        return results;
    }

    /// <summary>
    /// Remove near-duplicate detections of the same class (IoU > threshold
    /// keeps the higher-confidence one).
    /// </summary>
    internal static List<TableSubDetection> DeduplicateByIoU(
        List<TableSubDetection> items,
        float iouThreshold)
    {
        var sorted = items.OrderByDescending(d => d.Confidence).ToList();
        var keep = new bool[sorted.Count];
        Array.Fill(keep, true);

        for (var i = 0; i < sorted.Count; i++)
        {
            if (!keep[i]) continue;
            for (var j = i + 1; j < sorted.Count; j++)
            {
                if (!keep[j]) continue;
                if (ComputeIoU(sorted[i], sorted[j]) > iouThreshold)
                    keep[j] = false;
            }
        }

        var result = new List<TableSubDetection>();
        for (var i = 0; i < sorted.Count; i++)
            if (keep[i]) result.Add(sorted[i]);
        return result;
    }

    /// <summary>
    /// Build a cell grid by intersecting every row with every column.
    /// Rows are sorted top-to-bottom, columns left-to-right, so the resulting
    /// RowIndex/ColumnIndex reflect reading order.
    /// Cells outside the table bbox or smaller than <see cref="MinCellSidePx"/>
    /// are discarded.
    /// </summary>
    internal static List<TableCellBounds> BuildCellGrid(
        List<TableSubDetection> rows,
        List<TableSubDetection> columns,
        double tableX, double tableY, double tableW, double tableH)
    {
        var sortedRows = rows.OrderBy(r => r.Y).ToList();
        var sortedCols = columns.OrderBy(c => c.X).ToList();

        var cells = new List<TableCellBounds>(sortedRows.Count * sortedCols.Count);
        var tableRight = tableX + tableW;
        var tableBottom = tableY + tableH;

        for (var ri = 0; ri < sortedRows.Count; ri++)
        {
            var row = sortedRows[ri];
            for (var ci = 0; ci < sortedCols.Count; ci++)
            {
                var col = sortedCols[ci];

                // Intersection.
                var x1 = Math.Max(row.X, col.X);
                var y1 = Math.Max(row.Y, col.Y);
                var x2 = Math.Min(row.X + row.Width, col.X + col.Width);
                var y2 = Math.Min(row.Y + row.Height, col.Y + col.Height);

                // Clamp to table bbox.
                x1 = Math.Max(x1, tableX);
                y1 = Math.Max(y1, tableY);
                x2 = Math.Min(x2, tableRight);
                y2 = Math.Min(y2, tableBottom);

                var cellW = x2 - x1;
                var cellH = y2 - y1;
                if (cellW < MinCellSidePx || cellH < MinCellSidePx) continue;

                cells.Add(new TableCellBounds(ri, ci, x1, y1, cellW, cellH));
            }
        }

        return cells;
    }

    /// <summary>Compute IoU between two detections (page-image pixel coords).</summary>
    internal static float ComputeIoU(TableSubDetection a, TableSubDetection b) =>
        DocLayoutYoloService.ComputeRectIoU(a.X, a.Y, a.Width, a.Height, b.X, b.Y, b.Width, b.Height);

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
