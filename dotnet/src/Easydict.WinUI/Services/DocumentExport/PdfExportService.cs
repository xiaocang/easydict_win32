using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
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
        IReadOnlyDictionary<int, BackfillPageMetrics>? PageMetrics)
    {
        public static BackfillRenderingMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, null);
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
    /// CJK characters need more vertical space than Latin text.
    /// </summary>
    private static readonly Dictionary<Language, double> LineHeightMultipliers = new()
    {
        [Language.SimplifiedChinese] = 1.4,
        [Language.TraditionalChinese] = 1.4,
        [Language.Japanese] = 1.4,
        [Language.Korean] = 1.3,
    };

    public DocumentExportResult Export(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
        // Set up CJK font resolver if target language requires it
        EnsureCjkFontSetup(checkpoint.TargetLanguage);

        // 1. Always generate monolingual PDF first (existing backfill logic)
        ExportPdfWithCoordinateBackfill(checkpoint, sourceFilePath, outputPath);

        // 2. Handle bilingual mode
        string? bilingualPath = null;
        if (outputMode is DocumentOutputMode.Bilingual or DocumentOutputMode.Both)
        {
            bilingualPath = BuildBilingualOutputPath(outputPath);
            ExportBilingualPdf(sourceFilePath, outputPath, bilingualPath);
        }

        // 3. Bilingual-only: delete intermediate monolingual file, return bilingual path
        if (outputMode == DocumentOutputMode.Bilingual && bilingualPath != null)
        {
            try { File.Delete(outputPath); } catch { /* best-effort cleanup */ }
            return new DocumentExportResult
            {
                OutputPath = bilingualPath,
                BilingualOutputPath = bilingualPath
            };
        }

        // 4. Both or Monolingual
        return new DocumentExportResult
        {
            OutputPath = outputPath,
            BilingualOutputPath = bilingualPath
        };
    }

    // --------------------------------------------------
    // Bilingual PDF export (new)
    // --------------------------------------------------

    /// <summary>
    /// Creates a bilingual PDF by interleaving pages from the source PDF and the translated PDF.
    /// Original page 1 → Translated page 1 → Original page 2 → Translated page 2 → ...
    /// </summary>
    internal static void ExportBilingualPdf(string sourcePdfPath, string translatedPdfPath, string bilingualOutputPath)
    {
        var outputDirectory = Path.GetDirectoryName(bilingualOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var sourceDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
        using var translatedDoc = PdfReader.Open(translatedPdfPath, PdfDocumentOpenMode.Import);
        var bilingualDoc = new PdfDocument();

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

        // Copy bookmarks from source PDF, mapping page numbers for interleaved layout
        CopyBookmarksForBilingual(sourceDoc, bilingualDoc);

        bilingualDoc.Save(bilingualOutputPath);
    }

    /// <summary>
    /// Copies bookmarks from the source PDF to the bilingual PDF,
    /// adjusting page references for the interleaved layout (source page N → bilingual page 2N-1).
    /// </summary>
    internal static void CopyBookmarksForBilingual(PdfDocument sourceDoc, PdfDocument bilingualDoc)
    {
        try
        {
            if (sourceDoc.Outlines.Count == 0)
                return;

            CopyOutlineLevel(sourceDoc.Outlines, bilingualDoc.Outlines, sourceDoc, bilingualDoc);
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
        PdfDocument bilingualDoc)
    {
        foreach (var sourceOutline in sourceOutlines)
        {
            try
            {
                var sourcePageIndex = FindOutlinePageIndex(sourceOutline, sourceDoc);
                if (sourcePageIndex < 0)
                {
                    // No page reference, add as title-only bookmark
                    var newOutline = targetOutlines.Add(sourceOutline.Title, bilingualDoc.Pages[0]);
                    if (sourceOutline.Outlines.Count > 0)
                        CopyOutlineLevel(sourceOutline.Outlines, newOutline.Outlines, sourceDoc, bilingualDoc);
                    continue;
                }

                // Map source page index to bilingual page index: source page i → bilingual page 2*i
                var bilingualPageIndex = sourcePageIndex * 2;
                if (bilingualPageIndex >= bilingualDoc.PageCount)
                    bilingualPageIndex = bilingualDoc.PageCount - 1;

                var targetOutline = targetOutlines.Add(sourceOutline.Title, bilingualDoc.Pages[bilingualPageIndex]);

                // Recurse into children
                if (sourceOutline.Outlines.Count > 0)
                    CopyOutlineLevel(sourceOutline.Outlines, targetOutline.Outlines, sourceDoc, bilingualDoc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PdfExport] Failed to copy bookmark '{sourceOutline.Title}': {ex.Message}");
            }
        }
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
        public required XRect Rect { get; init; }
        public required double Padding { get; init; }
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
        var isCjkTarget = targetLanguage != null && LineHeightMultipliers.ContainsKey(targetLanguage.Value);
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

        // Collect overlay blocks grouped by page for two-pass rendering
        var overlayBlocksByPage = new Dictionary<int, List<OverlayBlockInfo>>();

        foreach (var chunkIndex in Enumerable.Range(0, checkpoint.SourceChunks.Count))
        {
            if (!checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            var metadata = metadataByChunkIndex[chunkIndex];
            if (metadata.SourceBlockType == SourceBlockType.Formula || metadata.IsFormulaLike)
            {
                continue;
            }

            candidateBlocks++;
            var perPage = GetOrCreatePageBackfill(pageMetrics, metadata.PageNumber);
            perPage.CandidateBlocks++;

            if (metadata.BoundingBox is null)
            {
                missingBoundingBoxBlocks++;
                perPage.MissingBoundingBoxBlocks++;
                continue;
            }

            var pageIndex = metadata.PageNumber - 1;
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
            {
                missingBoundingBoxBlocks++;
                perPage.MissingBoundingBoxBlocks++;
                continue;
            }

            var page = doc.Pages[pageIndex];
            var sourceText = checkpoint.SourceChunks[chunkIndex];
            if (TryReplacePdfTextObject(page, sourceText, translated))
            {
                renderedBlocks++;
                objectReplaceBlocks++;
                perPage.RenderedBlocks++;
                perPage.ObjectReplaceBlocks++;
                continue;
            }

            var box = metadata.BoundingBox.Value;
            var drawX = Math.Max(0, box.X);
            var drawY = Math.Max(0, page.Height.Point - (box.Y + box.Height));
            var drawWidth = Math.Max(10, box.Width);
            var drawHeight = Math.Max(10, box.Height);
            var rect = new XRect(drawX, drawY, drawWidth, drawHeight);

            // Scale padding with font size: larger fonts need more padding to cover descenders
            var fontSize = metadata.TextStyle?.FontSize > 0 ? metadata.TextStyle.FontSize : 11.0;
            var pad = Math.Min(6, Math.Max(2, fontSize * 0.15));

            if (!overlayBlocksByPage.TryGetValue(pageIndex, out var pageBlocks))
            {
                pageBlocks = new List<OverlayBlockInfo>();
                overlayBlocksByPage[pageIndex] = pageBlocks;
            }

            pageBlocks.Add(new OverlayBlockInfo
            {
                ChunkIndex = chunkIndex,
                TranslatedText = translated,
                Metadata = metadata,
                Rect = rect,
                Padding = pad
            });
        }

        // Two-pass rendering: for each page, draw all white backgrounds first, then all text
        foreach (var (pageIndex, blocks) in overlayBlocksByPage)
        {
            var page = doc.Pages[pageIndex];

            try
            {
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                // Pass 1: Draw all white background rectangles
                foreach (var block in blocks)
                {
                    gfx.DrawRectangle(XBrushes.White,
                        new XRect(
                            block.Rect.X - block.Padding,
                            block.Rect.Y - block.Padding,
                            block.Rect.Width + block.Padding * 2,
                            block.Rect.Height + block.Padding * 2));
                }

                // Pass 2: Draw all translated text
                foreach (var block in blocks)
                {
                    var metadata = block.Metadata;
                    var perPage = GetOrCreatePageBackfill(pageMetrics, metadata.PageNumber);
                    var rect = block.Rect;

                    var style = metadata.TextStyle;
                    var rotationAngle = style?.RotationAngle ?? 0;

                    // Handle rotated text (e.g., vertical sidebar text like arXiv identifiers)
                    if (rotationAngle is not 0)
                    {
                        var rotatedFont = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike, targetLanguage, metadata.BoundingBox!.Value.Width, style);
                        var brush = CreateBrush(style);

                        var state = gfx.Save();
                        var centerX = rect.X + rect.Width / 2;
                        var centerY = rect.Y + rect.Height / 2;
                        gfx.RotateAtTransform(rotationAngle, new XPoint(centerX, centerY));

                        // In rotated space, swap width/height for the text rectangle
                        var rotatedRect = new XRect(
                            centerX - rect.Height / 2,
                            centerY - rect.Width / 2,
                            rect.Height,
                            rect.Width);

                        gfx.DrawString(block.TranslatedText, rotatedFont, brush, rotatedRect, XStringFormats.TopLeft);
                        gfx.Restore(state);

                        renderedBlocks++;
                        overlayModeBlocks++;
                        perPage.RenderedBlocks++;
                        perPage.OverlayModeBlocks++;
                        continue;
                    }

                    var effectiveLineHeight = style?.LineSpacing > 0 ? style.LineSpacing : lineHeight;

                    // For CJK targets, ensure minimum line height based on font size
                    var baseFont = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike, targetLanguage, metadata.BoundingBox!.Value.Height, style);
                    if (isCjkTarget)
                    {
                        effectiveLineHeight = Math.Max(effectiveLineHeight, baseFont.Size * 1.4);
                    }

                    var font = FitFontToRect(gfx, block.TranslatedText, baseFont, rect.Width, rect.Height, effectiveLineHeight);
                    if (font.Size < baseFont.Size)
                    {
                        shrinkFontBlocks++;
                        perPage.ShrinkFontBlocks++;
                    }

                    var wrappedLines = WrapTextByWidth(gfx, block.TranslatedText, font, rect.Width).ToList();
                    var maxVisibleLines = Math.Max(1, (int)Math.Floor(rect.Height / effectiveLineHeight));
                    if (wrappedLines.Count > maxVisibleLines)
                    {
                        wrappedLines = wrappedLines.Take(maxVisibleLines).ToList();
                        var last = wrappedLines[^1];
                        wrappedLines[^1] = last.Length > 1 ? $"{last.TrimEnd('.', ' ')}…" : "…";
                        truncatedBlocks++;
                        perPage.TruncatedBlocks++;
                    }

                    {
                        var brush = CreateBrush(style);
                        var stringFormat = GetStringFormat(style);

                        var lineY = rect.Y;
                        foreach (var line in wrappedLines)
                        {
                            gfx.DrawString(line, font, brush, new XRect(rect.X, lineY, rect.Width, effectiveLineHeight), stringFormat);
                            lineY += effectiveLineHeight;
                        }
                    }

                    renderedBlocks++;
                    overlayModeBlocks++;
                    perPage.RenderedBlocks++;
                    perPage.OverlayModeBlocks++;
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
            BuildPageBackfillMetrics(pageMetrics));
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
                    var content = checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated)
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

        return new BackfillRenderingMetrics(0, 0, 0, 0, 0, 0, 0, checkpoint.SourceChunks.Count, structuredPageMetrics);
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
    /// Returns the line height for overlay rendering, accounting for CJK languages.
    /// </summary>
    internal static double GetLineHeight(Language? targetLanguage, double baseLineHeight = 14d)
    {
        if (targetLanguage != null && LineHeightMultipliers.TryGetValue(targetLanguage.Value, out var multiplier))
        {
            return baseLineHeight * multiplier;
        }
        return baseLineHeight;
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

    internal static XFont FitFontToRect(XGraphics gfx, string text, XFont baseFont, double width, double height, double lineHeight = 14d)
    {
        var size = baseFont.Size;
        while (size >= 8)
        {
            var candidate = new XFont(baseFont.Name, size, baseFont.Style);
            var lines = WrapTextByWidth(gfx, text, candidate, width).ToList();
            var maxLines = Math.Max(1, (int)Math.Floor(height / lineHeight));
            if (lines.Count <= maxLines)
            {
                return candidate;
            }

            size -= 0.5;
        }

        return new XFont(baseFont.Name, 8, baseFont.Style);
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
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                yield return string.Empty;
                continue;
            }

            var line = new StringBuilder();
            var lineWidth = 0.0;

            foreach (var token in TokenizeForWrapping(paragraph))
            {
                var tokenWidth = gfx.MeasureString(token, font).Width;

                if (line.Length > 0 && lineWidth + tokenWidth > maxWidth)
                {
                    yield return line.ToString();
                    line.Clear();
                    lineWidth = 0;
                }

                line.Append(token);
                lineWidth += tokenWidth;
            }

            if (line.Length > 0)
                yield return line.ToString();
        }
    }

    /// <summary>
    /// Splits text into wrappable tokens: individual CJK characters and space-delimited Latin words.
    /// CJK text can break at any character boundary; Latin text breaks at spaces.
    /// </summary>
    private static IEnumerable<string> TokenizeForWrapping(string text)
    {
        var wordBuffer = new StringBuilder();
        foreach (var ch in text)
        {
            if (IsCjkCharacter(ch))
            {
                if (wordBuffer.Length > 0)
                {
                    yield return wordBuffer.ToString();
                    wordBuffer.Clear();
                }
                yield return ch.ToString();
            }
            else if (ch == ' ')
            {
                if (wordBuffer.Length > 0)
                {
                    yield return wordBuffer.ToString();
                    wordBuffer.Clear();
                }
                wordBuffer.Append(ch);
            }
            else
            {
                wordBuffer.Append(ch);
            }
        }
        if (wordBuffer.Length > 0)
            yield return wordBuffer.ToString();
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
}
