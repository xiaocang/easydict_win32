using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.TextLayout;
using Easydict.TextLayout.FontFitting;
using Easydict.TextLayout.Preparation;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// PDF export service: migrated from LongDocumentTranslationService.
/// Handles coordinate backfill, structured PDF, and bilingual interleaved output.
/// </summary>
public sealed class PdfExportService : IDocumentExportService
{
    internal sealed record BackfillRenderingMetrics(
        int CandidateBlocks,
        int RenderedBlocks,
        int MissingBoundingBoxBlocks,
        int ShrinkFontBlocks,
        int TruncatedBlocks,
        int ObjectReplaceBlocks,
        int OverlayModeBlocks,
        int StructuredFallbackBlocks,
        IReadOnlyDictionary<int, BackfillPageMetrics>? PageMetrics,
        IReadOnlyList<BackfillBlockIssue>? BlockIssues)
    {
        public static BackfillRenderingMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, null, null);
    }

    internal sealed class PageBackfillAccumulator
    {
        public int CandidateBlocks { get; set; }
        public int RenderedBlocks { get; set; }
        public int MissingBoundingBoxBlocks { get; set; }
        public int ShrinkFontBlocks { get; set; }
        public int TruncatedBlocks { get; set; }
        public int ObjectReplaceBlocks { get; set; }
        public int OverlayModeBlocks { get; set; }
        public int StructuredFallbackBlocks { get; set; }
    }

    public IReadOnlyList<string> SupportedExtensions => [".pdf"];

    /// <summary>
    /// Language-specific line height multipliers for overlay rendering.
    /// CJK/complex scripts need more vertical space than Latin text.
    /// Aligned with pdf2zh converter.py:376-380 LANG_LINEHEIGHT_MAP.
    /// </summary>
    private static readonly Dictionary<Language, double> LineHeightMultipliers = new()
    {
        [Language.SimplifiedChinese] = 1.4,
        [Language.TraditionalChinese] = 1.4,
        [Language.Japanese] = 1.2,   // lowered from 1.4 (pdf2zh uses 1.1, compromise at 1.2)
        [Language.Korean] = 1.3,
        [Language.Arabic] = 1.0,     // pdf2zh: ar → 1.0
        [Language.Russian] = 1.0,    // pdf2zh: ru → 0.8, using 1.0 to avoid clipping
        [Language.Thai] = 1.3,       // tall ascenders/descenders need extra space
    };
    private static readonly Regex InlineScriptLatinWordRegex = new(@"[A-Za-z]{3,}", RegexOptions.Compiled);
    private static readonly Regex CitationLikeInlineScriptRegex = new(@"^\[\s*\d+(?:\s*,\s*\d+)*\s*\]$", RegexOptions.Compiled);

    public DocumentExportResult Export(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
        // Set up CJK font resolver if target language requires it
        EnsureCjkFontSetup(checkpoint.TargetLanguage);

        // 1. Always generate monolingual PDF first (existing backfill logic)
        var renderingMetrics = ExportPdfWithCoordinateBackfill(checkpoint, sourceFilePath, outputPath);
        var qualityMetrics = ToQualityMetrics(renderingMetrics);
        WriteBackfillIssuesSidecar(outputPath, renderingMetrics.BlockIssues);

        // 2. Handle bilingual mode
        string? bilingualPath = null;
        if (outputMode is DocumentOutputMode.Bilingual or DocumentOutputMode.Both)
        {
            bilingualPath = BuildBilingualOutputPath(outputPath);
            ExportBilingualPdf(sourceFilePath, outputPath, bilingualPath, checkpoint.PageRange);
            WriteBackfillIssuesSidecar(bilingualPath, renderingMetrics.BlockIssues);
        }

        PdfPageSelectionHelper.FilterPdfInPlace(outputPath, checkpoint.PageRange);

        // 3. Bilingual-only: delete intermediate monolingual file, return bilingual path
        if (outputMode == DocumentOutputMode.Bilingual && bilingualPath != null)
        {
            try { File.Delete(outputPath); } catch { /* best-effort cleanup */ }
            return new DocumentExportResult
            {
                OutputPath = bilingualPath,
                BilingualOutputPath = bilingualPath,
                BackfillMetrics = qualityMetrics
            };
        }

        // 4. Both or Monolingual
        return new DocumentExportResult
        {
            OutputPath = outputPath,
            BilingualOutputPath = bilingualPath,
            BackfillMetrics = qualityMetrics
        };
    }

    // --------------------------------------------------
    // Bilingual PDF export (new)
    // --------------------------------------------------

    /// <summary>
    /// Creates a bilingual PDF by interleaving pages from the source PDF and the translated PDF.
    /// Original page 1 → Translated page 1 → Original page 2 → Translated page 2 → ...
    /// </summary>
    internal static void ExportBilingualPdf(
        string sourcePdfPath,
        string translatedPdfPath,
        string bilingualOutputPath,
        string? pageRange = null)
    {
        var outputDirectory = Path.GetDirectoryName(bilingualOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var sourceDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
        using var translatedDoc = PdfReader.Open(translatedPdfPath, PdfDocumentOpenMode.Import);
        var bilingualDoc = new PdfDocument();

        var selectedPages = PdfPageSelectionHelper.ResolveSelectedPages(pageRange, sourceDoc.PageCount);
        if (selectedPages is null)
        {
            var maxPages = Math.Max(sourceDoc.PageCount, translatedDoc.PageCount);
            for (var i = 0; i < maxPages; i++)
            {
                if (i < sourceDoc.PageCount)
                {
                    bilingualDoc.AddPage(sourceDoc.Pages[i]);
                }

                if (i < translatedDoc.PageCount)
                {
                    bilingualDoc.AddPage(translatedDoc.Pages[i]);
                }
            }
        }
        else
        {
            foreach (var pageNumber in selectedPages)
            {
                var pageIndex = pageNumber - 1;
                if (pageIndex < sourceDoc.PageCount)
                {
                    bilingualDoc.AddPage(sourceDoc.Pages[pageIndex]);
                }

                if (pageIndex < translatedDoc.PageCount)
                {
                    bilingualDoc.AddPage(translatedDoc.Pages[pageIndex]);
                }
            }
        }

        // Copy bookmarks from source PDF, mapping page numbers for interleaved layout
        CopyBookmarksForBilingual(sourceDoc, bilingualDoc, selectedPages);

        bilingualDoc.Save(bilingualOutputPath);
    }

    /// <summary>
    /// Copies bookmarks from the source PDF to the bilingual PDF,
    /// adjusting page references for the interleaved layout (source page N → bilingual page 2N-1).
    /// </summary>
    internal static void CopyBookmarksForBilingual(
        PdfDocument sourceDoc,
        PdfDocument bilingualDoc,
        IReadOnlyList<int>? selectedPages = null)
    {
        try
        {
            if (sourceDoc.Outlines.Count == 0 || bilingualDoc.PageCount == 0)
                return;

            var pageIndexMap = BuildBilingualPageIndexMap(sourceDoc.PageCount, selectedPages);
            CopyOutlineLevel(sourceDoc.Outlines, bilingualDoc.Outlines, sourceDoc, bilingualDoc, pageIndexMap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfExport] Bookmark copy failed: {ex.Message}");
        }
    }

    private static void CopyOutlineLevel(
        PdfOutlineCollection sourceOutlines,
        PdfOutlineCollection targetOutlines,
        PdfDocument sourceDoc,
        PdfDocument bilingualDoc,
        IReadOnlyDictionary<int, int> pageIndexMap)
    {
        foreach (var sourceOutline in sourceOutlines)
        {
            try
            {
                var sourcePageIndex = FindOutlinePageIndex(sourceOutline, sourceDoc);
                if (sourcePageIndex < 0 || !pageIndexMap.TryGetValue(sourcePageIndex, out var bilingualPageIndex))
                {
                    // Skip bookmarks outside the selected page subset, but keep descending into children.
                    if (sourceOutline.Outlines.Count > 0)
                        CopyOutlineLevel(sourceOutline.Outlines, targetOutlines, sourceDoc, bilingualDoc, pageIndexMap);
                    continue;
                }

                // Map source page index to bilingual page index: source page i → bilingual page 2*i
                if (bilingualPageIndex >= bilingualDoc.PageCount)
                    bilingualPageIndex = bilingualDoc.PageCount - 1;

                var targetOutline = targetOutlines.Add(sourceOutline.Title, bilingualDoc.Pages[bilingualPageIndex]);

                // Recurse into children
                if (sourceOutline.Outlines.Count > 0)
                    CopyOutlineLevel(sourceOutline.Outlines, targetOutline.Outlines, sourceDoc, bilingualDoc, pageIndexMap);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PdfExport] Failed to copy bookmark '{sourceOutline.Title}': {ex.Message}");
            }
        }
    }

    private static IReadOnlyDictionary<int, int> BuildBilingualPageIndexMap(int totalPages, IReadOnlyList<int>? selectedPages)
    {
        if (selectedPages is null)
        {
            return Enumerable.Range(0, totalPages)
                .ToDictionary(pageIndex => pageIndex, pageIndex => pageIndex * 2);
        }

        return selectedPages
            .Select((pageNumber, selectedIndex) => new { pageNumber, selectedIndex })
            .ToDictionary(
                entry => entry.pageNumber - 1,
                entry => entry.selectedIndex * 2);
    }

    /// <summary>
    /// Finds the 0-based page index for a bookmark outline entry.
    /// Returns -1 if the page reference cannot be resolved.
    /// </summary>
    private static int FindOutlinePageIndex(PdfOutline outline, PdfDocument doc)
    {
        try
        {
            // PdfSharpCore's PdfOutline.DestinationPage returns the PdfPage
            var destPage = outline.DestinationPage;
            if (destPage == null)
                return -1;

            for (var i = 0; i < doc.PageCount; i++)
            {
                if (ReferenceEquals(doc.Pages[i], destPage))
                    return i;
            }
        }
        catch
        {
            // Some bookmarks may reference invalid pages
        }

        return -1;
    }

    /// <summary>
    /// Derives the bilingual output path from the monolingual output path.
    /// e.g., "output/doc-translated.pdf" → "output/doc-translated-bilingual.pdf"
    /// </summary>
    internal static string BuildBilingualOutputPath(string monolingualOutputPath)
    {
        var directory = Path.GetDirectoryName(monolingualOutputPath) ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(monolingualOutputPath);
        var extension = Path.GetExtension(monolingualOutputPath);
        return Path.Combine(directory, $"{nameWithoutExt}-bilingual{extension}");
    }

    // --------------------------------------------------
    // PDF coordinate backfill export (migrated)
    // --------------------------------------------------

    /// <summary>
    /// Data collected during the pre-processing pass for a single overlay block.
    /// Used to separate the white-background pass from the text-drawing pass.
    /// </summary>
    private sealed class OverlayBlockInfo
    {
        public required int ChunkIndex { get; init; }
        public required string TranslatedText { get; init; }
        public required LongDocumentChunkMetadata Metadata { get; init; }
        public bool UsesSourceFallback { get; init; }
        public required XRect Rect { get; init; }
        public required double Padding { get; init; }
        public IReadOnlyList<XRect>? BackgroundLineRects { get; init; }
        public IReadOnlyList<XRect>? RenderLineRects { get; init; }
        public IReadOnlyList<XRect> ProtectedInlineRects { get; init; } = [];
        /// <summary>
        /// True when the source text was hidden in the PDF content stream via "3 Tr" (invisible
        /// rendering mode), so no white-rectangle erasure is needed in Pass 1.
        /// </summary>
        public bool SourceHidden { get; init; }
    }

    internal static BackfillRenderingMetrics ExportPdfWithCoordinateBackfill(LongDocumentTranslationCheckpoint checkpoint, string sourcePdfPath, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var targetLanguage = checkpoint.TargetLanguage;
        var lineHeight = GetLineHeight(targetLanguage);
        var isCjkTarget = targetLanguage is Language.SimplifiedChinese or Language.TraditionalChinese
            or Language.Japanese or Language.Korean;
        using var doc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Modify);
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        var candidateBlocks = 0;
        var renderedBlocks = 0;
        var missingBoundingBoxBlocks = 0;
        var shrinkFontBlocks = 0;
        var truncatedBlocks = 0;
        var objectReplaceBlocks = 0;
        var overlayModeBlocks = 0;
        var pageMetrics = new Dictionary<int, PageBackfillAccumulator>();
        var blockIssues = new List<BackfillBlockIssue>();

        // Collect overlay blocks grouped by page for two-pass rendering
        var overlayBlocksByPage = new Dictionary<int, List<OverlayBlockInfo>>();
        var protectedFormulaRectsByPage = new Dictionary<int, List<XRect>>();

        foreach (var chunkIndex in Enumerable.Range(0, checkpoint.SourceChunks.Count))
        {
            var metadata = metadataByChunkIndex[chunkIndex];
            var sourceText = checkpoint.SourceChunks[chunkIndex];
            CollectProtectedFormulaRect(protectedFormulaRectsByPage, doc, metadata);
            if (!PdfExportCheckpointTextResolver.TryGetRenderableText(
                    checkpoint,
                    chunkIndex,
                    out var translated,
                    out var usesSourceFallback))
            {
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "missing-translation"
                });
                continue;
            }

            translated = NormalizeTranslationForOverlay(sourceText, translated);
            if (metadata.SourceBlockType == SourceBlockType.Formula || metadata.IsFormulaLike)
            {
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "skipped-formula",
                    Detail = $"SourceBlockType={metadata.SourceBlockType}, IsFormulaLike={metadata.IsFormulaLike}"
                });
                continue;
            }

            candidateBlocks++;
            var perPage = GetOrCreatePageBackfill(pageMetrics, metadata.PageNumber);
            perPage.CandidateBlocks++;

            if (metadata.BoundingBox is null)
            {
                missingBoundingBoxBlocks++;
                perPage.MissingBoundingBoxBlocks++;
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "missing-bbox"
                });
                continue;
            }

            var pageIndex = metadata.PageNumber - 1;
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
            {
                missingBoundingBoxBlocks++;
                perPage.MissingBoundingBoxBlocks++;
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "page-out-of-range",
                    Detail = $"Page {metadata.PageNumber} out of range (document has {doc.Pages.Count} pages)"
                });
                continue;
            }

            var page = doc.Pages[pageIndex];
            if (TryReplacePdfTextObject(page, sourceText, translated))
            {
                renderedBlocks++;
                objectReplaceBlocks++;
                perPage.RenderedBlocks++;
                perPage.ObjectReplaceBlocks++;
                if (usesSourceFallback)
                {
                    blockIssues.Add(new BackfillBlockIssue
                    {
                        ChunkIndex = chunkIndex,
                        SourceBlockId = metadata.SourceBlockId,
                        PageNumber = metadata.PageNumber,
                        Kind = "rendered-source-fallback"
                    });
                }
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "object-replaced"
                });
                continue;
            }

            // If the block is rotated (vertical sidebar text), keep the original text rather than
            // attempting overlay redraw. Overlay for rotated blocks tends to collide with nearby content.
            var rotationAngle = metadata.TextStyle?.RotationAngle ?? 0;
            if (Math.Abs(rotationAngle) > 0.01)
            {
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "skipped-rotated",
                    Detail = $"RotationAngle={rotationAngle:F2}"
                });
                continue;
            }

            // Explicit table blocks remain protected to avoid corrupting true tabular layouts.
            if (metadata.SourceBlockType == SourceBlockType.TableCell ||
                metadata.RegionType == LayoutRegionType.Table)
            {
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "skipped-table-like-unsafe",
                    Detail = $"RegionType={metadata.RegionType}, SourceBlockType={metadata.SourceBlockType}"
                });
                continue;
            }

            if (metadata.RegionType == LayoutRegionType.TableLike &&
                !IsSafeTableLikeCell(metadata, page, sourceText))
            {
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "skipped-table-like-unsafe",
                    Detail = $"RegionType=TableLike, RotationAngle={rotationAngle:F2}, Box={metadata.BoundingBox}, SourceText='{sourceText}'"
                });
                continue;
            }

            var box = metadata.BoundingBox.Value;
            var rect = ToPageRect(page, box, minSize: 10);

            // When available, use per-line positions to build narrower per-line rectangles.
            // This reduces accidental erasure/drawing across adjacent columns and helps keep layout stable.
            var lineRects = TryBuildLineRects(page.Height.Point, rect, metadata.TextStyle, lineHeight);
            lineRects = ExpandLineRectsForCell(
                lineRects,
                rect,
                lineHeight,
                metadata.RegionType == LayoutRegionType.TableLike || rect.Width <= page.Width.Point * 0.55);
            var (translatedText, renderLineRects, backgroundLineRects, protectedInlineRects) =
                HandleInlineScriptLinesForOverlay(sourceText, translated, lineRects);
            if (lineRects is { Count: > 0 } && renderLineRects is { Count: 0 })
            {
                // All lines are inline scripts — fall through to block-level rendering
                // instead of skipping entirely. Clear lineRects so rendering uses block rect.
                renderLineRects = null;
                backgroundLineRects = null;
                protectedInlineRects = [];
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = chunkIndex,
                    SourceBlockId = metadata.SourceBlockId,
                    PageNumber = metadata.PageNumber,
                    Kind = "inline-script-fallback"
                });
            }

            // Scale padding with font size: larger fonts need more padding to cover descenders
            var fontSize = metadata.TextStyle?.FontSize > 0 ? metadata.TextStyle.FontSize : 11.0;
            var pad = Math.Clamp(fontSize * 0.25, 2.5, 10);
            if (isCjkTarget)
            {
                pad = Math.Max(pad, Math.Clamp(fontSize * 0.30, 3, 12));
            }

            if (!overlayBlocksByPage.TryGetValue(pageIndex, out var pageBlocks))
            {
                pageBlocks = new List<OverlayBlockInfo>();
                overlayBlocksByPage[pageIndex] = pageBlocks;
            }

            // Prefer hiding the source text via "3 Tr" (invisible rendering mode) in the content
            // stream rather than drawing a white rectangle over it. This preserves table borders,
            // column dividers, and other adjacent graphics that a white box would erase.
            var sourceHidden = TryHideSourceTextInStream(page, sourceText);

            pageBlocks.Add(new OverlayBlockInfo
            {
                ChunkIndex = chunkIndex,
                TranslatedText = translatedText,
                Metadata = metadata,
                UsesSourceFallback = usesSourceFallback,
                Rect = rect,
                Padding = pad,
                BackgroundLineRects = backgroundLineRects,
                RenderLineRects = renderLineRects,
                ProtectedInlineRects = protectedInlineRects,
                SourceHidden = sourceHidden
            });
        }

        // Two-pass rendering: for each page, draw all white backgrounds first, then all text
        foreach (var (pageIndex, blocks) in overlayBlocksByPage)
        {
            var page = doc.Pages[pageIndex];

            try
            {
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                // Pass 1: Draw all white background rectangles.
                // Blocks where TryHideSourceTextInStream succeeded (SourceHidden=true) skip this
                // entirely — the source text is already invisible via "3 Tr" in the content stream,
                // so no white-box erasure is needed (and would only damage adjacent graphics).
                foreach (var block in blocks)
                {
                    if (block.SourceHidden)
                        continue;

                    var clipRect = ExpandOverlayClipRect(block.Rect, block.Padding, page);
                    var formulaRects = protectedFormulaRectsByPage.TryGetValue(pageIndex, out var protectedRects)
                        ? protectedRects
                        : null;
                    var clipState = gfx.Save();
                    try
                    {
                        IntersectClipWithProtectionHoles(gfx, clipRect, block.Padding, page, block.ProtectedInlineRects, formulaRects, block.Rect);

                        // Prefer per-letter glyph rectangles when available (formula blocks with
                        // math-font character data). Per-letter erasure targets only the ink area of
                        // each glyph, leaving the gaps between characters — where table rules and
                        // column dividers typically run — untouched.
                        var letterRects = BuildPerLetterEraseRects(block.Metadata, page.Height.Point);
                        var backgroundLineRects = block.BackgroundLineRects ?? block.RenderLineRects;
                        if (letterRects is { Count: > 0 })
                        {
                            foreach (var r in letterRects)
                            {
                                gfx.DrawRectangle(XBrushes.White,
                                    new XRect(
                                        r.X - block.Padding * 0.5,
                                        r.Y - block.Padding,
                                        r.Width + block.Padding,
                                        r.Height + block.Padding * 2));
                            }
                        }
                        else if (backgroundLineRects is { Count: > 0 })
                        {
                            foreach (var r in backgroundLineRects)
                            {
                                gfx.DrawRectangle(XBrushes.White,
                                    new XRect(
                                        r.X - block.Padding,
                                        r.Y - block.Padding,
                                        r.Width + block.Padding * 2,
                                        r.Height + block.Padding * 2));
                            }
                        }
                        else
                        {
                            gfx.DrawRectangle(XBrushes.White,
                                new XRect(
                                    block.Rect.X - block.Padding,
                                    block.Rect.Y - block.Padding,
                                    block.Rect.Width + block.Padding * 2,
                                    block.Rect.Height + block.Padding * 2));
                        }
                    }
                    finally
                    {
                        gfx.Restore(clipState);
                    }
                }

                // Pass 2: Draw all translated text
                foreach (var block in blocks)
                {
                    var metadata = block.Metadata;
                    var perPage = GetOrCreatePageBackfill(pageMetrics, metadata.PageNumber);
                    var rect = block.Rect;
                    var clipRect = ExpandOverlayClipRect(rect, block.Padding, page);
                    var formulaRects = protectedFormulaRectsByPage.TryGetValue(pageIndex, out var protectedRects)
                        ? protectedRects
                        : null;

                    var style = metadata.TextStyle;
                    // Rotated blocks are filtered out during collection; keep this as a safety net.
                    var rotationAngle = style?.RotationAngle ?? 0;
                    if (Math.Abs(rotationAngle) > 0.01)
                        continue;

                    var effectiveLineHeight = style?.LineSpacing > 0 ? style.LineSpacing : lineHeight;

                    // For CJK targets, ensure minimum line height based on font size
                    var baseFont = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike, targetLanguage, metadata.BoundingBox!.Value.Height, style);
                    if (isCjkTarget)
                    {
                        effectiveLineHeight = Math.Max(effectiveLineHeight, baseFont.Size * 1.4);
                    }

                    var font = block.RenderLineRects is { Count: > 0 }
                        ? FitFontToLineRects(gfx, block.TranslatedText, baseFont, block.RenderLineRects)
                        : FitFontToRect(gfx, block.TranslatedText, baseFont, rect.Width, rect.Height, effectiveLineHeight);
                    if (font.Size < baseFont.Size)
                    {
                        shrinkFontBlocks++;
                        perPage.ShrinkFontBlocks++;
                        blockIssues.Add(new BackfillBlockIssue
                        {
                            ChunkIndex = block.ChunkIndex,
                            SourceBlockId = metadata.SourceBlockId,
                            PageNumber = metadata.PageNumber,
                            Kind = "shrink-font",
                            Detail = $"Font shrunk from {baseFont.Size:F1}pt to {font.Size:F1}pt"
                        });
                    }

                    var wrappedLines = block.RenderLineRects is { Count: > 0 }
                        ? WrapTextByWidths(gfx, block.TranslatedText, font, block.RenderLineRects.Select(r => r.Width).ToList()).ToList()
                        : WrapTextByWidth(gfx, block.TranslatedText, font, rect.Width).ToList();

                    // Dynamic line height reduction — aligned with pdf2zh converter.py:512-515:
                    //   while (lidx+1)*size*lh > height and lh >= 1: lh -= 0.05
                    // When wrapped lines exceed available height, progressively shrink line spacing
                    // before falling back to truncation.
                    if (block.RenderLineRects is not { Count: > 0 })
                    {
                        var totalTextHeight = wrappedLines.Count * effectiveLineHeight;
                        while (totalTextHeight > rect.Height && effectiveLineHeight > font.Size)
                        {
                            effectiveLineHeight -= 0.05 * font.Size;
                            totalTextHeight = wrappedLines.Count * effectiveLineHeight;
                        }

                        // Ensure minimum line height of 1.0× font size
                        effectiveLineHeight = Math.Max(effectiveLineHeight, font.Size);
                    }

                    var maxVisibleLines = block.RenderLineRects is { Count: > 0 }
                        ? block.RenderLineRects.Count
                        : Math.Max(1, (int)Math.Floor(rect.Height / effectiveLineHeight));
                    // When the source was hidden via "3 Tr" there is no white box clipping the
                    // bbox below, so allow a couple of extra lines to flow into natural whitespace
                    // rather than hard-truncating — mirroring pdf2zh's natural overflow behaviour.
                    if (block.SourceHidden)
                        maxVisibleLines += 2;
                    var originalLineCount = wrappedLines.Count;
                    if (wrappedLines.Count > maxVisibleLines)
                    {
                        wrappedLines = wrappedLines.Take(maxVisibleLines).ToList();
                        var last = wrappedLines[^1];
                        wrappedLines[^1] = last.Length > 1 ? $"{last.TrimEnd('.', ' ')}..." : "...";
                        truncatedBlocks++;
                        perPage.TruncatedBlocks++;
                        blockIssues.Add(new BackfillBlockIssue
                        {
                            ChunkIndex = block.ChunkIndex,
                            SourceBlockId = metadata.SourceBlockId,
                            PageNumber = metadata.PageNumber,
                            Kind = "truncated",
                            Detail = $"Truncated from {originalLineCount} to {maxVisibleLines} lines"
                        });
                    }

                    var clipState = gfx.Save();
                    try
                    {
                        IntersectClipWithProtectionHoles(gfx, clipRect, block.Padding, page, block.ProtectedInlineRects, formulaRects, block.Rect);
                        var brush = CreateBrush(style);
                        var stringFormat = GetStringFormat(style);
                        var formulaContext = IsFormulaContext(metadata, block.TranslatedText);

                        if (block.RenderLineRects is { Count: > 0 })
                        {
                            for (var i = 0; i < wrappedLines.Count && i < block.RenderLineRects.Count; i++)
                            {
                                DrawStringMultiFont(gfx, wrappedLines[i], font, brush, block.RenderLineRects[i], stringFormat, formulaContext);
                            }
                        }
                        else
                        {
                            var lineY = rect.Y;
                            foreach (var line in wrappedLines)
                            {
                                DrawStringMultiFont(gfx, line, font, brush, new XRect(rect.X, lineY, rect.Width, effectiveLineHeight), stringFormat, formulaContext);
                                lineY += effectiveLineHeight;
                            }
                        }
                    }
                    finally
                    {
                        gfx.Restore(clipState);
                    }

                    renderedBlocks++;
                    overlayModeBlocks++;
                    perPage.RenderedBlocks++;
                    perPage.OverlayModeBlocks++;
                    if (block.UsesSourceFallback)
                    {
                        blockIssues.Add(new BackfillBlockIssue
                        {
                            ChunkIndex = block.ChunkIndex,
                            SourceBlockId = metadata.SourceBlockId,
                            PageNumber = metadata.PageNumber,
                            Kind = "rendered-source-fallback"
                        });
                    }
                    blockIssues.Add(new BackfillBlockIssue
                    {
                        ChunkIndex = block.ChunkIndex,
                        SourceBlockId = metadata.SourceBlockId,
                        PageNumber = metadata.PageNumber,
                        Kind = block.SourceHidden ? "invisible-hidden" : "overlay-rendered"
                    });
                }
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"[PdfExport] Skipping page {pageIndex + 1}: {ex.Message}");
                foreach (var block in blocks)
                {
                    var perPage = GetOrCreatePageBackfill(pageMetrics, block.Metadata.PageNumber);
                    missingBoundingBoxBlocks++;
                    perPage.MissingBoundingBoxBlocks++;
                    blockIssues.Add(new BackfillBlockIssue
                    {
                        ChunkIndex = block.ChunkIndex,
                        SourceBlockId = block.Metadata.SourceBlockId,
                        PageNumber = block.Metadata.PageNumber,
                        Kind = "missing-bbox",
                        Detail = $"Page graphics error: {ex.Message}"
                    });
                }
            }
        }

        doc.Save(outputPath);

        return new BackfillRenderingMetrics(
            candidateBlocks,
            renderedBlocks,
            missingBoundingBoxBlocks,
            shrinkFontBlocks,
            truncatedBlocks,
            objectReplaceBlocks,
            overlayModeBlocks,
            0,
            BuildPageBackfillMetrics(pageMetrics),
            blockIssues.Count > 0 ? blockIssues : null);
    }

    private static void WriteBackfillIssuesSidecar(string outputPath, IReadOnlyList<BackfillBlockIssue>? blockIssues)
    {
        try
        {
            var issueKinds = new HashSet<string>(StringComparer.Ordinal)
            {
                "missing-translation",
                "rendered-source-fallback",
                "skipped-formula",
                "skipped-rotated",
                "skipped-table-like-unsafe",
                "missing-bbox",
                "page-out-of-range",
                "truncated",
                "shrink-font",
                "invisible-hidden"   // source text hidden via "3 Tr" in content stream (no white-box erasure)
            };

            var issueList = (blockIssues ?? Array.Empty<BackfillBlockIssue>())
                .Where(i => issueKinds.Contains(i.Kind))
                .ToList();

            var json = JsonSerializer.Serialize(issueList, new JsonSerializerOptions { WriteIndented = true });

            // Keep both filenames for backward/forward compatibility.
            // Users may look for either suffix in the output directory.
            var singular = $"{outputPath}.backfill_issue.json";
            var plural = $"{outputPath}.backfill_issues.json";
            File.WriteAllText(singular, json, Encoding.UTF8);
            File.WriteAllText(plural, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfExport] Failed to write backfill issues sidecar: {ex.Message}");
        }
    }

    private static bool IsSafeTableLikeCell(LongDocumentChunkMetadata metadata, PdfPage page, string sourceText)
    {
        if (metadata.BoundingBox is null)
        {
            return false;
        }

        var box = metadata.BoundingBox.Value;
        if (box.Height > page.Height.Point * 0.25)
        {
            return false;
        }

        var rotationAngle = metadata.TextStyle?.RotationAngle ?? 0;
        if (Math.Abs(rotationAngle) > 0.01)
        {
            return false;
        }

        if (!HasTranslatableText(sourceText))
        {
            return false;
        }

        // Avoid drawing over true tables: even if a block is misclassified as TableLike, tabular text should remain protected.
        if (LooksTabularText(sourceText))
        {
            return false;
        }

        // Allow wide header/footer sentences which are sometimes misclassified as TableLike.
        var top = box.Y + box.Height;
        var isHeaderFooterBand = top >= page.Height.Point * 0.85 || box.Y <= page.Height.Point * 0.15;
        var maxWidthRatio = isHeaderFooterBand ? 0.95 : 0.55;
        if (box.Width > page.Width.Point * maxWidthRatio)
        {
            return false;
        }

        return true;
    }

    private static bool HasTranslatableText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Any(ch => char.IsLetter(ch) || IsCjkCharacter(ch));
    }

    private static bool LooksTabularText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains('\t', StringComparison.Ordinal) ||
            text.Contains('|', StringComparison.Ordinal) ||
            text.Contains("  ", StringComparison.Ordinal))
        {
            return true;
        }

        if (Regex.IsMatch(text, @"\b\d+(\.\d+)?\b\s+\b\d+(\.\d+)?\b"))
        {
            return true;
        }

        var digits = 0;
        var letters = 0;
        var spaces = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) digits++;
            else if (char.IsLetter(ch)) letters++;
            else if (ch == ' ') spaces++;
        }

        return spaces >= 2 && digits >= 6 && digits > letters * 2;
    }

    private static string NormalizeTranslationForOverlay(string sourceText, string translatedText)
    {
        if (string.IsNullOrEmpty(translatedText))
        {
            return translatedText;
        }

        if (sourceText.Contains('*', StringComparison.Ordinal))
        {
            var normalized = translatedText
                .Replace('＊', '*')
                .Replace('∗', '*')
                .Replace('﹡', '*');

            if (!normalized.Contains('*', StringComparison.Ordinal) && normalized.Contains('□', StringComparison.Ordinal))
            {
                normalized = normalized.Replace('□', '*');
            }

            return normalized;
        }

        return translatedText;
    }

    private static void CollectProtectedFormulaRect(
        Dictionary<int, List<XRect>> protectedFormulaRectsByPage,
        PdfDocument doc,
        LongDocumentChunkMetadata metadata)
    {
        if (metadata.BoundingBox is null)
        {
            return;
        }

        if (metadata.SourceBlockType != SourceBlockType.Formula && !metadata.IsFormulaLike)
        {
            return;
        }

        var pageIndex = metadata.PageNumber - 1;
        if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
        {
            return;
        }

        var page = doc.Pages[pageIndex];
        var formulaRect = ToPageRect(page, metadata.BoundingBox.Value, minSize: 1);
        if (formulaRect.Width < 1 || formulaRect.Height < 1)
        {
            return;
        }

        if (!protectedFormulaRectsByPage.TryGetValue(pageIndex, out var pageRects))
        {
            pageRects = [];
            protectedFormulaRectsByPage[pageIndex] = pageRects;
        }

        pageRects.Add(formulaRect);
    }

    internal static XRect ToPageRect(PdfPage page, BlockRect box, double minSize)
    {
        var drawX = Math.Max(0, box.X);
        var drawY = Math.Max(0, page.Height.Point - (box.Y + box.Height));
        var drawWidth = Math.Max(minSize, box.Width);
        var drawHeight = Math.Max(minSize, box.Height);
        return new XRect(drawX, drawY, drawWidth, drawHeight);
    }

    internal static (IReadOnlyList<XRect>? RenderLineRects, IReadOnlyList<XRect> ProtectedInlineRects) SplitLineRectsForInlineScriptProtection(
        string sourceText,
        IReadOnlyList<XRect>? lineRects)
    {
        if (lineRects is null || lineRects.Count == 0)
        {
            return (lineRects, []);
        }

        var sourceLines = sourceText.Replace("\r\n", "\n").Split('\n');
        if (sourceLines.Length < 2)
        {
            return (lineRects, []);
        }

        var pairedCount = Math.Min(sourceLines.Length, lineRects.Count);
        if (pairedCount == 0)
        {
            return (lineRects, []);
        }

        var sortedHeights = lineRects
            .Take(pairedCount)
            .Select(rect => Math.Max(1, rect.Height))
            .OrderBy(height => height)
            .ToList();
        var medianRectHeight = sortedHeights[sortedHeights.Count / 2];

        var protectedIndexes = new HashSet<int>();
        for (var i = 0; i < pairedCount; i++)
        {
            var rect = lineRects[i];
            if (!LooksLikeInlineScriptLine(sourceLines[i]))
            {
                continue;
            }

            if (rect.Height <= medianRectHeight * 0.75)
            {
                protectedIndexes.Add(i);
            }
        }

        if (protectedIndexes.Count == 0)
        {
            return (lineRects, []);
        }

        var renderLineRects = new List<XRect>(lineRects.Count - protectedIndexes.Count);
        var protectedInlineRects = new List<XRect>(protectedIndexes.Count);
        for (var i = 0; i < lineRects.Count; i++)
        {
            var rect = lineRects[i];
            if (protectedIndexes.Contains(i))
            {
                protectedInlineRects.Add(rect);
            }
            else
            {
                renderLineRects.Add(rect);
            }
        }

        return (renderLineRects, protectedInlineRects);
    }

    internal static (string TranslatedText, IReadOnlyList<XRect>? RenderLineRects, IReadOnlyList<XRect>? BackgroundLineRects, IReadOnlyList<XRect> ProtectedInlineRects)
        HandleInlineScriptLinesForOverlay(
            string sourceText,
            string translatedText,
            IReadOnlyList<XRect>? lineRects)
    {
        if (lineRects is null || lineRects.Count == 0)
        {
            return (translatedText, lineRects, lineRects, []);
        }

        var (renderLineRects, detectedProtectedRects) = SplitLineRectsForInlineScriptProtection(sourceText, lineRects);
        if (detectedProtectedRects.Count == 0)
        {
            return (translatedText, renderLineRects, renderLineRects, []);
        }

        var normalizedTranslation = NormalizeTranslationForInlineScriptLines(translatedText);

        var protectedRectSet = new HashSet<XRect>(detectedProtectedRects);
        var scriptLineIndices = new HashSet<int>();
        for (var i = 0; i < lineRects.Count; i++)
        {
            if (protectedRectSet.Contains(lineRects[i]))
            {
                scriptLineIndices.Add(i);
            }
        }

        if (scriptLineIndices.Count == 0)
        {
            return (normalizedTranslation, renderLineRects, renderLineRects, []);
        }

        var sourceLines = sourceText.Replace("\r\n", "\n").Split('\n');
        var protectedIndices = new HashSet<int>(scriptLineIndices);
        foreach (var scriptLineIndex in scriptLineIndices.OrderBy(i => i))
        {
            if (scriptLineIndex < 0 || scriptLineIndex >= sourceLines.Length)
            {
                continue;
            }

            var scriptText = sourceLines[scriptLineIndex].Trim();
            if (scriptText.Length == 0)
            {
                continue;
            }

            if (IsCitationLikeInlineScript(scriptText))
            {
                if (ContainsInlineScriptFragment(normalizedTranslation, scriptText))
                {
                    protectedIndices.Remove(scriptLineIndex);
                }
                continue;
            }

            if (!IsProbablySubscriptLine(lineRects, scriptLineIndex, scriptLineIndices))
            {
                continue;
            }

            if (!TryBuildInlineSubscriptAttachmentsForScriptLine(sourceLines, scriptLineIndices, scriptLineIndex, out var attachments))
            {
                continue;
            }

            if (TryApplyInlineSubscriptAttachments(normalizedTranslation, attachments, out var augmented))
            {
                normalizedTranslation = augmented;
                protectedIndices.Remove(scriptLineIndex);
            }
        }

        var backgroundLineRects = new List<XRect>(lineRects.Count);
        var protectedInlineRects = new List<XRect>();
        for (var i = 0; i < lineRects.Count; i++)
        {
            if (protectedIndices.Contains(i))
            {
                protectedInlineRects.Add(lineRects[i]);
            }
            else
            {
                backgroundLineRects.Add(lineRects[i]);
            }
        }

        return (normalizedTranslation, renderLineRects, backgroundLineRects, protectedInlineRects);
    }

    internal static string NormalizeTranslationForInlineScriptLines(string translatedText)
    {
        if (string.IsNullOrEmpty(translatedText))
        {
            return translatedText;
        }

        var lines = translatedText.Replace("\r\n", "\n").Split('\n');
        var normalized = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (!LooksLikeInlineScriptLine(line))
            {
                normalized.Add(line);
                continue;
            }

            if (IsCitationLikeInlineScript(line))
            {
                var trimmed = line.Trim();
                if (normalized.Count > 0)
                {
                    normalized[^1] = normalized[^1] + trimmed;
                }
                else
                {
                    normalized.Add(trimmed);
                }
            }
        }

        return string.Join("\n", normalized);
    }

    internal static bool IsCitationLikeInlineScript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return CitationLikeInlineScriptRegex.IsMatch(text.Trim());
    }

    private static bool ContainsInlineScriptFragment(string text, string fragment)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(fragment))
        {
            return false;
        }

        var normalizedText = NormalizeInlineScriptSearchText(text);
        var normalizedFragment = NormalizeInlineScriptSearchText(fragment);
        return normalizedText.Contains(normalizedFragment, StringComparison.Ordinal);
    }

    private static string NormalizeInlineScriptSearchText(string text)
    {
        var noWhitespace = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return noWhitespace
            .Replace('［', '[')
            .Replace('］', ']')
            .Replace('，', ',');
    }

    private static bool IsProbablySubscriptLine(IReadOnlyList<XRect> lineRects, int scriptLineIndex, HashSet<int> scriptLineIndices)
    {
        if (scriptLineIndex < 0 || scriptLineIndex >= lineRects.Count)
        {
            return false;
        }

        var scriptRect = lineRects[scriptLineIndex];
        var scriptCenterY = scriptRect.Y + scriptRect.Height / 2;

        int? baseIndex = null;
        for (var i = scriptLineIndex - 1; i >= 0; i--)
        {
            if (scriptLineIndices.Contains(i))
            {
                continue;
            }

            baseIndex = i;
            break;
        }

        if (baseIndex == null)
        {
            for (var i = scriptLineIndex + 1; i < lineRects.Count; i++)
            {
                if (scriptLineIndices.Contains(i))
                {
                    continue;
                }

                baseIndex = i;
                break;
            }
        }

        if (baseIndex == null)
        {
            return false;
        }

        var baseRect = lineRects[baseIndex.Value];
        var baseCenterY = baseRect.Y + baseRect.Height / 2;
        return scriptCenterY > baseCenterY + 0.5;
    }

    private static bool TryBuildInlineSubscriptAttachmentsForScriptLine(
        IReadOnlyList<string> sourceLines,
        HashSet<int> scriptLineIndices,
        int scriptLineIndex,
        out List<(char BaseChar, string Subscript)> attachments)
    {
        attachments = [];

        if (scriptLineIndex < 0 || scriptLineIndex >= sourceLines.Count)
        {
            return false;
        }

        var scriptText = sourceLines[scriptLineIndex];
        var tokens = SplitInlineScriptTokens(scriptText);
        if (tokens.Count == 0)
        {
            return false;
        }

        var previousIndex = FindPreviousNonScriptLineIndex(sourceLines, scriptLineIndices, scriptLineIndex);
        if (previousIndex < 0)
        {
            return false;
        }

        var previousLine = sourceLines[previousIndex];
        if (!TryInferBaseCharForInlineScript(previousLine, tokens.Count, out var baseChar))
        {
            return false;
        }

        attachments = new List<(char BaseChar, string Subscript)>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!TryConvertToUnicodeSubscript(token, out var subscript))
            {
                attachments = [];
                return false;
            }
            attachments.Add((baseChar, subscript));
        }

        return attachments.Count > 0;
    }

    private static int FindPreviousNonScriptLineIndex(IReadOnlyList<string> sourceLines, HashSet<int> scriptLineIndices, int scriptLineIndex)
    {
        for (var i = scriptLineIndex - 1; i >= 0; i--)
        {
            if (scriptLineIndices.Contains(i))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(sourceLines[i]))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitInlineScriptTokens(string scriptText)
    {
        if (string.IsNullOrWhiteSpace(scriptText))
        {
            return [];
        }

        var trimmed = scriptText.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var parts = trimmed.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        }

        var tokens = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var token = part.Trim().TrimEnd(',', ';', '.', ':');
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static bool TryInferBaseCharForInlineScript(string previousLine, int tokenCount, out char baseChar)
    {
        baseChar = default;

        if (tokenCount <= 0)
        {
            return false;
        }

        if (tokenCount == 1 && TryExtractTrailingIsolatedAsciiSymbol(previousLine, out baseChar))
        {
            return true;
        }

        var isolatedSymbols = ExtractIsolatedAsciiSymbols(previousLine);
        if (isolatedSymbols.Count == 0)
        {
            return false;
        }

        if (tokenCount > 1)
        {
            var candidates = isolatedSymbols
                .GroupBy(ch => ch)
                .Where(g => g.Count() >= tokenCount)
                .Select(g => g.Key)
                .ToList();

            if (candidates.Count == 1)
            {
                baseChar = candidates[0];
                return true;
            }

            return false;
        }

        var unique = isolatedSymbols.Distinct().ToList();
        if (unique.Count == 1)
        {
            baseChar = unique[0];
            return true;
        }

        return false;
    }

    private static bool TryExtractTrailingIsolatedAsciiSymbol(string line, out char symbol)
    {
        symbol = default;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimEnd();
        var end = trimmed.Length - 1;
        while (end >= 0 && IsTrailingScriptPunctuation(trimmed[end]))
        {
            end--;
        }

        if (end < 0)
        {
            return false;
        }

        var ch = trimmed[end];
        if (!IsAsciiLetterOrDigit(ch))
        {
            return false;
        }

        if (end > 0 && IsAsciiLetterOrDigit(trimmed[end - 1]))
        {
            return false;
        }

        symbol = ch;
        return true;
    }

    private static bool IsTrailingScriptPunctuation(char ch)
    {
        return ch is ',' or '.' or ';' or ':' or ')' or ']' or '}' or '>' or '?' or '!' or '，' or '。' or '；' or '：' or '）' or '】' or '」' or '』';
    }

    private static List<char> ExtractIsolatedAsciiSymbols(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return [];
        }

        var result = new List<char>();
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (!IsAsciiLetterOrDigit(ch))
            {
                continue;
            }

            if (i > 0 && IsAsciiLetterOrDigit(line[i - 1]))
            {
                continue;
            }

            if (i + 1 < line.Length && IsAsciiLetterOrDigit(line[i + 1]))
            {
                continue;
            }

            result.Add(ch);
        }

        return result;
    }

    private static bool IsAsciiLetterOrDigit(char ch)
    {
        return ch is >= '0' and <= '9' ||
               ch is >= 'A' and <= 'Z' ||
               ch is >= 'a' and <= 'z';
    }

    internal static bool TryConvertToUnicodeSubscript(string token, out string subscript)
    {
        subscript = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (!TryMapToUnicodeSubscript(ch, out var mapped))
            {
                subscript = string.Empty;
                return false;
            }

            builder.Append(mapped);
        }

        subscript = builder.ToString();
        return subscript.Length > 0;
    }

    private static bool TryMapToUnicodeSubscript(char ch, out char mapped)
    {
        mapped = ch;

        if (ch is >= '0' and <= '9')
        {
            mapped = (char)('₀' + (ch - '0'));
            return true;
        }

        mapped = char.ToLowerInvariant(ch) switch
        {
            '+' => '₊',
            '-' => '₋',
            '\u2212' => '₋',
            '=' => '₌',
            '(' => '₍',
            ')' => '₎',
            'a' => 'ₐ',
            'e' => 'ₑ',
            'h' => 'ₕ',
            'i' => 'ᵢ',
            'j' => 'ⱼ',
            'k' => 'ₖ',
            'l' => 'ₗ',
            'm' => 'ₘ',
            'n' => 'ₙ',
            'o' => 'ₒ',
            'p' => 'ₚ',
            'r' => 'ᵣ',
            's' => 'ₛ',
            't' => 'ₜ',
            'u' => 'ᵤ',
            'v' => 'ᵥ',
            'x' => 'ₓ',
            _ => '\0'
        };

        return mapped != '\0';
    }

    internal static bool TryApplyInlineSubscriptAttachments(
        string translatedText,
        IReadOnlyList<(char BaseChar, string Subscript)> attachments,
        out string augmentedText)
    {
        augmentedText = translatedText;

        if (string.IsNullOrEmpty(translatedText) || attachments.Count == 0)
        {
            return false;
        }

        var cursor = 0;
        var builder = new StringBuilder(translatedText.Length + attachments.Count * 3);

        foreach (var (baseChar, subscript) in attachments)
        {
            if (!TryFindNextEligibleBaseOccurrence(translatedText, baseChar, cursor, out var matchIndex))
            {
                augmentedText = translatedText;
                return false;
            }

            builder.Append(translatedText.AsSpan(cursor, matchIndex - cursor));
            builder.Append(baseChar);
            builder.Append(subscript);
            cursor = matchIndex + 1;
        }

        builder.Append(translatedText.AsSpan(cursor));
        augmentedText = builder.ToString();
        return true;
    }

    private static bool TryFindNextEligibleBaseOccurrence(string text, char baseChar, int startIndex, out int matchIndex)
    {
        matchIndex = -1;

        if (startIndex < 0)
        {
            startIndex = 0;
        }

        for (var i = startIndex; i < text.Length; i++)
        {
            if (text[i] != baseChar)
            {
                continue;
            }

            if (i > 0 && IsAsciiLetterOrDigit(text[i - 1]))
            {
                continue;
            }

            if (i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (IsAsciiLetterOrDigit(next))
                {
                    continue;
                }

                if (next is '_' or '^' || IsUnicodeSubscriptChar(next))
                {
                    continue;
                }
            }

            matchIndex = i;
            return true;
        }

        return false;
    }

    private static bool IsUnicodeSubscriptChar(char ch)
    {
        return (ch is >= '\u2080' and <= '\u209F') ||
               ch is '\u1D62' or '\u1D63' or '\u1D64' or '\u1D65' or '\u2C7C';
    }

    internal static bool LooksLikeInlineScriptLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length is < 1 or > 24)
        {
            return false;
        }

        if (trimmed.Any(IsCjkCharacter))
        {
            return false;
        }

        if (InlineScriptLatinWordRegex.IsMatch(trimmed))
        {
            return false;
        }

        if (!trimmed.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        var hasDigits = trimmed.Any(char.IsDigit);
        var hasSymbols = trimmed.Any(IsInlineScriptSymbol);
        return hasDigits || hasSymbols || trimmed.Length <= 2;
    }

    private static bool IsInlineScriptSymbol(char ch)
    {
        return ch switch
        {
            '[' or ']' or '(' or ')' or '{' or '}' or '=' or '+' or '-' or '_' or '^' or '*' or '/' => true,
            '\u2212' => true,
            _ => false
        };
    }

    /// <summary>
    /// Determines whether a formula protection hole should be applied when rendering a text block.
    /// Returns false when the formula hole has ANY intersection with the text block rect.
    /// Standalone display equations don't spatially overlap with text blocks, so any intersection
    /// means the formula is inline or the bounding box is imprecise — the translated text
    /// should cover that area.
    /// </summary>
    internal static bool ShouldApplyFormulaHole(XRect formulaHole, XRect textBlockRect)
    {
        var interLeft = Math.Max(formulaHole.X, textBlockRect.X);
        var interTop = Math.Max(formulaHole.Y, textBlockRect.Y);
        var interRight = Math.Min(formulaHole.Right, textBlockRect.Right);
        var interBottom = Math.Min(formulaHole.Bottom, textBlockRect.Bottom);

        // No intersection → apply the hole (formula is external to this block)
        // Any intersection → skip the hole (formula overlaps with this text block)
        return interRight <= interLeft || interBottom <= interTop;
    }

    private static void IntersectClipWithProtectionHoles(
        XGraphics gfx,
        XRect clipRect,
        double padding,
        PdfPage page,
        IReadOnlyList<XRect>? protectedInlineRects,
        IReadOnlyList<XRect>? protectedFormulaRects,
        XRect? textBlockRect = null)
    {
        var holes = new List<XRect>();
        if (protectedInlineRects is { Count: > 0 })
        {
            holes.AddRange(protectedInlineRects.Where(rect => RectsIntersect(rect, clipRect)));
        }

        if (protectedFormulaRects is { Count: > 0 })
        {
            foreach (var rect in protectedFormulaRects)
            {
                if (!RectsIntersect(rect, clipRect))
                    continue;

                // When we know the text block rect, skip formula holes that overlap significantly
                if (textBlockRect.HasValue && !ShouldApplyFormulaHole(rect, textBlockRect.Value))
                    continue;

                holes.Add(rect);
            }
        }

        if (holes.Count == 0)
        {
            gfx.IntersectClip(clipRect);
            return;
        }

        var holePad = Math.Clamp(padding * 0.6, 1.5, 4.0);
        var path = new XGraphicsPath { FillMode = XFillMode.Alternate };
        path.AddRectangle(clipRect);

        foreach (var hole in holes)
        {
            var expanded = InflateAndClampRect(hole, holePad, page);
            if (!RectsIntersect(expanded, clipRect))
            {
                continue;
            }

            path.AddRectangle(expanded);
        }

        gfx.IntersectClip(path);
    }

    private static XRect InflateAndClampRect(XRect rect, double padding, PdfPage page)
    {
        var pageWidth = page.Width.Point;
        var pageHeight = page.Height.Point;
        var left = Math.Max(0, rect.X - padding);
        var top = Math.Max(0, rect.Y - padding);
        var right = Math.Min(pageWidth, rect.Right + padding);
        var bottom = Math.Min(pageHeight, rect.Bottom + padding);
        return new XRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool RectsIntersect(XRect left, XRect right)
    {
        return left.X < right.Right &&
               left.Right > right.X &&
               left.Y < right.Bottom &&
               left.Bottom > right.Y;
    }

    private static XRect ExpandOverlayClipRect(XRect rect, double padding, PdfPage page)
    {
        // Tight bounding boxes from PDF extraction can clip ascenders/descenders.
        // Expand the clip rectangle slightly (mostly vertically) to avoid truncated glyphs,
        // while keeping horizontal expansion tiny to minimize cross-column risk.
        var expandX = Math.Min(2.0, Math.Max(0, padding * 0.4));
        var expandY = Math.Min(8.0, Math.Max(2.0, padding * 0.9));

        var pageWidth = page.Width.Point;
        var pageHeight = page.Height.Point;

        var x = Math.Max(0, rect.X - expandX);
        var y = Math.Max(0, rect.Y - expandY);
        var right = Math.Min(pageWidth, rect.Right + expandX);
        var bottom = Math.Min(pageHeight, rect.Bottom + expandY);

        var w = Math.Max(1, right - x);
        var h = Math.Max(1, bottom - y);
        return new XRect(x, y, w, h);
    }

    private static IReadOnlyList<XRect>? ExpandLineRectsForCell(
        IReadOnlyList<XRect>? lineRects,
        XRect blockRect,
        double effectiveLineHeight,
        bool isCellLikeRegion)
    {
        if (!isCellLikeRegion || lineRects is null || lineRects.Count == 0)
        {
            return lineRects;
        }

        var maxLines = Math.Min(6, (int)Math.Floor(blockRect.Height / Math.Max(1, effectiveLineHeight)));
        if (maxLines <= lineRects.Count || maxLines <= 1)
        {
            return lineRects;
        }

        var lineHeight = blockRect.Height / maxLines;
        if (lineHeight < 3)
        {
            return lineRects;
        }

        var expanded = new List<XRect>(maxLines);
        for (var i = 0; i < maxLines; i++)
        {
            expanded.Add(new XRect(blockRect.X, blockRect.Y + i * lineHeight, blockRect.Width, lineHeight));
        }

        return expanded;
    }

    // --------------------------------------------------
    // Structured PDF export (migrated)
    // --------------------------------------------------

    internal static BackfillRenderingMetrics ExportStructuredPdf(LongDocumentTranslationCheckpoint checkpoint, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var targetLanguage = checkpoint.TargetLanguage;
        var lineHeight = GetLineHeight(targetLanguage, 16d);
        var doc = new PdfDocument();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);
        var groupedChunks = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .GroupBy(index => metadataByChunkIndex[index].PageNumber);

        foreach (var pageGroup in groupedChunks)
        {
            var page = doc.AddPage();
            var gfx = XGraphics.FromPdfPage(page);

            try
            {
                const int margin = 40;
                var y = (double)margin;
                var width = page.Width - margin * 2;

                var headingFont = PickFont(SourceBlockType.Heading, false, targetLanguage);
                gfx.DrawString($"Page {pageGroup.Key}", headingFont, XBrushes.Black, new XRect(margin, y, width, 24), XStringFormats.TopLeft);
                y += 24;

                foreach (var chunkIndex in pageGroup)
                {
                    var metadata = metadataByChunkIndex[chunkIndex];
                    var content = PdfExportCheckpointTextResolver.TryGetRenderableText(
                            checkpoint,
                            chunkIndex,
                            out var translated,
                            out _)
                        ? translated
                        : $"[Chunk {chunkIndex + 1} translation failed. Retry required.]";

                    var font = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike, targetLanguage);
                    foreach (var line in WrapText(content, 95))
                    {
                        if (y > page.Height - margin)
                        {
                            page = doc.AddPage();
                            var nextGraphics = XGraphics.FromPdfPage(page);
                            gfx.Dispose();
                            gfx = nextGraphics;
                            y = margin;
                        }

                        gfx.DrawString(line, font, XBrushes.Black, new XRect(margin, y, width, 20), XStringFormats.TopLeft);
                        y += lineHeight;
                    }

                    y += 8;
                }
            }
            finally
            {
                gfx.Dispose();
            }
        }

        doc.Save(outputPath);

        var structuredPageMetrics = groupedChunks.ToDictionary(
            group => group.Key,
            group => new BackfillPageMetrics
            {
                CandidateBlocks = 0,
                RenderedBlocks = 0,
                MissingBoundingBoxBlocks = 0,
                ShrinkFontBlocks = 0,
                TruncatedBlocks = 0,
                ObjectReplaceBlocks = 0,
                OverlayModeBlocks = 0,
                StructuredFallbackBlocks = group.Count()
            });

        return new BackfillRenderingMetrics(0, 0, 0, 0, 0, 0, 0, checkpoint.SourceChunks.Count, structuredPageMetrics, null);
    }

    // --------------------------------------------------
    // PDF object-level text replacement (migrated)
    // --------------------------------------------------

    internal static bool TryReplacePdfTextObject(PdfPage page, string sourceText, string translatedText)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translatedText))
        {
            return false;
        }

        if (!IsAscii(sourceText) || !IsAscii(translatedText))
        {
            return false;
        }

        try
        {
            var createSingleContent = page.Contents.GetType().GetMethod("CreateSingleContent");
            if (createSingleContent is null)
            {
                return false;
            }

            var contentStream = createSingleContent.Invoke(page.Contents, null);
            if (contentStream is null)
            {
                return false;
            }

            var streamProperty = contentStream.GetType().GetProperty("Stream");
            var streamValue = streamProperty?.GetValue(contentStream);
            if (streamValue is null)
            {
                return false;
            }

            var valueProperty = streamValue.GetType().GetProperty("Value");
            var raw = valueProperty?.GetValue(streamValue) as byte[];
            if (raw is null || raw.Length == 0)
            {
                return false;
            }

            var content = Encoding.ASCII.GetString(raw);
            if (!TryPatchPdfLiteralToken(content, sourceText, translatedText, out var patched))
            {
                return false;
            }

            valueProperty?.SetValue(streamValue, Encoding.ASCII.GetBytes(patched));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfExport] TryReplacePdfTextObject failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finds the Tj/TJ operator for <paramref name="sourceText"/> in the page content stream and
    /// wraps it with "3 Tr … 0 Tr" to make the source text invisible without erasing any adjacent
    /// graphics (table borders, column dividers, images, etc.).
    /// <para>
    /// Unlike <see cref="TryReplacePdfTextObject"/>, this method works for any source text whose
    /// bytes are representable in Latin-1 (ISO 8859-1), including all ASCII text. The translated
    /// text is written by the normal XGraphics overlay path — no white rectangle is needed.
    /// </para>
    /// Returns <c>true</c> if the operator was found and hidden; <c>false</c> if it could not be
    /// located (caller should fall back to the white-rectangle overlay path).
    /// </summary>
    internal static bool TryHideSourceTextInStream(PdfPage page, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            return false;

        try
        {
            var createSingleContent = page.Contents.GetType().GetMethod("CreateSingleContent");
            if (createSingleContent is null)
                return false;

            var contentStream = createSingleContent.Invoke(page.Contents, null);
            if (contentStream is null)
                return false;

            var streamProperty = contentStream.GetType().GetProperty("Stream");
            var streamValue = streamProperty?.GetValue(contentStream);
            if (streamValue is null)
                return false;

            var valueProperty = streamValue.GetType().GetProperty("Value");
            var raw = valueProperty?.GetValue(streamValue) as byte[];
            if (raw is null || raw.Length == 0)
                return false;

            // Decode as Latin-1 (ISO 8859-1). Every byte value 0x00-0xFF maps to the same Unicode
            // code point, so binary content round-trips without corruption, and ASCII text is
            // identical to Latin-1 in the 0x00-0x7F range.
            var content = Encoding.Latin1.GetString(raw);
            var (start, end) = FindTextOperatorRange(content, sourceText);
            if (start < 0)
                return false;

            // Inject "3 Tr" before the text-show operator and restore "0 Tr" immediately after.
            // PDF text rendering mode 3 = neither fill nor stroke (invisible), 0 = fill (normal).
            var modified = content[..start] + "3 Tr " + content[start..end] + " 0 Tr" + content[end..];
            valueProperty?.SetValue(streamValue, Encoding.Latin1.GetBytes(modified));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfExport] TryHideSourceTextInStream failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Locates the PDF text-show operator (Tj or TJ) that renders <paramref name="sourceText"/>
    /// in the content stream <paramref name="content"/> (decoded as Latin-1).
    /// </summary>
    /// <returns>
    /// <c>(start, end)</c> character indices of the complete operator span
    /// (from the opening <c>(</c> or <c>[</c> to just past <c>j</c>/<c>J</c>), or
    /// <c>(-1, -1)</c> if not found.
    /// </returns>
    internal static (int Start, int End) FindTextOperatorRange(string content, string sourceText)
    {
        // --- Literal Tj form: (escapedSource) Tj ---
        var escapedSource = EscapePdfLiteralString(sourceText);
        var sourceToken = $"({escapedSource})";
        var idx = content.IndexOf(sourceToken, StringComparison.Ordinal);
        if (idx >= 0)
        {
            // Scan past optional whitespace to confirm "Tj" follows.
            var pos = idx + sourceToken.Length;
            while (pos < content.Length && content[pos] is ' ' or '\t' or '\r' or '\n')
                pos++;
            if (pos + 2 <= content.Length && content[pos] == 'T' && content[pos + 1] == 'j')
            {
                var afterOp = pos + 2;
                // Guard: Tj must be a complete token (not "Tj0" or similar).
                if (afterOp >= content.Length || !char.IsLetterOrDigit(content[afterOp]))
                    return (idx, afterOp);
            }
            // If Tj doesn't follow, fall through to the TJ-array search below.
        }

        // --- TJ array form: [(body)] TJ ---
        var normalizedSource = NormalizePdfTextForMatch(sourceText);
        if (string.IsNullOrWhiteSpace(normalizedSource))
            return (-1, -1);

        foreach (Match match in Regex.Matches(content, @"\[(?<body>.*?)\]\s*TJ", RegexOptions.Singleline))
        {
            var bodyGroup = match.Groups["body"];
            if (!bodyGroup.Success)
                continue;

            var extracted = ExtractPdfLiteralStrings(bodyGroup.Value);
            if (extracted.Count == 0)
                continue;

            var combined = string.Concat(extracted.Select(item => item.Value));
            if (string.Equals(NormalizePdfTextForMatch(combined), normalizedSource, StringComparison.Ordinal))
                return (match.Index, match.Index + match.Length);
        }

        return (-1, -1);
    }

    /// <summary>
    /// Builds a list of per-letter glyph rectangles in XGraphics coordinates (Y-down) from
    /// <see cref="LongDocumentChunkMetadata.FormulaCharacters"/>, which stores PDF-coordinate
    /// (Y-up) glyph positions extracted at analysis time.
    /// Returns <c>null</c> when no character data is available (most non-formula blocks).
    /// </summary>
    internal static IReadOnlyList<XRect>? BuildPerLetterEraseRects(LongDocumentChunkMetadata metadata, double pageHeight)
    {
        var characters = metadata.FormulaCharacters?.Characters;
        if (characters is null || characters.Count == 0)
            return null;

        var rects = new List<XRect>(characters.Count);
        foreach (var glyph in characters)
        {
            if (glyph.GlyphWidth <= 0 || glyph.GlyphHeight <= 0)
                continue;

            // Flip Y from PDF space (origin = bottom-left, Y increases upward)
            // to XGraphics space (origin = top-left, Y increases downward).
            // GlyphBottom is the lower edge of the glyph in PDF coordinates, so
            // GlyphBottom + GlyphHeight is the upper edge — which becomes the top in screen space.
            rects.Add(new XRect(
                Math.Max(0, glyph.GlyphLeft),
                Math.Max(0, pageHeight - (glyph.GlyphBottom + glyph.GlyphHeight)),
                glyph.GlyphWidth,
                glyph.GlyphHeight));
        }

        return rects.Count > 0 ? rects : null;
    }

    internal static bool TryPatchPdfLiteralToken(string content, string sourceText, string translatedText, out string patched)
    {
        patched = content;

        var escapedSource = EscapePdfLiteralString(sourceText);
        var sourceToken = $"({escapedSource})";
        var idx = content.IndexOf(sourceToken, StringComparison.Ordinal);
        if (idx >= 0)
        {
            if (translatedText.Length > sourceText.Length)
            {
                return false;
            }

            var padded = translatedText.PadRight(sourceText.Length);
            var escapedTranslated = EscapePdfLiteralString(padded);
            var targetToken = $"({escapedTranslated})";

            patched = content.Remove(idx, sourceToken.Length).Insert(idx, targetToken);
            return true;
        }

        if (!TryPatchPdfArrayTextToken(content, sourceText, translatedText, out patched))
        {
            return false;
        }

        return true;
    }

    internal static bool TryPatchPdfArrayTextToken(string content, string sourceText, string translatedText, out string patched)
    {
        patched = content;
        var normalizedSource = NormalizePdfTextForMatch(sourceText);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return false;
        }

        foreach (Match match in Regex.Matches(content, @"\[(?<body>.*?)\]\s*TJ", RegexOptions.Singleline))
        {
            var bodyGroup = match.Groups["body"];
            if (!bodyGroup.Success)
            {
                continue;
            }

            var extracted = ExtractPdfLiteralStrings(bodyGroup.Value);
            if (extracted.Count == 0)
            {
                continue;
            }

            var combined = string.Concat(extracted.Select(item => item.Value));
            if (!string.Equals(NormalizePdfTextForMatch(combined), normalizedSource, StringComparison.Ordinal))
            {
                continue;
            }

            var escapedTranslated = EscapePdfLiteralString(translatedText);
            var replacement = $"({escapedTranslated}) Tj";
            patched = content.Remove(match.Index, match.Length).Insert(match.Index, replacement);
            return true;
        }

        return false;
    }

    internal static List<(int Start, int Length, string Value)> ExtractPdfLiteralStrings(string content)
    {
        var items = new List<(int Start, int Length, string Value)>();

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '(')
            {
                continue;
            }

            var (length, value) = ParsePdfLiteralString(content, i);
            if (length <= 0)
            {
                continue;
            }

            items.Add((i, length, value));
            i += length - 1;
        }

        return items;
    }

    internal static (int Length, string Value) ParsePdfLiteralString(string content, int startIndex)
    {
        var builder = new StringBuilder();
        var nesting = 0;
        var escaped = false;

        for (var index = startIndex; index < content.Length; index++)
        {
            var current = content[index];

            if (index == startIndex)
            {
                nesting = 1;
                continue;
            }

            if (escaped)
            {
                builder.Append(current);
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '(')
            {
                nesting++;
                builder.Append(current);
                continue;
            }

            if (current == ')')
            {
                nesting--;
                if (nesting == 0)
                {
                    return (index - startIndex + 1, builder.ToString());
                }

                builder.Append(current);
                continue;
            }

            builder.Append(current);
        }

        return (0, string.Empty);
    }

    internal static string NormalizePdfTextForMatch(string text)
    {
        return string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    }

    internal static string EscapePdfLiteralString(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }

    internal static bool IsAscii(string text)
    {
        return text.All(c => c <= 0x7F);
    }

    // --------------------------------------------------
    // Font and text rendering helpers (migrated)
    // --------------------------------------------------

    internal static XFont PickFont(SourceBlockType sourceBlockType, bool isFormulaLike,
        Language? targetLanguage = null, double? boxHeight = null, BlockTextStyle? textStyle = null)
    {
        if (sourceBlockType == SourceBlockType.Formula || isFormulaLike)
        {
            return new XFont("Consolas", 11, XFontStyle.Italic);
        }

        var fontFamily = ResolveFontFamily(targetLanguage);

        // Use extracted font size when available, otherwise estimate from bounding box height.
        double fontSize;
        if (textStyle?.FontSize > 0)
        {
            fontSize = Math.Clamp(textStyle.FontSize, 6, 28);
        }
        else if (boxHeight.HasValue)
        {
            // For single-line blocks, box height ≈ font size × 1.3 (line spacing).
            fontSize = Math.Clamp(boxHeight.Value / 1.3, 6, 28);
        }
        else
        {
            fontSize = sourceBlockType == SourceBlockType.Heading ? 14.0 : 11.0;
        }

        // Use extracted bold/italic when available, otherwise infer from block type.
        XFontStyle style;
        if (textStyle != null)
        {
            style = (textStyle.IsBold, textStyle.IsItalic) switch
            {
                (true, true) => XFontStyle.BoldItalic,
                (true, false) => XFontStyle.Bold,
                (false, true) => XFontStyle.Italic,
                _ => XFontStyle.Regular
            };
        }
        else
        {
            style = sourceBlockType == SourceBlockType.Heading ? XFontStyle.Bold : XFontStyle.Regular;
        }

        return new XFont(fontFamily, fontSize, style);
    }

    /// <summary>
    /// Creates an XBrush from the extracted text color, falling back to black.
    /// </summary>
    internal static XBrush CreateBrush(BlockTextStyle? style)
    {
        if (style == null || style.IsBlack)
            return XBrushes.Black;

        return new XSolidBrush(XColor.FromArgb(style.ColorR, style.ColorG, style.ColorB));
    }

    /// <summary>
    /// Returns the XStringFormat matching the extracted text alignment, falling back to TopLeft.
    /// </summary>
    internal static XStringFormat GetStringFormat(BlockTextStyle? style)
    {
        if (style == null)
            return XStringFormats.TopLeft;

        return style.Alignment switch
        {
            Easydict.TranslationService.LongDocument.TextAlignment.Center => XStringFormats.TopCenter,
            Easydict.TranslationService.LongDocument.TextAlignment.Right => new XStringFormat
            {
                Alignment = XStringAlignment.Far,
                LineAlignment = XLineAlignment.Near
            },
            _ => XStringFormats.TopLeft
        };
    }

    /// <summary>
    /// Resolves the best font family name for the given target language.
    /// Uses CJK-specific Noto Sans fonts when available, falls back to Arial.
    /// </summary>
    internal static string ResolveFontFamily(Language? targetLanguage)
    {
        if (targetLanguage == null)
        {
            return "Arial";
        }

        var cjkFamily = targetLanguage switch
        {
            Language.SimplifiedChinese => CjkFontResolver.NotoSansSC,
            Language.TraditionalChinese => CjkFontResolver.NotoSansTC,
            Language.Japanese => CjkFontResolver.NotoSansJP,
            Language.Korean => CjkFontResolver.NotoSansKR,
            _ => null
        };

        if (cjkFamily != null && CjkFontResolver.IsFontRegistered(cjkFamily))
        {
            return cjkFamily;
        }

        // Fall back to Windows system CJK fonts before Arial
        var systemCjk = targetLanguage switch
        {
            Language.SimplifiedChinese => CjkFontResolver.MicrosoftYaHei,
            Language.TraditionalChinese => CjkFontResolver.MicrosoftJhengHei,
            Language.Japanese => CjkFontResolver.YuGothic,
            Language.Korean => CjkFontResolver.MalgunGothic,
            _ => null
        };

        if (systemCjk != null)
        {
            var systemFontFile = systemCjk switch
            {
                CjkFontResolver.MicrosoftYaHei => "msyh.ttc",
                CjkFontResolver.MicrosoftJhengHei => "msjh.ttc",
                CjkFontResolver.YuGothic => "yugothm.ttc",
                CjkFontResolver.MalgunGothic => "malgun.ttf",
                _ => null
            };

            if (systemFontFile != null)
            {
                var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (File.Exists(Path.Combine(fontsDir, systemFontFile)))
                {
                    Debug.WriteLine($"[PdfExportService] Using system CJK font: {systemCjk}");
                    return systemCjk;
                }
            }
        }

        return "Arial";
    }

    /// <summary>
    /// Returns the line height for overlay rendering, accounting for language-specific multipliers.
    /// Configured languages use their explicit multiplier; others get a 1.15× default for readability.
    /// </summary>
    internal static double GetLineHeight(Language? targetLanguage, double baseLineHeight = 14d)
    {
        if (targetLanguage != null && LineHeightMultipliers.TryGetValue(targetLanguage.Value, out var multiplier))
        {
            return baseLineHeight * multiplier;
        }
        // Default multiplier for non-configured languages — slightly above 1.0
        // to improve readability for Latin/Cyrillic/etc. scripts.
        return baseLineHeight * 1.15;
    }

    /// <summary>
    /// Ensures CJK font resolver is set up and fonts are registered if available.
    /// </summary>
    private static void EnsureCjkFontSetup(Language? targetLanguage)
    {
        if (targetLanguage == null || !FontDownloadService.RequiresCjkFont(targetLanguage.Value))
        {
            return;
        }

        CjkFontResolver.EnsureInitialized();

        // Try to register font from the download cache
        using var fontService = new FontDownloadService();
        var fontPath = fontService.GetCachedFontPath(targetLanguage.Value);
        if (fontPath != null)
        {
            var familyName = targetLanguage switch
            {
                Language.SimplifiedChinese => CjkFontResolver.NotoSansSC,
                Language.TraditionalChinese => CjkFontResolver.NotoSansTC,
                Language.Japanese => CjkFontResolver.NotoSansJP,
                Language.Korean => CjkFontResolver.NotoSansKR,
                _ => null
            };

            if (familyName != null)
            {
                CjkFontResolver.RegisterFont(familyName, fontPath);
            }
        }
        else
        {
            Debug.WriteLine($"[PdfExportService] CJK font not downloaded for {targetLanguage}. Using Arial fallback.");
        }
    }

    /// <summary>Minimum font size for font shrinking — lowered from 8 to 6 to give more room before truncation.</summary>
    private const double MinFontSize = 6;

    internal static XFont FitFontToRect(XGraphics gfx, string text, XFont baseFont, double width, double height, double lineHeight = 14d)
    {
        var engine = TextLayoutEngine.Instance;
        var lineHeightMultiplier = lineHeight / baseFont.Size;
        var request = new FontFitRequest
        {
            Text = text,
            StartFontSize = baseFont.Size,
            MinFontSize = MinFontSize,
            MaxWidth = width,
            MaxHeight = height,
            LineHeightMultiplier = lineHeightMultiplier,
        };
        var result = FontFitSolver.Solve(request, engine,
            fontSize => new XGraphicsMeasurer(gfx, new XFont(baseFont.Name, fontSize, baseFont.Style)));

        return new XFont(baseFont.Name, result.ChosenFontSize, baseFont.Style);
    }

    /// <summary>
    /// Fits a font to a set of line rectangles by shrinking until the text can be wrapped
    /// into at most <paramref name="lineRects"/>.Count lines (and fits the smallest line height).
    /// </summary>
    internal static XFont FitFontToLineRects(XGraphics gfx, string text, XFont baseFont, IReadOnlyList<XRect> lineRects)
    {
        if (lineRects.Count == 0)
            return baseFont;

        var widths = lineRects.Select(r => Math.Max(10, r.Width)).ToList();
        var heights = lineRects.Select(r => Math.Max(1, r.Height)).ToList();

        var engine = TextLayoutEngine.Instance;
        var request = new FontFitRequest
        {
            Text = text,
            StartFontSize = baseFont.Size,
            MinFontSize = MinFontSize,
            LineWidths = widths,
            LineHeights = heights,
        };
        var result = FontFitSolver.Solve(request, engine,
            fontSize => new XGraphicsMeasurer(gfx, new XFont(baseFont.Name, fontSize, baseFont.Style)));

        return new XFont(baseFont.Name, result.ChosenFontSize, baseFont.Style);
    }

    internal static IEnumerable<string> WrapText(string text, int maxChars)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            var p = paragraph.TrimEnd('\r');
            if (p.Length <= maxChars)
            {
                yield return p;
                continue;
            }

            var start = 0;
            while (start < p.Length)
            {
                var len = Math.Min(maxChars, p.Length - start);
                yield return p.Substring(start, len);
                start += len;
            }
        }
    }

    internal static IEnumerable<string> WrapTextByWidth(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var engine = TextLayoutEngine.Instance;
        var measurer = new XGraphicsMeasurer(gfx, font);
        var prepared = engine.Prepare(
            new TextPrepareRequest { Text = text, NormalizeWhitespace = false },
            measurer);
        var result = engine.LayoutWithLines(prepared, maxWidth);
        return result.Lines.Select(l => l.Text);
    }

    /// <summary>
    /// Wraps text using a different max width for each output line. If the text exceeds the number of
    /// widths provided, wrapping continues using the last width (so callers can detect overflow by line count).
    /// </summary>
    internal static IEnumerable<string> WrapTextByWidths(XGraphics gfx, string text, XFont font, IReadOnlyList<double> maxWidths)
    {
        if (maxWidths.Count == 0)
            return [];

        var widths = maxWidths.Select(w => Math.Max(10, w)).ToList();
        var engine = TextLayoutEngine.Instance;
        var measurer = new XGraphicsMeasurer(gfx, font);
        var prepared = engine.Prepare(
            new TextPrepareRequest { Text = text, NormalizeWhitespace = false },
            measurer);
        var result = engine.LayoutWithLines(prepared, widths);
        return result.Lines.Select(l => l.Text);
    }

    /// <summary>
    /// Builds per-line rectangles from extracted source line positions.
    /// Returns null when line positions are missing or look like a multi-column same-baseline grid.
    /// </summary>
    internal static IReadOnlyList<XRect>? TryBuildLineRects(
        double pageHeightPoints,
        XRect blockRect,
        BlockTextStyle? style,
        double fallbackLineHeight)
    {
        var positions = style?.LinePositions;
        if (positions == null || positions.Count == 0)
            return null;

        // Single-line blocks (e.g., titles/headings) often have a tall bounding box.
        // If we force a single output line, long translations can over-shrink fonts.
        // Instead, allow a small number of virtual lines when height permits.
        if (positions.Count == 1)
        {
            var p = positions[0];
            var leftSingle = Math.Max(blockRect.X, p.Left);
            var rightSingle = Math.Min(blockRect.Right, p.Right);
            if (rightSingle - leftSingle < 5 || blockRect.Height < 3)
                return null;

            var singleLineSpacing = style?.LineSpacing > 0 ? style.LineSpacing : 0;
            if (singleLineSpacing <= 0 && style?.FontSize > 0)
            {
                singleLineSpacing = style.FontSize * 1.3;
            }
            if (singleLineSpacing <= 0)
            {
                singleLineSpacing = Math.Max(8, fallbackLineHeight);
            }

            var suggested = (int)Math.Floor(blockRect.Height / Math.Max(1, singleLineSpacing));
            var lineCount = Math.Clamp(suggested, 1, 3);
            if (lineCount <= 1)
            {
                return [new XRect(leftSingle, blockRect.Y, rightSingle - leftSingle, blockRect.Height)];
            }

            var h = blockRect.Height / lineCount;
            if (h < 3)
            {
                return [new XRect(leftSingle, blockRect.Y, rightSingle - leftSingle, blockRect.Height)];
            }

            var rects = new List<XRect>(lineCount);
            for (var i = 0; i < lineCount; i++)
            {
                rects.Add(new XRect(leftSingle, blockRect.Y + i * h, rightSingle - leftSingle, h));
            }
            return rects;
        }

        // If multiple line entries share (approximately) the same baseline, this is likely a grid/row layout.
        // We can't reliably map a single translated paragraph back into multiple same-row cells, so fall back.
        var sortedBaselines = positions.Select(p => p.BaselineY).OrderByDescending(v => v).ToList();
        for (var i = 1; i < sortedBaselines.Count; i++)
        {
            if (Math.Abs(sortedBaselines[i - 1] - sortedBaselines[i]) < 0.5)
                return null;
        }

        var lineSpacing = style?.LineSpacing > 0 ? style.LineSpacing : 0;
        if (lineSpacing <= 0 && sortedBaselines.Count > 1)
        {
            var gaps = new List<double>();
            for (var i = 0; i < sortedBaselines.Count - 1; i++)
            {
                var gap = sortedBaselines[i] - sortedBaselines[i + 1];
                if (gap > 0.1)
                    gaps.Add(gap);
            }
            gaps.Sort();
            if (gaps.Count > 0)
                lineSpacing = gaps[gaps.Count / 2];
        }
        if (lineSpacing <= 0)
            lineSpacing = Math.Max(8, fallbackLineHeight);

        var result = new List<XRect>(positions.Count);
        var ordered = positions.OrderByDescending(p => p.BaselineY).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var pos = ordered[i];
            var upperPdf = i == 0 ? pos.BaselineY + lineSpacing / 2 : (ordered[i - 1].BaselineY + pos.BaselineY) / 2;
            var lowerPdf = i == ordered.Count - 1 ? pos.BaselineY - lineSpacing / 2 : (pos.BaselineY + ordered[i + 1].BaselineY) / 2;
            if (upperPdf <= lowerPdf)
                continue;

            var y = pageHeightPoints - upperPdf;
            var height = upperPdf - lowerPdf;

            var left = Math.Max(blockRect.X, pos.Left);
            var right = Math.Min(blockRect.Right, pos.Right);
            if (right - left < 5)
                continue;

            // Clamp vertically into the block rect.
            var yTop = Math.Max(blockRect.Y, y);
            var yBottom = Math.Min(blockRect.Bottom, y + height);
            var h = yBottom - yTop;
            if (h < 3)
                continue;

            result.Add(new XRect(left, yTop, right - left, h));
        }

        return result.Count > 0 ? result : null;
    }

    internal static bool LooksLikeGridLinePositions(IReadOnlyList<BlockLinePosition> positions)
    {
        if (positions.Count < 2)
            return false;

        // Same-baseline multiple segments (columns on the same row) => grid.
        var sortedBaselines = positions.Select(p => p.BaselineY).OrderByDescending(v => v).ToList();
        for (var i = 1; i < sortedBaselines.Count; i++)
        {
            if (Math.Abs(sortedBaselines[i - 1] - sortedBaselines[i]) < 0.5)
                return true;
        }
        return false;
    }

    private static bool IsCjkCharacter(char ch)
    {
        return ch is >= '\u4E00' and <= '\u9FFF'    // CJK Unified Ideographs
            or >= '\u3400' and <= '\u4DBF'           // CJK Extension A
            or >= '\u3000' and <= '\u303F'           // CJK Symbols and Punctuation
            or >= '\u3040' and <= '\u309F'           // Hiragana
            or >= '\u30A0' and <= '\u30FF'           // Katakana
            or >= '\uAC00' and <= '\uD7AF'           // Hangul Syllables
            or >= '\uFF00' and <= '\uFFEF'           // Fullwidth Forms
            or >= '\uF900' and <= '\uFAFF';          // CJK Compatibility Ideographs
    }

    // --------------------------------------------------
    // Page-level backfill metrics helpers (migrated)
    // --------------------------------------------------

    internal static PageBackfillAccumulator GetOrCreatePageBackfill(
        IDictionary<int, PageBackfillAccumulator> pageMetrics,
        int pageNumber)
    {
        if (!pageMetrics.TryGetValue(pageNumber, out var metrics))
        {
            metrics = new PageBackfillAccumulator();
            pageMetrics[pageNumber] = metrics;
        }

        return metrics;
    }

    internal static IReadOnlyDictionary<int, BackfillPageMetrics>? BuildPageBackfillMetrics(
        IReadOnlyDictionary<int, PageBackfillAccumulator> pageMetrics)
    {
        if (pageMetrics.Count == 0)
        {
            return null;
        }

        return pageMetrics.ToDictionary(
            entry => entry.Key,
            entry => new BackfillPageMetrics
            {
                CandidateBlocks = entry.Value.CandidateBlocks,
                RenderedBlocks = entry.Value.RenderedBlocks,
                MissingBoundingBoxBlocks = entry.Value.MissingBoundingBoxBlocks,
                ShrinkFontBlocks = entry.Value.ShrinkFontBlocks,
                TruncatedBlocks = entry.Value.TruncatedBlocks,
                ObjectReplaceBlocks = entry.Value.ObjectReplaceBlocks,
                OverlayModeBlocks = entry.Value.OverlayModeBlocks,
                StructuredFallbackBlocks = entry.Value.StructuredFallbackBlocks
            });
    }

    internal static BackfillQualityMetrics ToQualityMetrics(BackfillRenderingMetrics metrics)
    {
        return new BackfillQualityMetrics
        {
            CandidateBlocks = metrics.CandidateBlocks,
            RenderedBlocks = metrics.RenderedBlocks,
            MissingBoundingBoxBlocks = metrics.MissingBoundingBoxBlocks,
            ShrinkFontBlocks = metrics.ShrinkFontBlocks,
            TruncatedBlocks = metrics.TruncatedBlocks,
            ObjectReplaceBlocks = metrics.ObjectReplaceBlocks,
            OverlayModeBlocks = metrics.OverlayModeBlocks,
            StructuredFallbackBlocks = metrics.StructuredFallbackBlocks,
            PageMetrics = metrics.PageMetrics,
            BlockIssues = metrics.BlockIssues
        };
    }

    // --------------------------------------------------
    // Multi-font rendering for math characters
    // --------------------------------------------------

    /// <summary>
    /// Returns true for Unicode characters that need a math-capable font (Cambria Math)
    /// because CJK fonts typically lack these glyphs.
    /// </summary>
    internal static bool NeedsMathFont(char ch)
    {
        return ch is >= '\u2200' and <= '\u22FF'   // Mathematical Operators
            or >= '\u0370' and <= '\u03FF'          // Greek and Coptic
            or >= '\u2100' and <= '\u214F'          // Letterlike Symbols
            or >= '\u2070' and <= '\u209F'          // Superscripts and Subscripts
            or '\u2212'                             // Minus sign
            or '\u00D7'                             // Multiplication sign
            or '\u00F7'                             // Division sign
            or >= '\u2190' and <= '\u21FF'          // Arrows
            or >= '\u2300' and <= '\u23FF'          // Miscellaneous Technical
            or >= '\u27C0' and <= '\u27EF'          // Miscellaneous Mathematical Symbols-A
            or >= '\u2980' and <= '\u29FF'          // Miscellaneous Mathematical Symbols-B
            or >= '\u2A00' and <= '\u2AFF'          // Supplemental Mathematical Operators
            or >= '\u25A0' and <= '\u25FF'          // Geometric Shapes (includes □ U+25A1)
            or >= '\u2500' and <= '\u257F'          // Box Drawing
            or >= '\u2150' and <= '\u218F';         // Number Forms (fractions like ½)
    }

    /// <summary>
    /// A font segment within a line: text that shares the same font requirement.
    /// </summary>
    internal readonly record struct FontSegment(string Text, bool NeedsMathFont);

    /// <summary>
    /// Splits a line into contiguous segments that share the same font requirement.
    /// </summary>
    internal static List<FontSegment> SegmentLineByFont(string line)
    {
        if (string.IsNullOrEmpty(line))
            return [new FontSegment(string.Empty, false)];

        var segments = new List<FontSegment>();
        var current = new StringBuilder();
        var currentNeedsMath = NeedsMathFont(line[0]);

        foreach (var ch in line)
        {
            var charNeedsMath = NeedsMathFont(ch);
            if (charNeedsMath != currentNeedsMath)
            {
                if (current.Length > 0)
                    segments.Add(new FontSegment(current.ToString(), currentNeedsMath));
                current.Clear();
                currentNeedsMath = charNeedsMath;
            }
            current.Append(ch);
        }

        if (current.Length > 0)
            segments.Add(new FontSegment(current.ToString(), currentNeedsMath));

        return segments;
    }

    /// <summary>
    /// Returns true if a block has formula context (math font characters or formula-like content).
    /// </summary>
    private static bool IsFormulaContext(LongDocumentChunkMetadata metadata, string translatedText)
    {
        if (metadata.FormulaCharacters?.HasMathFontCharacters == true)
            return true;

        if (metadata.IsFormulaLike)
            return true;

        // Check if translated text contains formula-like patterns
        foreach (var ch in translatedText)
        {
            if (NeedsMathFont(ch))
                return true;
        }

        // Check for script patterns (e.g. h_{t-1}, x^{2}) even without math font chars
        if (ContainsScriptPatterns(translatedText))
            return true;

        return false;
    }

    private static readonly Regex ScriptPatternRegex = new(@"[_^]\{[^}]+\}|[_^]\w", RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the text contains subscript/superscript patterns like x_{t-1} or x^2.
    /// </summary>
    private static bool ContainsScriptPatterns(string text)
    {
        return ScriptPatternRegex.IsMatch(text);
    }

    /// <summary>
    /// Draws a line of text using multiple fonts: base font for normal characters,
    /// Cambria Math for math/Greek/symbol characters. Falls through to regular DrawString
    /// when no math characters are detected (fast path).
    /// </summary>
    private static void DrawStringMultiFont(
        XGraphics gfx,
        string line,
        XFont baseFont,
        XBrush brush,
        XRect lineRect,
        XStringFormat format,
        bool isFormulaContext)
    {
        if (!isFormulaContext)
        {
            gfx.DrawString(line, baseFont, brush, lineRect, format);
            return;
        }

        // Check if any character needs a math font
        var hasMathChars = false;
        foreach (var ch in line)
        {
            if (NeedsMathFont(ch))
            {
                hasMathChars = true;
                break;
            }
        }

        // Check for subscript/superscript patterns
        if (ContainsScriptPatterns(line))
        {
            var fragments = ParseFormulaFragments(line);
            if (fragments.Count > 1 || fragments.Any(f => f.Kind != FormulaFragmentKind.Normal))
            {
                DrawFormulaLine(gfx, fragments, baseFont, brush, lineRect);
                return;
            }
        }

        if (!hasMathChars)
        {
            gfx.DrawString(line, baseFont, brush, lineRect, format);
            return;
        }

        // Segment-based multi-font rendering
        var segments = SegmentLineByFont(line);
        if (segments.Count <= 1 && !segments[0].NeedsMathFont)
        {
            gfx.DrawString(line, baseFont, brush, lineRect, format);
            return;
        }

        var mathFont = CreateMathFont(baseFont.Size, baseFont.Style);
        var x = lineRect.X;
        var y = lineRect.Y;

        foreach (var segment in segments)
        {
            var font = segment.NeedsMathFont ? mathFont : baseFont;
            var size = gfx.MeasureString(segment.Text, font);
            gfx.DrawString(segment.Text, font, brush, new XRect(x, y, size.Width + 1, lineRect.Height), XStringFormats.TopLeft);
            x += size.Width;
        }
    }

    /// <summary>
    /// Creates a math-capable font (Cambria Math) at the specified size.
    /// Falls back to Times New Roman if Cambria Math is unavailable.
    /// </summary>
    private static XFont CreateMathFont(double size, XFontStyle style)
    {
        try
        {
            return new XFont(CjkFontResolver.CambriaMath, size, style);
        }
        catch
        {
            try
            {
                return new XFont(CjkFontResolver.TimesNewRoman, size, style);
            }
            catch
            {
                return new XFont("Arial", size, style);
            }
        }
    }

    // --------------------------------------------------
    // Formula subscript/superscript rendering
    // --------------------------------------------------

    internal enum FormulaFragmentKind
    {
        Normal,
        Subscript,
        Superscript
    }

    internal readonly record struct FormulaFragment(string Text, FormulaFragmentKind Kind);

    /// <summary>
    /// Parses a line containing formula patterns (_{...}, ^{...}, _x, ^x) into
    /// a list of fragments with their rendering kind.
    /// </summary>
    internal static List<FormulaFragment> ParseFormulaFragments(string line)
    {
        if (string.IsNullOrEmpty(line))
            return [new FormulaFragment(string.Empty, FormulaFragmentKind.Normal)];

        var fragments = new List<FormulaFragment>();
        var normalBuffer = new StringBuilder();
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];
            if (ch is '_' or '^' && i + 1 < line.Length)
            {
                // Flush normal text
                if (normalBuffer.Length > 0)
                {
                    fragments.Add(new FormulaFragment(normalBuffer.ToString(), FormulaFragmentKind.Normal));
                    normalBuffer.Clear();
                }

                var kind = ch == '_' ? FormulaFragmentKind.Subscript : FormulaFragmentKind.Superscript;
                i++; // skip _ or ^

                if (i < line.Length && line[i] == '{')
                {
                    // Grouped: _{...} or ^{...}
                    i++; // skip {
                    var groupContent = new StringBuilder();
                    var nesting = 1;
                    while (i < line.Length && nesting > 0)
                    {
                        if (line[i] == '{') nesting++;
                        else if (line[i] == '}') nesting--;

                        if (nesting > 0)
                            groupContent.Append(line[i]);
                        i++;
                    }
                    fragments.Add(new FormulaFragment(groupContent.ToString(), kind));
                }
                else if (i < line.Length)
                {
                    // Single character: _x or ^2
                    fragments.Add(new FormulaFragment(line[i].ToString(), kind));
                    i++;
                }
            }
            else
            {
                normalBuffer.Append(ch);
                i++;
            }
        }

        if (normalBuffer.Length > 0)
            fragments.Add(new FormulaFragment(normalBuffer.ToString(), FormulaFragmentKind.Normal));

        return fragments.Count > 0 ? fragments : [new FormulaFragment(line, FormulaFragmentKind.Normal)];
    }

    /// <summary>
    /// Renders a line with formula fragments, applying vertical offsets for subscripts and superscripts.
    /// Subscripts are drawn smaller and lower, superscripts smaller and higher.
    /// </summary>
    private static void DrawFormulaLine(
        XGraphics gfx,
        List<FormulaFragment> fragments,
        XFont baseFont,
        XBrush brush,
        XRect lineRect)
    {
        var x = lineRect.X;
        var baselineY = lineRect.Y;
        var scriptSize = Math.Max(6, baseFont.Size * 0.7);

        foreach (var fragment in fragments)
        {
            XFont font;
            double yOffset;

            switch (fragment.Kind)
            {
                case FormulaFragmentKind.Subscript:
                    font = new XFont(baseFont.Name, scriptSize, baseFont.Style);
                    yOffset = baseFont.Size * 0.3; // shift down
                    break;
                case FormulaFragmentKind.Superscript:
                    font = new XFont(baseFont.Name, scriptSize, baseFont.Style);
                    yOffset = -baseFont.Size * 0.35; // shift up
                    break;
                default:
                    font = baseFont;
                    yOffset = 0;
                    break;
            }

            // Check if segment has math characters and use math font
            var hasMathChars = fragment.Text.Any(NeedsMathFont);
            if (hasMathChars)
            {
                var mathFont = CreateMathFont(font.Size, font.Style);
                var segments = SegmentLineByFont(fragment.Text);
                foreach (var seg in segments)
                {
                    var segFont = seg.NeedsMathFont ? mathFont : font;
                    var segSize = gfx.MeasureString(seg.Text, segFont);
                    gfx.DrawString(seg.Text, segFont, brush,
                        new XRect(x, baselineY + yOffset, segSize.Width + 1, lineRect.Height),
                        XStringFormats.TopLeft);
                    x += segSize.Width;
                }
            }
            else
            {
                var textSize = gfx.MeasureString(fragment.Text, font);
                gfx.DrawString(fragment.Text, font, brush,
                    new XRect(x, baselineY + yOffset, textSize.Width + 1, lineRect.Height),
                    XStringFormats.TopLeft);
                x += textSize.Width;
            }
        }
    }
}
