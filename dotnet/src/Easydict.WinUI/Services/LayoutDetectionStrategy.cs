using System.Diagnostics;
using Easydict.TranslationService.LongDocument;
using UglyToad.PdfPig.Content;
using PdfPigPage = UglyToad.PdfPig.Content.Page;

namespace Easydict.WinUI.Services;

/// <summary>
/// Orchestrates layout detection across different strategies (heuristic, ONNX, Vision LLM).
/// Merges ML detection results with PdfPig text extraction.
/// </summary>
internal sealed class LayoutDetectionStrategy
{
    // Rendering target size — must match RenderPdfPageAsync in this file.
    private const int RenderTargetSize = 1024;

    /// <summary>IoU threshold for matching text blocks to ML-detected regions (legacy MergeDetections path).</summary>
    private const float IoUMatchThreshold = 0.3f;

    private readonly DocLayoutYoloService _onnxService;
    private readonly VisionLayoutDetectionService _visionService;
    private readonly LayoutModelDownloadService _downloadService;

    public LayoutDetectionStrategy(
        DocLayoutYoloService onnxService,
        VisionLayoutDetectionService visionService,
        LayoutModelDownloadService downloadService)
    {
        _onnxService = onnxService;
        _visionService = visionService;
        _downloadService = downloadService;
    }

    /// <summary>
    /// Detect layout regions and extract text blocks from a PDF page.
    /// Uses an ML-first, pixel-mask-style approach similar to pdf2zh:
    /// ML bounding boxes are the authoritative column boundaries; page words are
    /// assigned to the smallest enclosing ML region by their centre point, then
    /// grouped into paragraphs within each region.
    /// Returns an empty list when ML detection is unavailable — the caller
    /// (<see cref="LongDocumentTranslationService"/>) falls back to heuristic extraction.
    /// </summary>
    /// <param name="textPage">PdfPig page for text/word extraction.</param>
    /// <param name="pdfPath">Path to the PDF file (for page rendering).</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="mode">Layout detection mode.</param>
    /// <param name="visionEndpoint">Vision LLM endpoint (for VisionLLM mode).</param>
    /// <param name="visionApiKey">Vision LLM API key (for VisionLLM mode).</param>
    /// <param name="visionModel">Vision LLM model (for VisionLLM mode).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ML-driven source document blocks, or empty when ML is unavailable.</returns>
    public async Task<IReadOnlyList<EnhancedSourceBlock>> DetectAndExtractAsync(
        PdfPigPage textPage,
        string pdfPath,
        int pageIndex,
        LayoutDetectionMode mode,
        string? visionEndpoint = null,
        string? visionApiKey = null,
        string? visionModel = null,
        CancellationToken ct = default)
    {
        // Heuristic mode: skip ML entirely; caller uses ExtractLayoutBlocksFromPage.
        if (mode == LayoutDetectionMode.Heuristic)
            return [];

        // Try ML detection
        List<LayoutDetection>? mlDetections = null;
        LayoutRegionSource mlSource = LayoutRegionSource.Unknown;

        if (mode is LayoutDetectionMode.OnnxLocal or LayoutDetectionMode.Auto)
        {
            mlDetections = await TryOnnxDetectionAsync(pdfPath, pageIndex, ct);
            if (mlDetections is not null)
                mlSource = LayoutRegionSource.OnnxModel;
        }

        if (mlDetections is null && mode is LayoutDetectionMode.VisionLLM)
        {
            mlDetections = await TryVisionDetectionAsync(
                pdfPath, pageIndex, visionEndpoint, visionApiKey, visionModel, ct);
            if (mlDetections is not null)
                mlSource = LayoutRegionSource.VisionLLM;
        }

        if (mlDetections is null || mlDetections.Count == 0)
        {
            Debug.WriteLine($"[LayoutStrategy] ML detection unavailable for page {pageIndex + 1}, caller will use heuristic");
            return [];
        }

        // ML-driven: assign page words to ML regions by centre point, then group into blocks.
        return ExtractBlocksByMlRegions(textPage, mlDetections, mlSource);
    }

    /// <summary>
    /// Check whether the ONNX model is available for inference (without downloading).
    /// </summary>
    public bool IsOnnxReady => _downloadService.IsReady && _onnxService.IsInitialized;

