using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
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

    private sealed class PageBackfillAccumulator
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

    public DocumentExportResult Export(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
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

        bilingualDoc.Save(bilingualOutputPath);
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

    internal static BackfillRenderingMetrics ExportPdfWithCoordinateBackfill(LongDocumentTranslationCheckpoint checkpoint, string sourcePdfPath, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

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

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var box = metadata.BoundingBox.Value;

            var drawX = Math.Max(0, box.X);
            var drawY = Math.Max(0, page.Height.Point - (box.Y + box.Height));
            var drawWidth = Math.Max(40, box.Width);
            var drawHeight = Math.Max(14, box.Height);

            var rect = new XRect(drawX, drawY, drawWidth, drawHeight);
            var baseFont = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike);
            var font = FitFontToRect(gfx, translated, baseFont, rect.Width, rect.Height);
            if (font.Size < baseFont.Size)
            {
                shrinkFontBlocks++;
                perPage.ShrinkFontBlocks++;
            }

            var wrappedLines = WrapTextByWidth(gfx, translated, font, rect.Width).ToList();
            var maxVisibleLines = Math.Max(1, (int)Math.Floor(rect.Height / 14d));
            if (wrappedLines.Count > maxVisibleLines)
            {
                wrappedLines = wrappedLines.Take(maxVisibleLines).ToList();
                var last = wrappedLines[^1];
                wrappedLines[^1] = last.Length > 1 ? $"{last.TrimEnd('.', ' ')}…" : "…";
                truncatedBlocks++;
                perPage.TruncatedBlocks++;
            }

            var lineY = rect.Y;
            foreach (var line in wrappedLines)
            {
                gfx.DrawString(line, font, XBrushes.Black, new XRect(rect.X, lineY, rect.Width, 20), XStringFormats.TopLeft);
                lineY += 14;
            }

            renderedBlocks++;
            overlayModeBlocks++;
            perPage.RenderedBlocks++;
            perPage.OverlayModeBlocks++;
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
                var y = margin;
                var width = page.Width - margin * 2;

                var headingFont = new XFont("Arial", 14, XFontStyle.Bold);
                gfx.DrawString($"Page {pageGroup.Key}", headingFont, XBrushes.Black, new XRect(margin, y, width, 24), XStringFormats.TopLeft);
                y += 24;

                foreach (var chunkIndex in pageGroup)
                {
                    var metadata = metadataByChunkIndex[chunkIndex];
                    var content = checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated)
                        ? translated
                        : $"[Chunk {chunkIndex + 1} translation failed. Retry required.]";

                    var font = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike);
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
                        y += 16;
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
        catch
        {
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

    internal static XFont PickFont(SourceBlockType sourceBlockType, bool isFormulaLike)
    {
        if (sourceBlockType == SourceBlockType.Heading)
        {
            return new XFont("Arial", 14, XFontStyle.Bold);
        }

        if (sourceBlockType == SourceBlockType.Formula || isFormulaLike)
        {
            return new XFont("Consolas", 11, XFontStyle.Italic);
        }

        return new XFont("Arial", 11);
    }

    internal static XFont FitFontToRect(XGraphics gfx, string text, XFont baseFont, double width, double height)
    {
        var size = baseFont.Size;
        while (size >= 8)
        {
            var candidate = new XFont(baseFont.Name, size, baseFont.Style);
            var lines = WrapTextByWidth(gfx, text, candidate, width).ToList();
            var maxLines = Math.Max(1, (int)Math.Floor(height / 14d));
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

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var line = words[0];
            for (var i = 1; i < words.Length; i++)
            {
                var candidate = $"{line} {words[i]}";
                if (gfx.MeasureString(candidate, font).Width <= maxWidth)
                {
                    line = candidate;
                }
                else
                {
                    yield return line;
                    line = words[i];
                }
            }

            yield return line;
        }
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
