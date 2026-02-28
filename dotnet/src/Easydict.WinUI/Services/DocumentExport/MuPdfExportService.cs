// MuPDF.NET-based PDF export service for pdf2zh-aligned content stream replacement.
// Uses MuPDF.NET (official C# bindings) for font embedding, content stream replacement,
// XRef manipulation, and dual PDF output — matching pdf2zh's PyMuPDF-based pipeline.

using System.Diagnostics;
using System.Text;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using MuPDF.NET;
using MuPdfPage = MuPDF.NET.Page;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfPigPage = UglyToad.PdfPig.Content.Page;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// PDF export service using MuPDF.NET for content stream replacement.
/// This replaces the PdfSharpCore overlay approach with pdf2zh's strategy:
/// 1. Extract graphics ops (ops_base) via ContentStreamInterpreter
/// 2. Build character-level paragraphs via CharacterParagraphBuilder
/// 3. Replace page content stream via MuPDF.NET (UpdateStream + SetContents)
/// 4. Embed Noto/SourceHan fonts via InsertFont
/// 5. Generate dual PDF via InsertFile + MovePage
/// </summary>
public sealed class MuPdfExportService : IDocumentExportService
{
    public IReadOnlyList<string> SupportedExtensions => [".pdf"];

    /// <summary>
    /// Language → CJK font name mapping for embedded fonts.
    /// Uses SourceHanSerif (思源宋体) variants like pdf2zh.
    /// </summary>
    private static readonly Dictionary<Language, string> CjkFontNames = new()
    {
        [Language.SimplifiedChinese] = "SourceHanSerifCN",
        [Language.TraditionalChinese] = "SourceHanSerifTW",
        [Language.Japanese] = "SourceHanSerifJP",
        [Language.Korean] = "SourceHanSerifKR",
    };

    /// <summary>
    /// Noto font name for non-CJK scripts (Latin, Arabic, Cyrillic, Devanagari, etc.)
    /// Matches pdf2zh's GoNotoKurrent-Regular.
    /// </summary>
    private const string NotoFontName = "noto";

    public DocumentExportResult Export(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
        // 1. Generate monolingual translated PDF
        var metrics = ExportWithContentStreamReplacement(checkpoint, sourceFilePath, outputPath);

        // 2. Handle bilingual mode
        string? bilingualPath = null;
        if (outputMode is DocumentOutputMode.Bilingual or DocumentOutputMode.Both)
        {
            bilingualPath = Path.Combine(
                Path.GetDirectoryName(outputPath) ?? ".",
                Path.GetFileNameWithoutExtension(outputPath) + "_bilingual.pdf");
            GenerateBilingualPdf(sourceFilePath, outputPath, bilingualPath);
        }

        return new DocumentExportResult
        {
            OutputPath = outputPath,
            BilingualOutputPath = bilingualPath,
            BackfillMetrics = metrics,
        };
    }

    /// <summary>
    /// Core export method: replaces each page's content stream with translated text.
    /// Follows pdf2zh's pipeline:
    /// 1. Open source PDF with both PdfPig (read) and MuPDF.NET (write)
    /// 2. For each page: extract ops_base + characters, build paragraphs, generate new ops
    /// 3. Replace content stream, embed fonts, save
    /// </summary>
    private BackfillQualityMetrics? ExportWithContentStreamReplacement(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath)
    {
        var sourceBytes = File.ReadAllBytes(sourceFilePath);

        // Read-side: PdfPig for text extraction and content stream parsing
        using var pdfPigDoc = PdfPigDocument.Open(sourceBytes);

        // Write-side: MuPDF.NET for content stream replacement
        var muDoc = new Document(sourceFilePath);

        var pageCount = pdfPigDoc.NumberOfPages;
        var totalRendered = 0;
        var totalCandidates = 0;

        // Build lookup: page number → translated blocks
        var translatedBlocksByPage = BuildTranslatedBlockLookup(checkpoint);

        // Resolve font paths for embedding
        var fontPaths = ResolveFontPaths(checkpoint.TargetLanguage);

        for (var pageIdx = 0; pageIdx < pageCount; pageIdx++)
        {
            var pageNumber = pageIdx + 1;
            if (!translatedBlocksByPage.TryGetValue(pageNumber, out var blocks) || blocks.Count == 0)
                continue;

            var pdfPigPage = pdfPigDoc.GetPage(pageNumber);
            var muPage = muDoc[pageIdx];

            try
            {
                var rendered = RenderPageWithContentStreamReplacement(
                    pdfPigPage, muPage, muDoc, blocks, fontPaths);
                totalRendered += rendered;
                totalCandidates += blocks.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuPdfExport] Page {pageNumber} failed: {ex.Message}");
                // Fall through — page retains original content
            }
        }