    /// <summary>
    /// Check whether the ONNX model files are downloaded (even if not loaded yet).
    /// </summary>
    public bool IsOnnxDownloaded => _downloadService.IsReady;

    private async Task<List<LayoutDetection>?> TryOnnxDetectionAsync(
        string pdfPath, int pageIndex, CancellationToken ct)
    {
        try
        {
            if (!_onnxService.IsInitialized)
            {
                if (!_downloadService.IsReady)
                {
                    Debug.WriteLine("[LayoutStrategy] ONNX model not downloaded, skipping");
                    return null;
                }

                await _onnxService.InitializeAsync(ct: ct);
            }

            var (pixels, width, height) = await RenderPdfPageAsync(pdfPath, pageIndex, ct);
            var detections = _onnxService.Detect(pixels, width, height);
            Debug.WriteLine($"[LayoutStrategy] ONNX detected {detections.Count} regions on page {pageIndex + 1}");
            return detections;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LayoutStrategy] ONNX detection failed: {ex.Message}");
            return null;
        }
    }

    private async Task<List<LayoutDetection>?> TryVisionDetectionAsync(
        string pdfPath,
        int pageIndex,
        string? endpoint,
        string? apiKey,
        string? model,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            Debug.WriteLine("[LayoutStrategy] Vision LLM not configured, skipping");
            return null;
        }

        try
        {
            var (pixels, width, height) = await RenderPdfPageAsync(pdfPath, pageIndex, ct);
            var detections = await _visionService.DetectAsync(pixels, width, height, endpoint, apiKey, model, ct);
            Debug.WriteLine($"[LayoutStrategy] Vision LLM detected {detections.Count} regions on page {pageIndex + 1}");
            return detections;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LayoutStrategy] Vision LLM detection failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Merge ML detections with heuristic text blocks.
    /// ML detections provide region types; heuristic blocks provide text content.
    /// </summary>
    internal static IReadOnlyList<EnhancedSourceBlock> MergeDetections(
        List<HeuristicBlock> heuristicBlocks,
        List<LayoutDetection> mlDetections,
        LayoutRegionSource mlSource,
        PdfPigPage page)
    {
        var results = new List<EnhancedSourceBlock>();
        var pageHeight = (double)page.Height;

        foreach (var hb in heuristicBlocks)
        {
            var block = hb.Block;
            if (block.BoundingBox is null)
            {
                results.Add(new EnhancedSourceBlock(block, hb.RegionType, 1.0, LayoutRegionSource.Heuristic));
                continue;
            }

            var bbox = block.BoundingBox.Value;

            // Convert PdfPig coordinates (origin at bottom-left) to image coordinates (origin at top-left)
            var blockTop = pageHeight - (bbox.Y + bbox.Height);
            var blockLeft = bbox.X;
            var blockWidth = bbox.Width;
            var blockHeight = bbox.Height;

            // Find best matching ML detection by IoU
            var bestMatch = default(LayoutDetection?);
            var bestIoU = 0f;

            foreach (var det in mlDetections)
            {
                var iou = ComputeIoU(
                    blockLeft, blockTop, blockWidth, blockHeight,
                    det.X, det.Y, det.Width, det.Height);

                if (iou > bestIoU)
                {
                    bestIoU = iou;
                    bestMatch = det;
                }
            }

            if (bestMatch.HasValue && bestIoU >= IoUMatchThreshold)
            {
                // ML detection overrides heuristic region type
                var skipTranslation = bestMatch.Value.RegionType is
                    LayoutRegionType.Figure or
                    LayoutRegionType.Formula or
                    LayoutRegionType.IsolatedFormula;

                var enhancedBlock = block with
                {
                    IsFormulaLike = skipTranslation || block.IsFormulaLike
                };

                results.Add(new EnhancedSourceBlock(
                    enhancedBlock,
                    bestMatch.Value.RegionType,
                    bestMatch.Value.Confidence,
                    mlSource));
            }
            else
            {
                // No ML match, keep heuristic
                results.Add(new EnhancedSourceBlock(block, hb.RegionType, 1.0, LayoutRegionSource.Heuristic));
            }
        }

        return results;
    }

    private static float ComputeIoU(
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
    /// Render a PDF page to BGRA8 pixels using Windows.Data.Pdf API.
    /// </summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> RenderPdfPageAsync(
        string pdfPath, int pageIndex, CancellationToken ct)
    {
        const int targetSize = 1024;

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(pdfPath);
        var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        using var page = pdfDoc.GetPage((uint)pageIndex);

        // Calculate render dimensions preserving aspect ratio
        var pageWidth = page.Size.Width;
        var pageHeight = page.Size.Height;
        var scale = Math.Min(targetSize / pageWidth, targetSize / pageHeight);
        var renderWidth = (uint)Math.Round(pageWidth * scale);
        var renderHeight = (uint)Math.Round(pageHeight * scale);

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var options = new Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = renderWidth,
            DestinationHeight = renderHeight
        };

        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);

        // Decode the rendered image to BGRA8 pixels
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

        return (pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    // -----------------------------------------------------------------------
    // ML-driven word-to-region extraction (pdf2zh-style)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Region types that should exclude words from translation.
    /// Aligned with pdf2zh high_level.py vcls = ["abandon", "figure", "table", "isolate_formula", "formula_caption"].
    /// </summary>
    private static readonly HashSet<LayoutRegionType> ExcludeRegionTypes =
    [
        LayoutRegionType.Figure,
        LayoutRegionType.Formula,
        LayoutRegionType.IsolatedFormula
    ];

    /// <summary>
    /// Region types that contain translatable text.
    /// </summary>
    private static readonly HashSet<LayoutRegionType> TranslatableRegionTypes =
    [
        LayoutRegionType.Body,
        LayoutRegionType.Header,
        LayoutRegionType.Footer,
        LayoutRegionType.Title,
        LayoutRegionType.Caption,
        LayoutRegionType.LeftColumn,
        LayoutRegionType.RightColumn,
        LayoutRegionType.Table,
        LayoutRegionType.TableLike
    ];

    /// <summary>Minimum ML detection confidence to accept a region (pdf2zh uses 0.25; we use 0.3 slightly more conservative).</summary>
    private const float MinDetectionConfidence = 0.3f;

    /// <summary>
    /// Assign every horizontal word on the page to the smallest ML-detected region
    /// that contains its centre point, using a two-pass strategy aligned with
    /// pdf2zh high_level.py (line 128-157):
    ///   Pass 1: assign words to translatable regions.
    ///   Pass 2: remove words that fall inside exclude regions (Figure/Formula/IsolatedFormula).
    /// This ensures exclude regions always take priority over translatable ones.
    /// Words with no enclosing region are grouped heuristically as orphans.
    /// </summary>
    private static IReadOnlyList<EnhancedSourceBlock> ExtractBlocksByMlRegions(
        PdfPigPage page,
        List<LayoutDetection> mlDetections,
        LayoutRegionSource mlSource)
    {
        var pageWidthPdf = (double)page.Width;
        var pageHeightPdf = (double)page.Height;

        // Filter out low-confidence detections (aligned with pdf2zh confidence threshold).
        var filteredDetections = mlDetections
            .Where(d => d.Confidence >= MinDetectionConfidence)
            .ToList();

        Debug.WriteLine($"[LayoutStrategy] Confidence filter: {mlDetections.Count} → {filteredDetections.Count} detections (threshold={MinDetectionConfidence})");

        // Convert every ML detection box from rendered-image pixels (top-left origin)
        // to PDF point coordinates (bottom-left origin) for comparison with PdfPig words.
        var pdfRegions = filteredDetections
            .Select(det =>
            {
                var (rx, ry, rw, rh) = DetectionToPdfCoords(det, pageWidthPdf, pageHeightPdf);
                return (Det: det, PdfX: rx, PdfY: ry, PdfW: rw, PdfH: rh);
            })
            .ToList();

        // Collect all horizontal words (rotated words are handled by the heuristic fallback).
        var allWords = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text)
                     && w.TextOrientation == TextOrientation.Horizontal)
            .ToList();

        // ---- Two-pass word assignment (aligned with pdf2zh high_level.py) ----

        // Pass 1: Assign words to translatable regions only.
        var wordsByRegion = new List<Word>[pdfRegions.Count];
        for (var i = 0; i < pdfRegions.Count; i++)
            wordsByRegion[i] = [];

        var orphanWords = new List<Word>();

        foreach (var word in allWords)
        {
            var box = word.BoundingBox;
            var cx = (box.Left + box.Right) / 2.0;
            var cy = (box.Bottom + box.Top) / 2.0;

            var bestIdx = -1;
            var bestArea = double.MaxValue;

            for (var i = 0; i < pdfRegions.Count; i++)
            {
                var r = pdfRegions[i];
                // Only consider translatable regions in Pass 1
                if (!TranslatableRegionTypes.Contains(r.Det.RegionType))
                    continue;

                if (cx >= r.PdfX && cx <= r.PdfX + r.PdfW &&
                    cy >= r.PdfY && cy <= r.PdfY + r.PdfH)
                {
                    var area = r.PdfW * r.PdfH;
                    if (area < bestArea)
                    {
                        bestArea = area;
                        bestIdx = i;
                    }
                }
            }

            if (bestIdx >= 0)
                wordsByRegion[bestIdx].Add(word);
            else
                orphanWords.Add(word);
        }

        // Pass 2: Remove words that fall inside exclude regions (Figure/Formula/IsolatedFormula).
        // These regions take priority — words inside them should NOT be translated.
        var excludedWordCount = 0;
        foreach (var word in allWords)
        {
            var box = word.BoundingBox;
            var cx = (box.Left + box.Right) / 2.0;
            var cy = (box.Bottom + box.Top) / 2.0;

            for (var i = 0; i < pdfRegions.Count; i++)
            {
                var r = pdfRegions[i];
                if (!ExcludeRegionTypes.Contains(r.Det.RegionType))
                    continue;

                if (cx >= r.PdfX && cx <= r.PdfX + r.PdfW &&
                    cy >= r.PdfY && cy <= r.PdfY + r.PdfH)
                {
                    // Remove this word from whichever translatable region it was assigned to
                    for (var j = 0; j < wordsByRegion.Length; j++)
                    {
                        if (wordsByRegion[j].Remove(word))
                        {
                            excludedWordCount++;
                            break;
                        }
                    }

                    // Also remove from orphans
                    orphanWords.Remove(word);
                    break;
                }
            }
        }

        if (excludedWordCount > 0)
            Debug.WriteLine($"[LayoutStrategy] Pass 2: excluded {excludedWordCount} words in Figure/Formula regions");

        var results = new List<EnhancedSourceBlock>();
        var blockIndex = 0;

        // Process each ML region in visual reading order (top-to-bottom, left-to-right).
        var sortedRegionIndices = Enumerable.Range(0, pdfRegions.Count)
            .OrderByDescending(i => pdfRegions[i].PdfY + pdfRegions[i].PdfH) // top of region
            .ThenBy(i => pdfRegions[i].PdfX)
            .ToList();

        foreach (var i in sortedRegionIndices)
        {
            if (wordsByRegion[i].Count == 0)
                continue;

            var (det, _, _, _, _) = pdfRegions[i];

            // Figure regions: skip entirely — do not generate blocks.
            // Aligned with pdf2zh which skips figure regions from translation output.
            if (det.RegionType is LayoutRegionType.Figure)
                continue;

            var regionTag = RegionTypeToTag(det.RegionType);

            // Formula and isolated formula regions: mark blocks as formula-like
            // so the translation pipeline skips them.
            var skipTranslation = det.RegionType is
                LayoutRegionType.Formula or
                LayoutRegionType.IsolatedFormula;
            var isTable = det.RegionType is LayoutRegionType.Table;

            foreach (var block in LongDocumentTranslationService.GroupWordsIntoBlocks(
                wordsByRegion[i], page, page.Number, regionTag, ref blockIndex))
            {
                var finalBlock = skipTranslation ? block with { IsFormulaLike = true }
                               : isTable ? block with { BlockType = SourceBlockType.TableCell }
                               : block;

                results.Add(new EnhancedSourceBlock(finalBlock, det.RegionType, det.Confidence, mlSource));
            }
        }

        // Orphan words: use simple heuristic grouping so no text is lost.
        if (orphanWords.Count > 0)
        {
            foreach (var block in LongDocumentTranslationService.GroupWordsIntoBlocks(
                orphanWords, page, page.Number, "body", ref blockIndex))
            {
                results.Add(new EnhancedSourceBlock(block, LayoutRegionType.Body, 0.5, LayoutRegionSource.Heuristic));
            }
        }

        Debug.WriteLine($"[LayoutStrategy] ML-driven extraction: {results.Count} blocks from {filteredDetections.Count} regions + {orphanWords.Count} orphan words on page {page.Number}");
        return results;
    }

    /// <summary>
    /// Converts a <see cref="LayoutDetection"/> bounding box from rendered-image pixel
    /// coordinates (top-left origin, pixels) to PDF point coordinates (bottom-left origin).
    /// The scale factor is derived from the same <c>min(1024/w, 1024/h)</c> formula used
    /// by <c>RenderPdfPageAsync</c> so the two coordinate spaces are always consistent.
    /// </summary>
    private static (double X, double Y, double Width, double Height) DetectionToPdfCoords(
        LayoutDetection det, double pageWidthPdf, double pageHeightPdf)
    {
        var scale = Math.Min(RenderTargetSize / pageWidthPdf, RenderTargetSize / pageHeightPdf);
        var x = det.X / scale;
        var y = pageHeightPdf - (det.Y + det.Height) / scale;  // flip to bottom-left origin
        var w = det.Width / scale;
        var h = det.Height / scale;
        return (Math.Max(0, x), Math.Max(0, y), w, h);
    }

    /// <summary>Maps a <see cref="LayoutRegionType"/> to the region tag embedded in BlockIds.</summary>
    private static string RegionTypeToTag(LayoutRegionType type) => type switch
    {
        LayoutRegionType.Header => "header",
        LayoutRegionType.Footer => "footer",
        LayoutRegionType.Title => "title",
        LayoutRegionType.Figure => "figure",
        LayoutRegionType.Formula
            or LayoutRegionType.IsolatedFormula => "formula",
        LayoutRegionType.Table
            or LayoutRegionType.TableLike => "table",
        LayoutRegionType.Caption => "caption",
        LayoutRegionType.LeftColumn => "left",
        LayoutRegionType.RightColumn => "right",
        _ => "body",
    };

    // -----------------------------------------------------------------------
    // Legacy helpers — kept for unit-test compatibility
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extract heuristic blocks directly from the static method results.
    /// Called by the integration code that has access to ExtractLayoutBlocksFromPage.
    /// </summary>
    internal static List<HeuristicBlock> ParseHeuristicBlocks(IEnumerable<SourceDocumentBlock> blocks)
    {
        return blocks.Select(b =>
        {
            var regionType = InferRegionTypeFromBlockId(b.BlockId);
            return new HeuristicBlock(b, regionType);
        }).ToList();
    }

    /// <summary>
    /// Infer LayoutRegionType from the semantic tag in a block ID.
    /// Block IDs follow the pattern: p{pageNum}-{regionTag}-b{blockNum}
    /// </summary>
    internal static LayoutRegionType InferRegionTypeFromBlockId(string blockId)
    {
        var parts = blockId.Split('-');
        if (parts.Length < 3) return LayoutRegionType.Body;

        return parts[1] switch
        {
            "header" => LayoutRegionType.Header,
            "footer" => LayoutRegionType.Footer,
            "left" => LayoutRegionType.LeftColumn,
            "right" => LayoutRegionType.RightColumn,
            "table" => LayoutRegionType.TableLike,
            "body" => LayoutRegionType.Body,
            _ => LayoutRegionType.Body
        };
    }
}

/// <summary>
/// A text block extracted by heuristic detection, with its inferred region type.
/// </summary>
internal sealed record HeuristicBlock(SourceDocumentBlock Block, LayoutRegionType RegionType);

/// <summary>
/// A source document block enhanced with ML or heuristic layout detection results.
/// </summary>
public sealed record EnhancedSourceBlock(
    SourceDocumentBlock Block,
    LayoutRegionType RegionType,
    double Confidence,
    LayoutRegionSource Source);
