// MuPDF.NET-based PDF export service for pdf2zh-aligned content stream replacement.
// Uses MuPDF.NET (official C# bindings) for font embedding, content stream replacement,
// XRef manipulation, and dual PDF output — matching pdf2zh's PyMuPDF-based pipeline.

using System.Diagnostics;
using System.Text;
using Easydict.TextLayout;
using Easydict.TextLayout.Layout;
using Easydict.TextLayout.Preparation;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.FormulaProtection;
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

    private static readonly IReadOnlyDictionary<LatinFontKey, (string FontId, string FileName)> LatinSystemFonts =
        new Dictionary<LatinFontKey, (string FontId, string FileName)>
        {
            [new LatinFontKey(LatinFontFamily.Serif, LatinFontVariant.Regular)] = ("latserifr", "times.ttf"),
            [new LatinFontKey(LatinFontFamily.Serif, LatinFontVariant.Bold)] = ("latserifb", "timesbd.ttf"),
            [new LatinFontKey(LatinFontFamily.Serif, LatinFontVariant.Italic)] = ("latserifi", "timesi.ttf"),
            [new LatinFontKey(LatinFontFamily.Serif, LatinFontVariant.BoldItalic)] = ("latserifbi", "timesbi.ttf"),
            [new LatinFontKey(LatinFontFamily.Sans, LatinFontVariant.Regular)] = ("latsansr", "arial.ttf"),
            [new LatinFontKey(LatinFontFamily.Sans, LatinFontVariant.Bold)] = ("latsansb", "arialbd.ttf"),
            [new LatinFontKey(LatinFontFamily.Sans, LatinFontVariant.Italic)] = ("latsansi", "ariali.ttf"),
            [new LatinFontKey(LatinFontFamily.Sans, LatinFontVariant.BoldItalic)] = ("latsansbi", "arialbi.ttf"),
            [new LatinFontKey(LatinFontFamily.Mono, LatinFontVariant.Regular)] = ("latmonor", "consola.ttf"),
            [new LatinFontKey(LatinFontFamily.Mono, LatinFontVariant.Bold)] = ("latmonob", "consolab.ttf"),
            [new LatinFontKey(LatinFontFamily.Mono, LatinFontVariant.Italic)] = ("latmonoi", "consolai.ttf"),
            [new LatinFontKey(LatinFontFamily.Mono, LatinFontVariant.BoldItalic)] = ("latmonobi", "consolabi.ttf"),
        };

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
                block.TextStyle,
                block.SourceBlockType,
                block.UsesSourceFallback,
                block.DetectedFontNames);

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
    /// Uses TextLayout engine for line breaking, then renders each character with
    /// font routing, super/subscript signals, and GID encoding.
    /// </summary>
    internal static string GenerateBlockTextOperations(
        string translatedText,
        string fontId,
        double fontSize,
        BlockRect bbox,
        EmbeddedFontInfo fonts,
        BlockTextStyle? textStyle = null,
        SourceBlockType sourceBlockType = SourceBlockType.Paragraph,
        bool usesSourceFallback = false,
        IReadOnlyList<string>? detectedFontNames = null)
    {
        // Simplify inline LaTeX formulas to plain text with ^ _ super/subscript signals
        translatedText = SimplifyLatexMarkup(translatedText);
        if (string.IsNullOrWhiteSpace(translatedText)) return string.Empty;

        var renderFont = ResolveRenderFontPlan(
            translatedText,
            fontId,
            fonts,
            sourceBlockType,
            usesSourceFallback,
            detectedFontNames,
            textStyle);
        var sb = new StringBuilder();
        var lineHeight = usesSourceFallback && textStyle?.LineSpacing > 0
            ? textStyle.LineSpacing
            : fontSize * 1.2;

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

        // Use TextLayout engine for line breaking.
        // GlyphAdvanceMeasurer returns 0 for ^ and _ signals so they don't affect layout.
        var engine = TextLayoutEngine.Instance;
        var measurer = new GlyphAdvanceMeasurer(
            renderFont.PrimaryFace.GlyphMap, renderFont.PrimaryFace.AdvanceWidths, renderFont.PrimaryFace.UnitsPerEm,
            fonts.NotoGlyphMap, fonts.NotoAdvanceWidths, fonts.NotoUnitsPerEm,
            renderFont.PrimaryIsCjk,
            fontSize,
            renderFont.LatinFace?.GlyphMap,
            renderFont.LatinFace?.AdvanceWidths,
            renderFont.LatinFace?.UnitsPerEm ?? 1000);
        var prepared = engine.Prepare(
            new TextPrepareRequest { Text = translatedText, NormalizeWhitespace = false },
            measurer);

        // Incremental layout: one line at a time, emit PDF operators per line
        var cursor = LayoutCursor.Start;
        var currentY = startY;
        var linesRendered = 0;

        while (true)
        {
            // Check if we've exceeded the block height
            if (linesRendered > 0 && (startY - currentY) > maxHeight)
                break;

            var layoutLine = engine.LayoutNextLine(prepared, cursor, maxWidth);
            if (layoutLine is null)
                break;

            var lineText = layoutLine.Text;
            var currentX = startX;
            StringBuilder? runHex = null;
            string? runFontId = null;
            double runFontSize = 0;
            double runY = 0;
            double runAdvance = 0;

            void FlushRun()
            {
                if (runFontId is null || runHex is null || runHex.Length == 0)
                    return;

                sb.Append(ContentStreamInterpreter.GenerateTextOperator(
                    runFontId,
                    runFontSize,
                    currentX,
                    runY,
                    runHex.ToString()));

                currentX += runAdvance;
                runFontId = null;
                runFontSize = 0;
                runY = 0;
                runAdvance = 0;
                runHex.Clear();
            }

            void AppendGlyphRun(ResolvedGlyph glyph, double glyphFontSize, double glyphY)
            {
                if (runFontId is not null &&
                    string.Equals(runFontId, glyph.FontId, StringComparison.Ordinal) &&
                    Math.Abs(runFontSize - glyphFontSize) < 0.001 &&
                    Math.Abs(runY - glyphY) < 0.001)
                {
                    runHex!.Append(glyph.Hex);
                    runAdvance += glyph.Advance;
                    return;
                }

                FlushRun();

                runHex ??= new StringBuilder();
                runHex.Clear();
                runHex.Append(glyph.Hex);
                runFontId = glyph.FontId;
                runFontSize = glyphFontSize;
                runY = glyphY;
                runAdvance = glyph.Advance;
            }

            // Render each character in the laid-out line
            var superNext = false;
            var subNext = false;
            foreach (var ch in lineText)
            {
                // Consume ^ and _ as super/subscript signals (not rendered as characters)
                if (LatexFormulaSimplifier.IsScriptSignal(ch))
                {
                    if (ch == '^') { superNext = true; subNext = false; }
                    else { subNext = true; superNext = false; }
                    continue;
                }

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

                if (!TryResolveGlyph(
                        ch,
                        charFontSize,
                        renderFont,
                        fonts,
                        out var glyph))
                {
                    FlushRun();
                    continue;
                }

                AppendGlyphRun(glyph, charFontSize, charY);
            }

            FlushRun();
            currentY -= lineHeight;
            linesRendered++;

            // Advance cursor to next line
            cursor = new LayoutCursor(layoutLine.EndSegment, layoutLine.EndGrapheme);
        }

        // Reset color to black after block
        if (hasColor)
            sb.Append("0 0 0 rg ");

        return sb.ToString();
    }

    /// <summary>
    /// Simplifies LaTeX formula markup to plain text for PDF rendering.
    /// Preserves ^ and _ as rendering signals for super/subscript positioning.
    /// Delegates to <see cref="LatexFormulaSimplifier.Simplify"/> for Unicode symbol mapping.
    /// </summary>
    private static string SimplifyLatexMarkup(string text) =>
        LatexFormulaSimplifier.Simplify(text, preserveScriptSignals: true);

    /// <summary>
    /// Simplifies LaTeX math content to a Unicode approximation.
    /// Handles common constructs: \frac, \sqrt, Greek letters, math operators, etc.
    /// Preserves ^ and _ for super/subscript rendering signals.
    /// Delegates to <see cref="LatexFormulaSimplifier.SimplifyMathContent"/>.
    /// </summary>
    private static string SimplifyMathContent(string latex) =>
        LatexFormulaSimplifier.SimplifyMathContent(latex, preserveScriptSignals: true);

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

    private readonly record struct RenderFontPlan(
        EmbeddedFontFace PrimaryFace,
        EmbeddedFontFace? LatinFace,
        bool PrimaryIsCjk,
        bool UseHelveticaAsciiFallback);

    private readonly record struct ResolvedGlyph(
        string FontId,
        string Hex,
        double Advance);

    private static RenderFontPlan ResolveRenderFontPlan(
        string text,
        string defaultFontId,
        EmbeddedFontInfo fonts,
        SourceBlockType sourceBlockType,
        bool usesSourceFallback,
        IReadOnlyList<string>? detectedFontNames,
        BlockTextStyle? textStyle)
    {
        var primaryFace = new EmbeddedFontFace(
            defaultFontId,
            fonts.PrimaryGlyphMap,
            fonts.PrimaryAdvanceWidths,
            fonts.PrimaryUnitsPerEm);

        if (usesSourceFallback &&
            IsLatinDominant(text) &&
            TrySelectLatinFallbackFace(fonts, detectedFontNames, sourceBlockType, textStyle, out var latinFace))
        {
            return new RenderFontPlan(
                latinFace,
                latinFace,
                PrimaryIsCjk: false,
                UseHelveticaAsciiFallback: false);
        }

        if (fonts.PrimaryFontIsCjk &&
            TrySelectLatinFallbackFace(fonts, detectedFontNames, sourceBlockType, textStyle, out var inlineLatinFace))
        {
            return new RenderFontPlan(
                primaryFace,
                inlineLatinFace,
                PrimaryIsCjk: true,
                UseHelveticaAsciiFallback: false);
        }

        return new RenderFontPlan(
            primaryFace,
            null,
            PrimaryIsCjk: fonts.PrimaryFontIsCjk,
            UseHelveticaAsciiFallback: fonts.PrimaryFontIsCjk);
    }

    private static bool TryResolveGlyph(
        char ch,
        double charFontSize,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        out ResolvedGlyph glyph)
    {
        glyph = default;

        if (ShouldUseLatinFaceForAscii(ch, renderFont) &&
            renderFont.LatinFace is not null)
        {
            if (TryResolveFaceGlyph(
                    ch,
                    charFontSize,
                    renderFont.LatinFace,
                    ch == ' ' ? GlyphAdvanceMeasurer.SpaceAdvanceEm : 0.6,
                    out glyph))
            {
                return true;
            }

            if (renderFont.PrimaryIsCjk && IsAscii(ch))
            {
                glyph = CreateHelveticaGlyph(ch, charFontSize);
                return true;
            }
        }

        if (renderFont.UseHelveticaAsciiFallback && IsAscii(ch))
        {
            glyph = CreateHelveticaGlyph(ch, charFontSize);
            return true;
        }

        if (fonts.NotoFontId is not null && NeedsNotoFont(ch))
        {
            var notoFace = new EmbeddedFontFace(
                fonts.NotoFontId,
                fonts.NotoGlyphMap,
                fonts.NotoAdvanceWidths,
                fonts.NotoUnitsPerEm);

            return TryResolveFaceGlyph(ch, charFontSize, notoFace, 0.6, out glyph);
        }

        return TryResolveFaceGlyph(
            ch,
            charFontSize,
            renderFont.PrimaryFace,
            IsCjkCharacter(ch) ? 1.0 : 0.6,
            out glyph);
    }

    private static bool TryResolveFaceGlyph(
        char ch,
        double charFontSize,
        EmbeddedFontFace face,
        double fallbackEm,
        out ResolvedGlyph glyph)
    {
        glyph = default;

        if (face.GlyphMap is not null)
        {
            if (!face.GlyphMap.TryGetValue(ch, out var gid) || gid == 0)
                return false;

            glyph = new ResolvedGlyph(
                face.FontId,
                gid.ToString("X4"),
                charFontSize * GetGlyphAdvanceEm(gid, face.AdvanceWidths, face.UnitsPerEm, fallbackEm));
            return true;
        }

        if (string.Equals(face.FontId, "helv", StringComparison.Ordinal))
        {
            glyph = CreateHelveticaGlyph(ch, charFontSize);
            return true;
        }

        glyph = new ResolvedGlyph(
            face.FontId,
            ((int)ch).ToString("X4"),
            charFontSize * (IsCjkCharacter(ch) ? 1.0 : fallbackEm));
        return true;
    }

    private static ResolvedGlyph CreateHelveticaGlyph(char ch, double charFontSize) =>
        new(
            "helv",
            ((int)ch).ToString("X2"),
            charFontSize * (ch == ' '
                ? GlyphAdvanceMeasurer.SpaceAdvanceEm
                : GlyphAdvanceMeasurer.CjkPrimaryAsciiAdvanceEm));

    private static bool ShouldUseLatinFaceForAscii(char ch, RenderFontPlan renderFont) =>
        renderFont.LatinFace is not null && IsAscii(ch);

    private static bool IsAscii(char ch) => ch >= 0x20 && ch <= 0x7E;

    private static bool TrySelectLatinFallbackFace(
        EmbeddedFontInfo fonts,
        IReadOnlyList<string>? detectedFontNames,
        SourceBlockType sourceBlockType,
        BlockTextStyle? textStyle,
        out EmbeddedFontFace fontFace)
    {
        fontFace = null!;
        if (fonts.LatinFontFaces is null || fonts.LatinFontFaces.Count == 0)
            return false;

        var family = ChooseLatinFallbackFamily(detectedFontNames, sourceBlockType);
        var variant = GetLatinFontVariant(textStyle);
        return TryGetLatinFace(fonts.LatinFontFaces, family, variant, out fontFace);
    }

    private static bool TryGetLatinFace(
        IReadOnlyDictionary<LatinFontKey, EmbeddedFontFace> faces,
        LatinFontFamily family,
        LatinFontVariant variant,
        out EmbeddedFontFace fontFace)
    {
        if (faces.TryGetValue(new LatinFontKey(family, variant), out fontFace!))
            return true;

        if (faces.TryGetValue(new LatinFontKey(family, LatinFontVariant.Regular), out fontFace!))
            return true;

        if (variant == LatinFontVariant.BoldItalic)
        {
            if (faces.TryGetValue(new LatinFontKey(family, LatinFontVariant.Bold), out fontFace!))
                return true;
            if (faces.TryGetValue(new LatinFontKey(family, LatinFontVariant.Italic), out fontFace!))
                return true;
        }

        return false;
    }

    private static LatinFontVariant GetLatinFontVariant(BlockTextStyle? textStyle) =>
        (textStyle?.IsBold == true, textStyle?.IsItalic == true) switch
        {
            (true, true) => LatinFontVariant.BoldItalic,
            (true, false) => LatinFontVariant.Bold,
            (false, true) => LatinFontVariant.Italic,
            _ => LatinFontVariant.Regular
        };

    private static LatinFontFamily ChooseLatinFallbackFamily(
        IReadOnlyList<string>? detectedFontNames,
        SourceBlockType sourceBlockType)
    {
        if (detectedFontNames is not null)
        {
            foreach (var fontName in detectedFontNames)
            {
                if (string.IsNullOrWhiteSpace(fontName))
                    continue;

                if (LooksLikeMonoFont(fontName))
                    return LatinFontFamily.Mono;
                if (LooksLikeSansFont(fontName))
                    return LatinFontFamily.Sans;
                if (LooksLikeSerifFont(fontName))
                    return LatinFontFamily.Serif;
            }
        }

        return sourceBlockType switch
        {
            SourceBlockType.Heading or SourceBlockType.Caption => LatinFontFamily.Sans,
            SourceBlockType.Formula or SourceBlockType.TableCell => LatinFontFamily.Mono,
            _ => LatinFontFamily.Serif
        };
    }

    private static bool LooksLikeSerifFont(string fontName) =>
        fontName.Contains("Times", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Roman", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Serif", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("CMR", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("NimbusRom", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSansFont(string fontName) =>
        fontName.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Arial", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Sans", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Gothic", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeMonoFont(string fontName) =>
        fontName.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Mono", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Typewriter", StringComparison.OrdinalIgnoreCase);

    private static bool IsLatinDominant(string text)
    {
        var latinLetters = 0;
        var cjkLetters = 0;
        var otherLetters = 0;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsDigit(ch))
                continue;

            if (IsCjkCharacter(ch))
            {
                cjkLetters++;
                continue;
            }

            if (char.IsLetter(ch))
            {
                if (ch <= '\u024F')
                    latinLetters++;
                else
                    otherLetters++;
            }
        }

        return latinLetters > 0 && latinLetters >= cjkLetters + otherLetters;
    }

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
        var latinFontFaces = new Dictionary<LatinFontKey, EmbeddedFontFace>();

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

        foreach (var (key, fontInfo) in LatinSystemFonts)
        {
            TryEmbedLatinSystemFont(muPage, latinFontFaces, key, fontInfo.FontId, fontInfo.FileName);
        }

        return new EmbeddedFontInfo(
            primaryFontId, notoFontId,
            primaryGlyphMap, notoGlyphMap,
            primaryFontIsCjk,
            primaryAdvanceWidths, primaryUnitsPerEm,
            notoAdvanceWidths, notoUnitsPerEm,
            latinFontFaces);
    }

    private static void TryEmbedLatinSystemFont(
        MuPdfPage muPage,
        IDictionary<LatinFontKey, EmbeddedFontFace> latinFontFaces,
        LatinFontKey key,
        string fontId,
        string fileName)
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var fontPath = Path.Combine(fontsDir, fileName);
        if (!File.Exists(fontPath))
            return;

        try
        {
            var xref = muPage.InsertFont(fontId, fontPath);
            Debug.WriteLine($"[MuPdfExport] Embedded Latin fallback font: {fontId} (xref={xref})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuPdfExport] Failed to embed Latin fallback font '{fontId}': {ex.Message}");
            return;
        }

        var metrics = TrueTypeCmapParser.LoadFontMetrics(fontPath);
        latinFontFaces[key] = new EmbeddedFontFace(
            fontId,
            metrics?.GlyphMap,
            metrics?.AdvanceWidths,
            metrics?.UnitsPerEm ?? 1000);
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
    internal static Dictionary<int, List<TranslatedBlockData>> BuildTranslatedBlockLookup(
        LongDocumentTranslationCheckpoint checkpoint)
    {
        var result = new Dictionary<int, List<TranslatedBlockData>>();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        for (var i = 0; i < checkpoint.SourceChunks.Count; i++)
        {
            if (!metadataByChunkIndex.TryGetValue(i, out var metadata))
                continue;
            if (!PdfExportCheckpointTextResolver.TryGetRenderableText(
                    checkpoint,
                    i,
                    out var translated,
                    out var usesSourceFallback))
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
                SourceBlockType = metadata.SourceBlockType,
                UsesSourceFallback = usesSourceFallback,
                DetectedFontNames = metadata.DetectedFontNames,
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

    internal sealed record TranslatedBlockData
    {
        public required string TranslatedText { get; init; }
        public BlockRect? BoundingBox { get; init; }
        public double FontSize { get; init; }
        public bool TranslationSkipped { get; init; }
        public BlockTextStyle? TextStyle { get; init; }
        public SourceBlockType SourceBlockType { get; init; }
        public bool UsesSourceFallback { get; init; }
        public IReadOnlyList<string>? DetectedFontNames { get; init; }
    }

    internal readonly record struct LatinFontKey(LatinFontFamily Family, LatinFontVariant Variant);

    internal sealed record EmbeddedFontFace(
        string FontId,
        IReadOnlyDictionary<char, ushort>? GlyphMap,
        IReadOnlyDictionary<ushort, ushort>? AdvanceWidths,
        ushort UnitsPerEm);

    internal sealed record EmbeddedFontInfo(
        string PrimaryFontId,
        string? NotoFontId,
        IReadOnlyDictionary<char, ushort>? PrimaryGlyphMap,
        IReadOnlyDictionary<char, ushort>? NotoGlyphMap,
        bool PrimaryFontIsCjk,
        IReadOnlyDictionary<ushort, ushort>? PrimaryAdvanceWidths = null,
        ushort PrimaryUnitsPerEm = 1000,
        IReadOnlyDictionary<ushort, ushort>? NotoAdvanceWidths = null,
        ushort NotoUnitsPerEm = 1000,
        IReadOnlyDictionary<LatinFontKey, EmbeddedFontFace>? LatinFontFaces = null);

    internal enum LatinFontFamily
    {
        Serif,
        Sans,
        Mono
    }

    internal enum LatinFontVariant
    {
        Regular,
        Bold,
        Italic,
        BoldItalic
    }

    private sealed record FontPaths(string PrimaryFontName, string? PrimaryFontPath, string? NotoFontPath);
}