        // Font subsetting to reduce file size
        try
        {
            muDoc.SubsetFonts();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuPdfExport] SubsetFonts failed: {ex.Message}");
        }

        muDoc.Save(outputPath);
        muDoc.Close();

        return new BackfillQualityMetrics
        {
            CandidateBlocks = totalCandidates,
            RenderedBlocks = totalRendered,
            MissingBoundingBoxBlocks = 0,
            ShrinkFontBlocks = 0,
            TruncatedBlocks = 0,
            ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 0,
            StructuredFallbackBlocks = 0,
        };
    }

    /// <summary>
    /// Renders a single page by replacing its content stream with translated text.
    /// </summary>
    private int RenderPageWithContentStreamReplacement(
        PdfPigPage pdfPigPage,
        MuPdfPage muPage,
        Document muDoc,
        List<TranslatedBlockData> blocks,
        FontPaths fontPaths)
    {
        // Step 1: Extract content stream — separate text from graphics
        var streamResult = ContentStreamInterpreter.Interpret(pdfPigPage);
        var opsBase = streamResult.SerializeGraphicsOperations();

        // Step 2: Embed fonts into the page
        var embeddedFonts = EmbedFonts(muPage, fontPaths);

        // Step 3: Generate translated text operations
        var opsText = new StringBuilder();
        var rendered = 0;

        foreach (var block in blocks)
        {
            if (block.TranslationSkipped || string.IsNullOrWhiteSpace(block.TranslatedText))
                continue;

            if (block.BoundingBox is null)
                continue;

            var bbox = block.BoundingBox.Value;

            // Choose font based on target language
            var fontId = embeddedFonts.PrimaryFontId;
            var fontSize = block.FontSize > 0 ? block.FontSize : 10.0;

            // Generate text operations for this block
            var blockOps = GenerateBlockTextOperations(
                block.TranslatedText,
                fontId,
                fontSize,
                bbox,
                pdfPigPage.Height,
                embeddedFonts);

            opsText.Append(blockOps);
            rendered++;
        }

        // Step 4: Build and replace content stream
        if (rendered > 0)
        {
            var contentStream = ContentStreamInterpreter.BuildContentStream(
                opsBase, opsText.ToString());

            // Create a new xref for the content stream
            var xref = muDoc.GetNewXref();
            muDoc.UpdateObject(xref, "<<>>");
            muDoc.UpdateStream(xref, contentStream);
            muPage.SetContents(xref);
        }

        return rendered;
    }

    /// <summary>
    /// Generates PDF text operations for a translated block.
    /// Handles line wrapping and font size adjustment.
    /// </summary>
    private static string GenerateBlockTextOperations(
        string translatedText,
        string fontId,
        double fontSize,
        BlockRect bbox,
        double pageHeight,
        EmbeddedFontInfo fonts)
    {
        var sb = new StringBuilder();
        var lineHeight = fontSize * 1.2;

        // PDF coordinate system: origin at bottom-left, Y increases upward
        var startX = bbox.X;
        var startY = pageHeight - bbox.Y; // Convert from top-down to bottom-up
        var maxWidth = bbox.Width;
        var maxHeight = bbox.Height;

        // Simple line wrapping: split by existing newlines, then wrap long lines
        var lines = translatedText.Split('\n');
        var currentY = startY;
        var linesRendered = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Check if we've exceeded the block height
            if (linesRendered > 0 && (startY - currentY) > maxHeight)
                break;

            // Estimate character width (rough: fontSize * 0.5 for Latin, fontSize for CJK)
            var avgCharWidth = EstimateAverageCharWidth(line, fontSize);
            var charsPerLine = maxWidth > 0 ? (int)Math.Max(1, maxWidth / avgCharWidth) : line.Length;

            // Wrap line if needed
            var pos = 0;
            while (pos < line.Length)
            {
                var remaining = line.Length - pos;
                var count = Math.Min(remaining, charsPerLine);
                var segment = line.Substring(pos, count);

                // Generate PDF operators for this line segment
                foreach (var ch in segment)
                {
                    var charFontId = fontId;

                    // Check if character needs the Noto font (non-Latin, non-CJK)
                    if (fonts.NotoFontId is not null && NeedsNotoFont(ch))
                    {
                        charFontId = fonts.NotoFontId;
                    }

                    var hexCid = ((int)ch).ToString("X4");
                    sb.Append(ContentStreamInterpreter.GenerateTextOperator(
                        charFontId, fontSize, startX, currentY, hexCid));
                    startX += avgCharWidth;
                }

                startX = bbox.X; // Reset X for next line
                currentY -= lineHeight;
                pos += count;
                linesRendered++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Estimates average character width for line wrapping.
    /// </summary>
    private static double EstimateAverageCharWidth(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return fontSize * 0.5;

        // Count CJK vs Latin characters for width estimation
        var cjkCount = 0;
        var totalCount = 0;
        foreach (var ch in text)
        {
            if (ch > 0x2E7F) cjkCount++; // Rough CJK detection
            totalCount++;
        }

        if (totalCount == 0) return fontSize * 0.5;

        var cjkRatio = (double)cjkCount / totalCount;
        // CJK characters are roughly full-width, Latin roughly half-width
        return fontSize * (0.5 + cjkRatio * 0.5);
    }

    /// <summary>
    /// Checks if a character needs the Noto font (for non-Latin, non-CJK scripts).
    /// </summary>
    private static bool NeedsNotoFont(char ch)
    {
        // Arabic, Hebrew, Devanagari, Thai, etc.
        return ch is (>= '\u0600' and <= '\u06FF') // Arabic
            or (>= '\u0590' and <= '\u05FF') // Hebrew
            or (>= '\u0900' and <= '\u097F') // Devanagari
            or (>= '\u0E00' and <= '\u0E7F') // Thai
            or (>= '\u0400' and <= '\u04FF') // Cyrillic
            or (>= '\u10A0' and <= '\u10FF') // Georgian
            or (>= '\u1100' and <= '\u11FF'); // Hangul Jamo (supplementary)
    }

    /// <summary>
    /// Embeds required fonts into the MuPDF page.
    /// </summary>
    private static EmbeddedFontInfo EmbedFonts(MuPdfPage muPage, FontPaths fontPaths)
    {
        string? primaryFontId = null;
        string? notoFontId = null;

        // Embed primary font (CJK or Latin)
        if (fontPaths.PrimaryFontPath is not null && File.Exists(fontPaths.PrimaryFontPath))
        {
            try
            {
                var xref = muPage.InsertFont(fontPaths.PrimaryFontName, fontPaths.PrimaryFontPath);
                primaryFontId = fontPaths.PrimaryFontName;
                Debug.WriteLine($"[MuPdfExport] Embedded primary font: {fontPaths.PrimaryFontName} (xref={xref})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuPdfExport] Failed to embed primary font: {ex.Message}");
            }
        }

        // Fallback to built-in Helvetica if no custom font was embedded
        if (primaryFontId is null)
        {
            primaryFontId = "helv";
            try
            {
                muPage.InsertFont("helv", "");
            }
            catch (Exception)
            {
                // Font may already be registered on this page
            }
        }

        // Embed Noto font for non-CJK scripts
        if (fontPaths.NotoFontPath is not null && File.Exists(fontPaths.NotoFontPath))
        {
            try
            {
                var xref = muPage.InsertFont(NotoFontName, fontPaths.NotoFontPath);
                notoFontId = NotoFontName;
                Debug.WriteLine($"[MuPdfExport] Embedded Noto font (xref={xref})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuPdfExport] Failed to embed Noto font: {ex.Message}");
            }
        }

        return new EmbeddedFontInfo(primaryFontId, notoFontId);
    }

    /// <summary>
    /// Generates a bilingual (dual) PDF by interleaving original and translated pages.
    /// Matches pdf2zh's dual PDF output: original page 1, translated page 1, original page 2, ...
    /// </summary>
    private static void GenerateBilingualPdf(string sourcePath, string translatedPath, string outputPath)
    {
        var sourceDoc = new Document(sourcePath);
        var translatedDoc = new Document(translatedPath);
        try
        {
            var originalPageCount = sourceDoc.PageCount;

            // Insert all translated pages after the source document
            sourceDoc.InsertFile(translatedDoc);

            // Interleave: move translated pages to be after each original page
            // After insert, pages are: [orig1, orig2, ..., origN, trans1, trans2, ..., transN]
            // We want: [orig1, trans1, orig2, trans2, ...]
            for (var i = 0; i < originalPageCount; i++)
            {
                // The translated page at index (originalPageCount + i) needs to move to (2*i + 1)
                var fromIndex = originalPageCount + i;
                var toIndex = 2 * i + 1;
                if (fromIndex != toIndex)
                {
                    sourceDoc.MovePage(fromIndex, toIndex);
                }
            }

            sourceDoc.Save(outputPath);
            Debug.WriteLine($"[MuPdfExport] Bilingual PDF saved: {outputPath} ({sourceDoc.PageCount} pages)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuPdfExport] Bilingual PDF generation failed: {ex.Message}");
            // Fallback: copy the monolingual translated PDF
            File.Copy(translatedPath, outputPath, overwrite: true);
        }
        finally
        {
            sourceDoc.Close();
            translatedDoc.Close();
        }
    }

    /// <summary>
    /// Builds a lookup of page number → translated blocks from the checkpoint.
    /// Uses the same chunk-based structure as PdfExportService:
    /// ChunkMetadata[i] provides page number, bounding box, text style;
    /// TranslatedChunks[i] provides the translated text.
    /// </summary>
    private static Dictionary<int, List<TranslatedBlockData>> BuildTranslatedBlockLookup(
        LongDocumentTranslationCheckpoint checkpoint)
    {
        var result = new Dictionary<int, List<TranslatedBlockData>>();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        for (var i = 0; i < checkpoint.SourceChunks.Count; i++)
        {
            if (!metadataByChunkIndex.TryGetValue(i, out var metadata))
                continue;
            if (!checkpoint.TranslatedChunks.TryGetValue(i, out var translated)
                || string.IsNullOrWhiteSpace(translated))
                continue;

            var isFormulaSkipped = metadata.SourceBlockType == SourceBlockType.Formula
                || metadata.IsFormulaLike;

            var block = new TranslatedBlockData
            {
                TranslatedText = translated,
                BoundingBox = metadata.BoundingBox,
                FontSize = metadata.TextStyle?.FontSize ?? 10.0,
                TranslationSkipped = isFormulaSkipped,
            };

            if (!result.TryGetValue(metadata.PageNumber, out var list))
            {
                list = new List<TranslatedBlockData>();
                result[metadata.PageNumber] = list;
            }
            list.Add(block);
        }

        return result;
    }

    /// <summary>
    /// Resolves font file paths for embedding.
    /// Looks for downloaded CJK fonts and Noto fonts in the app's data directory.
    /// </summary>
    private static FontPaths ResolveFontPaths(Language? targetLanguage)
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict", "Fonts");

        string primaryName = "helv";
        string? primaryPath = null;
        string? notoPath = null;

        // Try to find CJK font
        if (targetLanguage.HasValue && CjkFontNames.TryGetValue(targetLanguage.Value, out var cjkName))
        {
            var cjkPath = Path.Combine(appDataPath, $"{cjkName}.otf");
            if (!File.Exists(cjkPath))
                cjkPath = Path.Combine(appDataPath, $"{cjkName}.ttf");

            if (File.Exists(cjkPath))
            {
                primaryName = cjkName;
                primaryPath = cjkPath;
            }
        }

        // Try to find Noto font for non-CJK scripts
        var notoNames = new[] { "GoNotoKurrent-Regular", "NotoSans-Regular", "NotoSerif-Regular" };
        foreach (var name in notoNames)
        {
            var path = Path.Combine(appDataPath, $"{name}.ttf");
            if (File.Exists(path))
            {
                notoPath = path;
                break;
            }
        }

        return new FontPaths(primaryName, primaryPath, notoPath);
    }

    // --- Internal types ---

    private sealed record TranslatedBlockData
    {
        public required string TranslatedText { get; init; }
        public BlockRect? BoundingBox { get; init; }
        public double FontSize { get; init; }
        public bool TranslationSkipped { get; init; }
    }

    private sealed record EmbeddedFontInfo(string PrimaryFontId, string? NotoFontId);

    private sealed record FontPaths(string PrimaryFontName, string? PrimaryFontPath, string? NotoFontPath);
}
