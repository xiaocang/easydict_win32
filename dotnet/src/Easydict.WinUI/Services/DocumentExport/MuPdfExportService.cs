// MuPDF.NET-based PDF export service for pdf2zh-aligned content stream replacement.
// Uses MuPDF.NET (official C# bindings) for font embedding, content stream replacement,
// XRef manipulation, and dual PDF output — matching pdf2zh's PyMuPDF-based pipeline.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
    /// All font IDs that represent CJK fonts — both SourceHanSerif and Noto CJK fallbacks.
    /// Used to decide whether to route ASCII through Helvetica (CJK fonts render ASCII full-width).
    /// </summary>
    private static readonly HashSet<string> CjkFontIds =
        new(CjkFontNames.Values, StringComparer.Ordinal)
        {
            "NotoSansSC-Regular",
            "NotoSansTC-Regular",
            "NotoSansJP-Regular",
            "NotoSansKR-Regular",
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
                embeddedFonts,
                block.TextStyle);

            opsText.Append(blockOps);
            rendered++;
        }

        // Step 4: Build and replace content stream
        if (rendered > 0)
        {
            // Generate white rectangle erasure ops to cover original content (including
            // Form XObject text) in all translated block areas, preventing bleed-through.
            var opsErase = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block.TranslationSkipped || string.IsNullOrWhiteSpace(block.TranslatedText) || block.BoundingBox is null)
                    continue;

                var bbox = block.BoundingBox.Value;
                // bbox.Y is the bottom-left Y in PDF bottom-up coords; use directly for 're' operator
                opsErase.Append($"1 1 1 rg {bbox.X:F6} {bbox.Y:F6} {bbox.Width:F6} {bbox.Height:F6} re f ");
            }

            var contentStream = ContentStreamInterpreter.BuildContentStream(
                opsBase, opsText.ToString(), eraseOps: opsErase.ToString());

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
    /// Handles line wrapping, font size adjustment, color, and ASCII/CJK font routing.
    /// </summary>
    private static string GenerateBlockTextOperations(
        string translatedText,
        string fontId,
        double fontSize,
        BlockRect bbox,
        EmbeddedFontInfo fonts,
        BlockTextStyle? textStyle = null)
    {
        // Simplify inline LaTeX formulas to plain text with ^ _ super/subscript signals
        translatedText = SimplifyLatexMarkup(translatedText);
        if (string.IsNullOrWhiteSpace(translatedText)) return string.Empty;

        var sb = new StringBuilder();
        var lineHeight = fontSize * 1.2;

        // PDF coordinate system: origin at bottom-left, Y increases upward.
        // bbox.Y is the BOTTOM of the block in PDF bottom-up coords.
        // Place the first baseline one font-size below the top edge so that
        // ascenders sit inside the box rather than protruding above it.
        var startX = bbox.X;
        var startY = bbox.Y + bbox.Height - fontSize;
        var maxWidth = bbox.Width;
        var maxHeight = bbox.Height;

        // Apply non-black color before block characters
        var hasColor = textStyle is not null && !textStyle.IsBlack;
        if (hasColor)
        {
            var r = textStyle!.ColorR / 255.0;
            var g = textStyle.ColorG / 255.0;
            var b = textStyle.ColorB / 255.0;
            sb.Append($"{r:F3} {g:F3} {b:F3} rg ");
        }

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
            // Declared outside the wrap loop so super/subscript signals survive across
            // segment boundaries (when the ^ or _ falls at the end of a wrapped segment).
            var superNext = false;
            var subNext = false;
            while (pos < line.Length)
            {
                var remaining = line.Length - pos;
                var count = Math.Min(remaining, charsPerLine);
                var segment = line.Substring(pos, count);

                // Generate PDF operators for this line segment.
                // ^ and _ are rendering signals left by SimplifyLatexMarkup:
                // the character immediately following them is raised/lowered as super/subscript.

                foreach (var ch in segment)
                {
                    // Consume ^ and _ as super/subscript signals (not rendered as characters)
                    if (ch == '^') { superNext = true; subNext = false; continue; }
                    if (ch == '_') { subNext = true; superNext = false; continue; }

                    // Apply super/subscript sizing and Y offset for this character only
                    var charFontSize = fontSize;
                    var charY = currentY;
                    if (superNext)
                    {
                        charFontSize = fontSize * 0.7;
                        charY = currentY + fontSize * 0.4;
                        superNext = false;
                    }
                    else if (subNext)
                    {
                        charFontSize = fontSize * 0.7;
                        charY = currentY - fontSize * 0.3;
                        subNext = false;
                    }

                    // Route basic ASCII when the primary font is CJK.
                    // CJK fonts map ASCII to full-width glyphs; we want half-width rendering.
                    if (fonts.PrimaryFontIsCjk && ch >= 0x20 && ch <= 0x7E)
                    {
                        if (ch == ' ')
                        {
                            // Emit no glyph — just advance by a proper space width.
                            // 0.55 × fontSize (old advance) was nearly 2× too wide; 0.3 is closer to Helvetica's 0.278 em.
                            startX += charFontSize * 0.3;
                            superNext = false;
                            subNext = false;
                            continue;
                        }

                        // Prefer the primary CJK font's half-width Latin glyph (visually matches CJK stroke weight).
                        // PrimaryGlyphMap covers the full BMP including ASCII, so this succeeds for NotoSansSC / SourceHanSerif.
                        if (fonts.PrimaryGlyphMap?.TryGetValue(ch, out var asciiGid) == true && asciiGid != 0)
                        {
                            sb.Append(ContentStreamInterpreter.GenerateTextOperator(
                                fontId, charFontSize, startX, charY, asciiGid.ToString("X4")));
                            startX += charFontSize * GetGlyphAdvanceEm(
                                asciiGid, fonts.PrimaryAdvanceWidths, fonts.PrimaryUnitsPerEm, fallbackEm: 0.55);
                        }
                        else
                        {
                            // Helv fallback: use 1-byte encoding (X2), not 4-byte (X4).
                            // Built-in Helvetica is a 1-byte-encoded Type1 font; <0041> is 2 bytes (0x00 + 'A').
                            sb.Append(ContentStreamInterpreter.GenerateTextOperator(
                                "helv", charFontSize, startX, charY, ((int)ch).ToString("X2")));
                            startX += charFontSize * 0.55; // helv metrics not loaded; 0.55 is a reasonable avg
                        }
                        continue;
                    }

                    var charFontId = fontId;

                    // Check if character needs the Noto font (non-Latin, non-CJK)
                    if (fonts.NotoFontId is not null && NeedsNotoFont(ch))
                        charFontId = fonts.NotoFontId;

                    // Encode the character for the content stream.
                    // MuPDF embeds fonts with Identity-H encoding: the char code in
                    // [<XXXX>] TJ must be the GID (glyph index), not the Unicode code point.
                    // This matches pdf2zh's raw_string() for the SourceHanSerif/Noto path.
                    string hexCid;
                    ushort resolvedGid = 0;
                    if (charFontId == fontId && fonts.PrimaryGlyphMap is not null)
                    {
                        if (!fonts.PrimaryGlyphMap.TryGetValue(ch, out resolvedGid) || resolvedGid == 0)
                            continue; // character not in font — skip (matches pdf2zh behavior)
                        hexCid = resolvedGid.ToString("X4");
                    }
                    else if (charFontId == fonts.NotoFontId && fonts.NotoGlyphMap is not null)
                    {
                        if (!fonts.NotoGlyphMap.TryGetValue(ch, out resolvedGid) || resolvedGid == 0)
                            continue; // character not in font — skip
                        hexCid = resolvedGid.ToString("X4");
                    }
                    else
                    {
                        // Built-in font (e.g., Helvetica fallback) — use Unicode code point directly
                        hexCid = ((int)ch).ToString("X4");
                    }

                    sb.Append(ContentStreamInterpreter.GenerateTextOperator(
                        charFontId, charFontSize, startX, charY, hexCid));
                    // CJK characters are always full-width (1 em).
                    // For Latin/other scripts, use per-glyph advance from hmtx when available
                    // to avoid letter-spacing of narrow glyphs (l, i, ., etc.).
                    if (IsCjkCharacter(ch))
                        startX += charFontSize;
                    else if (charFontId == fontId && resolvedGid != 0)
                        startX += charFontSize * GetGlyphAdvanceEm(
                            resolvedGid, fonts.PrimaryAdvanceWidths, fonts.PrimaryUnitsPerEm, fallbackEm: 0.6);
                    else if (charFontId == fonts.NotoFontId && resolvedGid != 0)
                        startX += charFontSize * GetGlyphAdvanceEm(
                            resolvedGid, fonts.NotoAdvanceWidths, fonts.NotoUnitsPerEm, fallbackEm: 0.6);
                    else
                        startX += charFontSize * 0.6;
                }

                startX = bbox.X; // Reset X for next line
                currentY -= lineHeight;
                pos += count;
                linesRendered++;
            }
        }

        // Reset color to black after block
        if (hasColor)
            sb.Append("0 0 0 rg ");

        return sb.ToString();
    }

    /// <summary>
    /// Simplifies LaTeX formula markup to plain text for PDF rendering.
    /// Preserves ^ and _ as rendering signals for super/subscript positioning.
    /// Unlike the old StripLatexMarkup, content between delimiters is kept (simplified),
    /// so inline math like "$h_t$" renders as "h" with subscript "t" rather than as blank.
    /// </summary>
    private static string SimplifyLatexMarkup(string text)
    {
        // Display math: $$...$$ → simplified content (surrounded by spaces)
        text = Regex.Replace(text, @"\$\$([\s\S]*?)\$\$",
            m => " " + SimplifyMathContent(m.Groups[1].Value) + " ");
        // Display math: \[...\] → simplified content
        text = Regex.Replace(text, @"\\\[([\s\S]*?)\\\]",
            m => " " + SimplifyMathContent(m.Groups[1].Value) + " ");
        // Inline math: $...$ → simplified content
        text = Regex.Replace(text, @"\$([^$\n]+)\$",
            m => SimplifyMathContent(m.Groups[1].Value));
        // Inline math: \(...\) → simplified content
        text = Regex.Replace(text, @"\\\(([\s\S]*?)\\\)",
            m => SimplifyMathContent(m.Groups[1].Value));
        // Residual \cmd{content} outside math → keep content
        text = Regex.Replace(text, @"\\[a-zA-Z]+\{([^}]*)\}", "$1");
        // Residual standalone \cmd → remove
        text = Regex.Replace(text, @"\\[a-zA-Z]+", string.Empty);
        // Expand _{abc} → _a_b_c  and  ^{abc} → ^a^b^c so that every character
        // inside a sub/superscript group gets its own rendering signal.
        // This handles multi-character subscripts produced by PdfTextLine.Normalize()
        // (e.g. h_{t-1} → h_t_-_1) so the renderer correctly positions each char.
        text = Regex.Replace(text, @"_\{([^}]*)\}",
            m => string.Concat(m.Groups[1].Value.Select(c => "_" + c)));
        text = Regex.Replace(text, @"\^\{([^}]*)\}",
            m => string.Concat(m.Groups[1].Value.Select(c => "^" + c)));
        // Remove lone $ \ { }; keep ^ _ as super/subscript rendering signals
        text = Regex.Replace(text, @"[\$\\{}]", string.Empty);
        // Collapse extra whitespace
        return Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
    }

    /// <summary>
    /// Simplifies LaTeX math content to an ASCII approximation.
    /// Handles common constructs: \frac, \sqrt, \text, \mathrm, etc.
    /// Preserves ^ and _ for super/subscript rendering signals.
    /// </summary>
    private static string SimplifyMathContent(string latex)
    {
        // \frac{a}{b} → a/b
        latex = Regex.Replace(latex, @"\\frac\{([^}]*)\}\{([^}]*)\}", "$1/$2");
        // \sqrt{x} → √x
        latex = Regex.Replace(latex, @"\\sqrt\{([^}]*)\}", "√$1");
        // \text{word} / \mathrm{word} / \mathbf{word} → word
        latex = Regex.Replace(latex, @"\\(?:text|mathrm|mathbf|mathit|operatorname)\{([^}]*)\}", "$1");
        // Other \cmd{content} → content
        latex = Regex.Replace(latex, @"\\[a-zA-Z]+\{([^}]*)\}", "$1");
        // Remove standalone \cmd (e.g. \alpha, \beta, \sum, \cdot)
        latex = Regex.Replace(latex, @"\\[a-zA-Z]+", string.Empty);
        // Expand _{abc} → _a_b_c and ^{abc} → ^a^b^c (per-character signals)
        latex = Regex.Replace(latex, @"_\{([^}]*)\}",
            m => string.Concat(m.Groups[1].Value.Select(c => "_" + c)));
        latex = Regex.Replace(latex, @"\^\{([^}]*)\}",
            m => string.Concat(m.Groups[1].Value.Select(c => "^" + c)));
        // Remove { } braces; keep ^ _ = + - * / spaces and alphanumerics
        latex = Regex.Replace(latex, @"[{}]", string.Empty);
        // Collapse whitespace
        return Regex.Replace(latex, @"\s+", " ").Trim();
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
        // Corrected formula: ASCII runs use ~0.55 em, CJK use ~1.0 em.
        // Weighted average = 0.55 + 0.45 × cjkRatio  (was 0.5 + 0.5 × cjkRatio)
        return fontSize * (0.55 + cjkRatio * 0.45);
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
    /// Returns the advance width (in em fractions) for a glyph, falling back to
    /// <paramref name="fallbackEm"/> when the font's hmtx data is unavailable.
    /// </summary>
    private static double GetGlyphAdvanceEm(
        ushort gid,
        IReadOnlyDictionary<ushort, ushort>? advanceWidths,
        ushort unitsPerEm,
        double fallbackEm)
    {
        if (advanceWidths is not null
            && advanceWidths.TryGetValue(gid, out var adv)
            && adv > 0
            && unitsPerEm > 0)
            return (double)adv / unitsPerEm;

        return fallbackEm;
    }

    /// <summary>
    /// Returns true for CJK-range characters that occupy a full em width.
    /// </summary>
    private static bool IsCjkCharacter(char ch) =>
        (ch >= '\u4E00' && ch <= '\u9FFF') ||  // CJK Unified Ideographs
        (ch >= '\u3400' && ch <= '\u4DBF') ||  // CJK Extension A
        (ch >= '\u3040' && ch <= '\u30FF') ||  // Hiragana + Katakana
        (ch >= '\uAC00' && ch <= '\uD7AF') ||  // Hangul Syllables
        (ch >= '\u2E80' && ch <= '\u2FFF') ||  // CJK Radicals, Symbols & Punctuation
        (ch >= '\uF900' && ch <= '\uFAFF');    // CJK Compatibility Ideographs

    /// <summary>
    /// Embeds required fonts into the MuPDF page.
    /// </summary>
    private static EmbeddedFontInfo EmbedFonts(MuPdfPage muPage, FontPaths fontPaths)
    {
        string? primaryFontId = null;
        string? notoFontId = null;
        IReadOnlyDictionary<char, ushort>? primaryGlyphMap = null;
        IReadOnlyDictionary<char, ushort>? notoGlyphMap = null;
        IReadOnlyDictionary<ushort, ushort>? primaryAdvanceWidths = null;
        ushort primaryUnitsPerEm = 1000;
        IReadOnlyDictionary<ushort, ushort>? notoAdvanceWidths = null;
        ushort notoUnitsPerEm = 1000;

        // Always embed Helvetica so it's available for ASCII characters
        // even when a CJK font is the primary (CJK fonts map ASCII to full-width glyphs)
        try { muPage.InsertFont("helv", ""); }
        catch (Exception) { /* Font may already be registered on this page */ }

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

            // Load GID map + advance widths for Identity-H encoding — only when embedded from file
            if (primaryFontId is not null)
            {
                var metrics = TrueTypeCmapParser.LoadFontMetrics(fontPaths.PrimaryFontPath);
                primaryGlyphMap = metrics?.GlyphMap;
                primaryAdvanceWidths = metrics?.AdvanceWidths;
                primaryUnitsPerEm = metrics?.UnitsPerEm ?? 1000;
            }
        }

        // Fallback to built-in Helvetica if no custom font was embedded
        if (primaryFontId is null)
            primaryFontId = "helv";

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

            if (notoFontId is not null)
            {
                var metrics = TrueTypeCmapParser.LoadFontMetrics(fontPaths.NotoFontPath);
                notoGlyphMap = metrics?.GlyphMap;
                notoAdvanceWidths = metrics?.AdvanceWidths;
                notoUnitsPerEm = metrics?.UnitsPerEm ?? 1000;
            }
        }

        var primaryFontIsCjk = CjkFontIds.Contains(primaryFontId);

        return new EmbeddedFontInfo(
            primaryFontId, notoFontId,
            primaryGlyphMap, notoGlyphMap,
            primaryFontIsCjk,
            primaryAdvanceWidths, primaryUnitsPerEm,
            notoAdvanceWidths, notoUnitsPerEm);
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

            var rotationAngle = metadata.TextStyle?.RotationAngle ?? 0;
            var isVertical = Math.Abs(rotationAngle) > 15.0;

            var block = new TranslatedBlockData
            {
                TranslatedText = translated,
                BoundingBox = metadata.BoundingBox,
                FontSize = metadata.TextStyle?.FontSize ?? 10.0,
                TranslationSkipped = isFormulaSkipped || isVertical,
                TextStyle = metadata.TextStyle,
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

        // Try to find CJK font — preference order:
        // 1. SourceHanSerif (preferred, matches pdf2zh)
        // 2. NotoSans CJK variant downloaded by FontDownloadService
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

        // Fallback: Noto CJK fonts downloaded by FontDownloadService
        if (primaryPath is null && targetLanguage.HasValue)
        {
            var notoVariant = targetLanguage.Value switch
            {
                Language.SimplifiedChinese  => "NotoSansSC-Regular",
                Language.TraditionalChinese => "NotoSansTC-Regular",
                Language.Japanese           => "NotoSansJP-Regular",
                Language.Korean             => "NotoSansKR-Regular",
                _                           => null,
            };
            if (notoVariant is not null)
            {
                var path = Path.Combine(appDataPath, $"{notoVariant}.ttf");
                if (File.Exists(path))
                {
                    primaryName = notoVariant;
                    primaryPath = path;
                }
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
        public BlockTextStyle? TextStyle { get; init; }
    }

    private sealed record EmbeddedFontInfo(
        string PrimaryFontId,
        string? NotoFontId,
        IReadOnlyDictionary<char, ushort>? PrimaryGlyphMap,
        IReadOnlyDictionary<char, ushort>? NotoGlyphMap,
        bool PrimaryFontIsCjk,
        IReadOnlyDictionary<ushort, ushort>? PrimaryAdvanceWidths = null,
        ushort PrimaryUnitsPerEm = 1000,
        IReadOnlyDictionary<ushort, ushort>? NotoAdvanceWidths = null,
        ushort NotoUnitsPerEm = 1000);

    private sealed record FontPaths(string PrimaryFontName, string? PrimaryFontPath, string? NotoFontPath);
}
