// MuPDF.NET-based PDF export service for pdf2zh-aligned content stream replacement.
// Uses MuPDF.NET (official C# bindings) for font embedding, content stream replacement,
// XRef manipulation, and dual PDF output — matching pdf2zh's PyMuPDF-based pipeline.

using System.Diagnostics;
using System.Text;
using Easydict.TextLayout;
using Easydict.TextLayout.FontFitting;
using Easydict.TextLayout.Layout;
using Easydict.TextLayout.Preparation;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.Models;
using MuPDF.NET;
using PdfSharpCore.Drawing;
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
    private const double MinFontSize = 6.0;

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
        var totalMissingBoundingBoxes = 0;
        var totalShrinkFontBlocks = 0;
        var totalTruncatedBlocks = 0;
        var pageMetrics = new Dictionary<int, BackfillPageMetrics>();
        var blockIssues = new List<BackfillBlockIssue>();

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
                var pageResult = RenderPageWithContentStreamReplacement(
                    pdfPigPage, muPage, muDoc, blocks, fontPaths);
                totalRendered += pageResult.RenderedBlocks;
                totalCandidates += blocks.Count;
                totalMissingBoundingBoxes += pageResult.MissingBoundingBoxBlocks;
                totalShrinkFontBlocks += pageResult.ShrinkFontBlocks;
                totalTruncatedBlocks += pageResult.TruncatedBlocks;
                pageMetrics[pageNumber] = new BackfillPageMetrics
                {
                    CandidateBlocks = blocks.Count,
                    RenderedBlocks = pageResult.RenderedBlocks,
                    MissingBoundingBoxBlocks = pageResult.MissingBoundingBoxBlocks,
                    ShrinkFontBlocks = pageResult.ShrinkFontBlocks,
                    TruncatedBlocks = pageResult.TruncatedBlocks,
                    ObjectReplaceBlocks = 0,
                    OverlayModeBlocks = 0,
                    StructuredFallbackBlocks = 0,
                };
                if (pageResult.BlockIssues.Count > 0)
                    blockIssues.AddRange(pageResult.BlockIssues);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuPdfExport] Page {pageNumber} failed: {ex.Message}");
                totalCandidates += blocks.Count;
                pageMetrics[pageNumber] = new BackfillPageMetrics
                {
                    CandidateBlocks = blocks.Count,
                    RenderedBlocks = 0,
                    MissingBoundingBoxBlocks = 0,
                    ShrinkFontBlocks = 0,
                    TruncatedBlocks = 0,
                    ObjectReplaceBlocks = 0,
                    OverlayModeBlocks = 0,
                    StructuredFallbackBlocks = 0,
                };
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
            MissingBoundingBoxBlocks = totalMissingBoundingBoxes,
            ShrinkFontBlocks = totalShrinkFontBlocks,
            TruncatedBlocks = totalTruncatedBlocks,
            ObjectReplaceBlocks = 0,
            OverlayModeBlocks = 0,
            StructuredFallbackBlocks = 0,
            PageMetrics = pageMetrics.Count > 0 ? pageMetrics : null,
            BlockIssues = blockIssues.Count > 0 ? blockIssues : null,
        };
    }

    /// <summary>
    /// Renders a single page by replacing its content stream with translated text.
    /// </summary>
    private PageRenderResult RenderPageWithContentStreamReplacement(
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
        var pageHeightPoints = Convert.ToDouble(pdfPigPage.Height);

        // Step 3: Pre-compute line-aware render geometry
        var preparedBlocks = blocks
            .Select(block => block.BoundingBox is null
                ? block
                : PrepareBlockForRendering(block, pageHeightPoints))
            .ToList();
        var pagePlan = BuildPageRenderPlan(
            preparedBlocks,
            pageHeightPoints,
            embeddedFonts.PrimaryFontId,
            embeddedFonts);

        // Step 4: Generate translated text operations
        var opsText = new StringBuilder();
        var opsErase = new StringBuilder();
        var rendered = 0;
        var missingBoundingBoxes = 0;
        var shrinkFontBlocks = 0;
        var truncatedBlocks = 0;
        var blockIssues = new List<BackfillBlockIssue>();

        foreach (var plannedBlock in pagePlan)
        {
            var block = plannedBlock.Block;
            if (block.TranslationSkipped || string.IsNullOrWhiteSpace(block.TranslatedText))
                continue;

            var effectiveBoundingBox = plannedBlock.LayoutBoundingBox ?? block.BoundingBox;
            if (effectiveBoundingBox is null)
            {
                missingBoundingBoxes++;
                continue;
            }

            var bbox = effectiveBoundingBox.Value;

            // Choose font based on target language
            var fontId = embeddedFonts.PrimaryFontId;
            var fontSize = block.FontSize > 0 ? block.FontSize : 10.0;

            var blockRenderResult = plannedBlock.PlannedOperations is not null
                ? RenderPlannedBlockTextOperations(plannedBlock)
                : GenerateBlockTextOperations(
                    block.TranslatedText,
                    fontId,
                    fontSize,
                    bbox,
                    embeddedFonts,
                    block.TextStyle,
                    block.SourceBlockType,
                    block.UsesSourceFallback,
                    block.DetectedFontNames,
                    plannedBlock.LayoutRenderLineRects,
                    plannedBlock.LayoutBackgroundLineRects);

            if (string.IsNullOrWhiteSpace(blockRenderResult.Operations))
                continue;

            opsText.Append(blockRenderResult.Operations);
            AppendEraseOperations(opsErase, block, embeddedFonts.PrimaryFontIsCjk, plannedBlock.EraseRects);
            rendered++;

            if (blockRenderResult.WasShrunk)
            {
                shrinkFontBlocks++;
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = block.ChunkIndex,
                    SourceBlockId = block.SourceBlockId,
                    PageNumber = block.PageNumber,
                    Kind = "shrink-font",
                    Detail = $"Font shrunk from {fontSize:F1}pt to {blockRenderResult.ChosenFontSize:F1}pt"
                });
            }

            if (blockRenderResult.WasTruncated)
            {
                truncatedBlocks++;
                blockIssues.Add(new BackfillBlockIssue
                {
                    ChunkIndex = block.ChunkIndex,
                    SourceBlockId = block.SourceBlockId,
                    PageNumber = block.PageNumber,
                    Kind = "truncated",
                    Detail = $"Rendered {blockRenderResult.LinesRendered} lines at {blockRenderResult.ChosenFontSize:F1}pt"
                });
            }
        }

        // Step 5: Build and replace content stream
        if (rendered > 0)
        {
            var contentStream = ContentStreamInterpreter.BuildContentStream(
                opsBase, opsText.ToString(), eraseOps: opsErase.ToString());

            // Create a new xref for the content stream
            var xref = muDoc.GetNewXref();
            muDoc.UpdateObject(xref, "<<>>");
            muDoc.UpdateStream(xref, contentStream);
            muPage.SetContents(xref);
        }

        return new PageRenderResult(
            rendered,
            missingBoundingBoxes,
            shrinkFontBlocks,
            truncatedBlocks,
            blockIssues);
    }

    /// <summary>
    /// Generates PDF text operations for a translated block.
    /// Uses TextLayout engine for line breaking, then renders each character with
    /// font routing, super/subscript signals, and GID encoding.
    /// </summary>
    internal static BlockTextRenderResult GenerateBlockTextOperations(
        string translatedText,
        string fontId,
        double fontSize,
        BlockRect bbox,
        EmbeddedFontInfo fonts,
        BlockTextStyle? textStyle = null,
        SourceBlockType sourceBlockType = SourceBlockType.Paragraph,
        bool usesSourceFallback = false,
        IReadOnlyList<string>? detectedFontNames = null,
        IReadOnlyList<BlockRect>? renderLineRects = null,
        IReadOnlyList<BlockRect>? backgroundLineRects = null)
    {
        var renderableText = PrepareRenderableTextForPdf(translatedText);
        if (string.IsNullOrWhiteSpace(renderableText))
            return new BlockTextRenderResult(string.Empty, fontSize, 0, WasShrunk: false, WasTruncated: false);

        var renderFont = ResolveRenderFontPlan(
            renderableText,
            fontId,
            fonts,
            sourceBlockType,
            usesSourceFallback,
            detectedFontNames,
            textStyle);

        var originalFontSize = fontSize > 0 ? fontSize : 10.0;
        var baseLineHeight = usesSourceFallback && textStyle?.LineSpacing > 0
            ? textStyle.LineSpacing
            : originalFontSize * 1.2;
        var lineHeightMultiplier = originalFontSize > 0
            ? Math.Max(1.0, baseLineHeight / originalFontSize)
            : 1.2;
        var availableHeight = ResolveAvailableHeight(bbox, renderLineRects, backgroundLineRects);

        var fitResult = SolveFontFit(
            renderableText,
            originalFontSize,
            bbox,
            renderLineRects,
            availableHeight,
            renderFont,
            fonts,
            lineHeightMultiplier);

        var chosenFontSize = fitResult.ChosenFontSize;
        var prepared = PrepareLayoutParagraph(renderableText, renderFont, fonts, chosenFontSize);
        var wrappedLines = LayoutLines(prepared, renderLineRects, bbox.Width).Select(l => l.Text).ToList();

        var lineHeight = fitResult.ChosenLineHeight;
        if (renderLineRects is not { Count: > 0 })
        {
            var totalTextHeight = wrappedLines.Count * lineHeight;
            while (totalTextHeight > bbox.Height && lineHeight > chosenFontSize)
            {
                lineHeight -= 0.05 * chosenFontSize;
                totalTextHeight = wrappedLines.Count * lineHeight;
            }

            lineHeight = Math.Max(lineHeight, chosenFontSize);
        }

        var maxVisibleLines = renderLineRects is { Count: > 0 }
            ? renderLineRects.Count
            : Math.Max(1, (int)Math.Floor(bbox.Height / Math.Max(chosenFontSize, lineHeight)));
        var wasTruncated = fitResult.WasTruncated;
        if (wrappedLines.Count > maxVisibleLines)
        {
            wrappedLines = wrappedLines.Take(maxVisibleLines).ToList();
            var lastWidth = renderLineRects is { Count: > 0 }
                ? renderLineRects[Math.Min(maxVisibleLines, renderLineRects.Count) - 1].Width
                : bbox.Width;
            wrappedLines[^1] = TruncateLineToFitWidth(
                wrappedLines[^1],
                lastWidth,
                renderFont,
                fonts,
                chosenFontSize);
            wasTruncated = true;
        }

        if (wrappedLines.Count == 0)
            return new BlockTextRenderResult(string.Empty, chosenFontSize, 0, WasShrunk: fitResult.WasShrunk, WasTruncated: wasTruncated);

        var operations = BuildBlockTextOperationsFromLines(
            wrappedLines,
            chosenFontSize,
            renderFont,
            fonts,
            textStyle,
            bbox,
            renderLineRects,
            lineHeight);

        return new BlockTextRenderResult(
            operations,
            chosenFontSize,
            wrappedLines.Count,
            WasShrunk: chosenFontSize < originalFontSize - 0.01,
            WasTruncated: wasTruncated);
    }

    internal static BlockTextRenderResult RenderPlannedBlockTextOperations(
        PlannedPageBlock plannedBlock)
    {
        if (plannedBlock.PlannedOperations is null)
            throw new InvalidOperationException("Planned retry-page block has no precomputed operations.");

        return new BlockTextRenderResult(
            plannedBlock.PlannedOperations,
            plannedBlock.PlannedChosenFontSize,
            plannedBlock.PlannedLinesRendered,
            plannedBlock.PlannedWasShrunk,
            plannedBlock.PlannedWasTruncated);
    }

    private static string BuildBlockTextOperationsFromLines(
        IReadOnlyList<string> lines,
        double chosenFontSize,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        BlockTextStyle? textStyle,
        BlockRect bbox,
        IReadOnlyList<BlockRect>? renderLineRects,
        double lineHeight)
    {
        var sb = new StringBuilder();

        var hasColor = textStyle is not null && !textStyle.IsBlack;
        if (hasColor)
        {
            var r = textStyle!.ColorR / 255.0;
            var g = textStyle.ColorG / 255.0;
            var b = textStyle.ColorB / 255.0;
            sb.Append($"{r:F3} {g:F3} {b:F3} rg ");
        }

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var lineText = lines[lineIndex];
            if (string.IsNullOrEmpty(lineText))
                continue;

            double startX;
            double baselineY;
            if (renderLineRects is { Count: > 0 } && lineIndex < renderLineRects.Count)
            {
                var rect = renderLineRects[lineIndex];
                startX = rect.X;
                baselineY = rect.Y + rect.Height - chosenFontSize;
            }
            else
            {
                startX = bbox.X;
                baselineY = bbox.Y + bbox.Height - chosenFontSize - lineIndex * lineHeight;
            }

            AppendLineTextOperations(
                sb,
                lineText,
                startX,
                baselineY,
                chosenFontSize,
                renderFont,
                fonts);
        }

        if (hasColor)
            sb.Append("0 0 0 rg ");

        return sb.ToString();
    }

    private static FontFitResult SolveFontFit(
        string translatedText,
        double fontSize,
        BlockRect bbox,
        IReadOnlyList<BlockRect>? renderLineRects,
        double availableHeight,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        double lineHeightMultiplier)
    {
        var request = new FontFitRequest
        {
            Text = translatedText,
            StartFontSize = fontSize,
            MinFontSize = MinFontSize,
            NormalizeWhitespace = false,
            LineHeightMultiplier = lineHeightMultiplier,
        };

        if (renderLineRects is { Count: > 0 })
        {
            request = request with
            {
                LineWidths = renderLineRects.Select(r => Math.Max(10, r.Width)).ToList(),
                MaxLineCount = renderLineRects.Count,
                MaxHeight = availableHeight,
            };
        }
        else
        {
            request = request with
            {
                MaxWidth = bbox.Width,
                MaxHeight = availableHeight,
            };
        }

        return FontFitSolver.Solve(
            request,
            TextLayoutEngine.Instance,
            size => CreateGlyphMeasurer(renderFont, fonts, size));
    }

    private static double ResolveAvailableHeight(
        BlockRect bbox,
        IReadOnlyList<BlockRect>? renderLineRects,
        IReadOnlyList<BlockRect>? backgroundLineRects)
    {
        return TryGetVerticalSpan(backgroundLineRects)
            ?? TryGetVerticalSpan(renderLineRects)
            ?? Math.Max(1, bbox.Height);
    }

    private static double? TryGetVerticalSpan(IReadOnlyList<BlockRect>? rects)
    {
        if (rects is not { Count: > 0 })
            return null;

        var minY = rects.Min(rect => rect.Y);
        var maxBottom = rects.Max(rect => rect.Y + rect.Height);
        return Math.Max(1, maxBottom - minY);
    }

    private static PreparedParagraph PrepareLayoutParagraph(
        string text,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        double fontSize)
    {
        return TextLayoutEngine.Instance.Prepare(
            new TextPrepareRequest
            {
                Text = text,
                NormalizeWhitespace = false,
            },
            CreateGlyphMeasurer(renderFont, fonts, fontSize));
    }

    private static GlyphAdvanceMeasurer CreateGlyphMeasurer(
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        double fontSize)
    {
        return new GlyphAdvanceMeasurer(
            renderFont.PrimaryFace.GlyphMap,
            renderFont.PrimaryFace.AdvanceWidths,
            renderFont.PrimaryFace.UnitsPerEm,
            fonts.NotoGlyphMap,
            fonts.NotoAdvanceWidths,
            fonts.NotoUnitsPerEm,
            renderFont.PrimaryIsCjk,
            fontSize,
            renderFont.LatinFace?.GlyphMap,
            renderFont.LatinFace?.AdvanceWidths,
            renderFont.LatinFace?.UnitsPerEm ?? 1000);
    }

    private static IReadOnlyList<LayoutLine> LayoutLines(
        PreparedParagraph prepared,
        IReadOnlyList<BlockRect>? renderLineRects,
        double maxWidth)
    {
        if (renderLineRects is { Count: > 0 })
        {
            return TextLayoutEngine.Instance
                .LayoutWithLines(prepared, renderLineRects.Select(r => Math.Max(10, r.Width)).ToList())
                .Lines;
        }

        return TextLayoutEngine.Instance.LayoutWithLines(prepared, maxWidth).Lines;
    }

    private static string TruncateLineToFitWidth(
        string lineText,
        double maxWidth,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        double fontSize)
    {
        var baseText = lineText.TrimEnd('.', ' ');
        var candidate = string.IsNullOrEmpty(baseText) ? "..." : $"{baseText}...";
        while (candidate.Length > 3 && !FitsWithinWidth(candidate, maxWidth, renderFont, fonts, fontSize))
        {
            if (string.IsNullOrEmpty(baseText))
                return "...";

            baseText = baseText[..^1].TrimEnd();
            candidate = string.IsNullOrEmpty(baseText) ? "..." : $"{baseText}...";
        }

        return candidate;
    }

    private static bool FitsWithinWidth(
        string text,
        double maxWidth,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts,
        double fontSize)
    {
        var prepared = PrepareLayoutParagraph(text, renderFont, fonts, fontSize);
        return TextLayoutEngine.Instance.Layout(prepared, maxWidth).LineCount <= 1;
    }

    private static void AppendLineTextOperations(
        StringBuilder sb,
        string lineText,
        double startX,
        double baselineY,
        double fontSize,
        RenderFontPlan renderFont,
        EmbeddedFontInfo fonts)
    {
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

        var superNext = false;
        var subNext = false;
        foreach (var ch in lineText)
        {
            if (LatexFormulaSimplifier.IsScriptSignal(ch))
            {
                if (ch == '^') { superNext = true; subNext = false; }
                else { subNext = true; superNext = false; }
                continue;
            }

            var charFontSize = fontSize;
            var charY = baselineY;
            if (superNext)
            {
                charFontSize = fontSize * 0.7;
                charY = baselineY + fontSize * 0.4;
                superNext = false;
            }
            else if (subNext)
            {
                charFontSize = fontSize * 0.7;
                charY = baselineY - fontSize * 0.3;
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
    }

    internal static bool ShouldUseUnifiedRetryLayout(IReadOnlyList<TranslatedBlockData> blocks)
    {
        return blocks.Any(block =>
            !block.TranslationSkipped &&
            block.BoundingBox is not null &&
            IsUnifiedRetryLayoutEligible(block) &&
            (block.RetryCount > 0 || block.UsesSourceFallback));
    }

    internal static IReadOnlyList<PlannedPageBlock> BuildPageRenderPlan(
        IReadOnlyList<TranslatedBlockData> preparedBlocks,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts)
    {
        return ShouldUseUnifiedRetryLayout(preparedBlocks)
            ? BuildUnifiedRetryPageLayout(preparedBlocks, pageHeightPoints, fontId, fonts)
            : preparedBlocks.Select(block => BuildDefaultPageBlock(block, pageHeightPoints)).ToList();
    }

    internal static IReadOnlyList<PlannedPageBlock> BuildUnifiedRetryPageLayout(
        IReadOnlyList<TranslatedBlockData> preparedBlocks,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts)
    {
        var plansByChunkIndex = new Dictionary<int, PlannedPageBlock>();
        var placedBounds = new List<XRect>();

        foreach (var block in preparedBlocks
                     .OrderBy(block => block.OrderInPage)
                     .ThenByDescending(block => block.ReadingOrderScore)
                     .ThenBy(block => block.ChunkIndex))
        {
            if (block.BoundingBox is not BlockRect bbox ||
                block.TranslationSkipped ||
                string.IsNullOrWhiteSpace(block.TranslatedText) ||
                !IsUnifiedRetryLayoutEligible(block))
            {
                var defaultPlan = BuildDefaultPageBlock(block, pageHeightPoints);
                plansByChunkIndex[block.ChunkIndex] = defaultPlan;
                if (defaultPlan.TopLeftBounds is XRect occupiedBounds)
                    placedBounds.Add(occupiedBounds);
                continue;
            }

            var sourceBoundsTopLeft = ToTopLeftRect(pageHeightPoints, bbox);
            var preferredRenderRectsTopLeft = ToTopLeftRects(pageHeightPoints, block.RenderLineRects);
            var preferredEraseRectsTopLeft = ToTopLeftRects(
                pageHeightPoints,
                block.BackgroundLineRects ?? block.RenderLineRects);
            var preferredTop = preferredEraseRectsTopLeft?.Min(rect => rect.Y)
                ?? preferredRenderRectsTopLeft?.Min(rect => rect.Y)
                ?? sourceBoundsTopLeft.Y;
            var gap = GetUnifiedRetryLayoutGap(block);
            var finalTop = FindNextAvailableTop(
                preferredTop,
                sourceBoundsTopLeft,
                placedBounds,
                gap);

            PlannedPageBlock plannedBlock;
            while (true)
            {
                plannedBlock = BuildUnifiedRetryPageBlock(
                    block,
                    finalTop,
                    pageHeightPoints,
                    fontId,
                    fonts,
                    sourceBoundsTopLeft,
                    preferredRenderRectsTopLeft,
                    preferredEraseRectsTopLeft);

                if (plannedBlock.TopLeftBounds is not XRect actualBounds)
                    break;

                var adjustedTop = FindNextAvailableTop(finalTop, actualBounds, placedBounds, gap);
                if (adjustedTop <= finalTop + 0.01)
                    break;

                finalTop = adjustedTop;
            }

            plansByChunkIndex[block.ChunkIndex] = plannedBlock;
            if (plannedBlock.TopLeftBounds is XRect topLeftBounds)
                placedBounds.Add(topLeftBounds);
        }

        return preparedBlocks
            .Select(block => plansByChunkIndex.TryGetValue(block.ChunkIndex, out var plan)
                ? plan
                : BuildDefaultPageBlock(block, pageHeightPoints))
            .ToList();
    }

    private static PlannedPageBlock BuildDefaultPageBlock(
        TranslatedBlockData block,
        double pageHeightPoints)
    {
        return new PlannedPageBlock
        {
            Block = block,
            LayoutBoundingBox = block.BoundingBox,
            LayoutRenderLineRects = block.RenderLineRects,
            LayoutBackgroundLineRects = block.BackgroundLineRects,
            EraseRects = block.BackgroundLineRects,
            TopLeftBounds = block.BoundingBox is BlockRect bbox
                ? ToTopLeftRect(pageHeightPoints, bbox)
                : null,
            PlannedOperations = null,
            PlannedChosenFontSize = 0,
            PlannedLinesRendered = 0,
            PlannedWasShrunk = false,
            PlannedWasTruncated = false,
            RenderableText = PrepareRenderableTextForPdf(block.TranslatedText),
        };
    }

    private static PlannedPageBlock BuildUnifiedRetryPageBlock(
        TranslatedBlockData block,
        double top,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts,
        XRect sourceBoundsTopLeft,
        IReadOnlyList<XRect>? preferredRenderRectsTopLeft,
        IReadOnlyList<XRect>? preferredEraseRectsTopLeft)
    {
        var plannedTextLayout = PlanUnifiedRetryPageBlockTextLayout(
            block,
            top,
            pageHeightPoints,
            fontId,
            fonts,
            sourceBoundsTopLeft,
            preferredRenderRectsTopLeft);
        var renderBoundsTopLeft = GetBounds(plannedTextLayout.RenderRectsTopLeft);
        var sourceEraseRectsTopLeft = preferredEraseRectsTopLeft is { Count: > 0 }
            ? preferredEraseRectsTopLeft
            : [sourceBoundsTopLeft];
        var eraseRectsTopLeft = BuildFinalEraseRectsTopLeft(
            sourceEraseRectsTopLeft,
            plannedTextLayout.RenderRectsTopLeft);
        var eraseRects = ToBottomUpRects(pageHeightPoints, eraseRectsTopLeft);

        return new PlannedPageBlock
        {
            Block = block,
            LayoutBoundingBox = ToBottomUpRect(pageHeightPoints, renderBoundsTopLeft),
            LayoutRenderLineRects = plannedTextLayout.RenderLineRects,
            LayoutBackgroundLineRects = eraseRects,
            EraseRects = eraseRects,
            TopLeftBounds = renderBoundsTopLeft,
            PlannedOperations = plannedTextLayout.Operations,
            PlannedChosenFontSize = plannedTextLayout.ChosenFontSize,
            PlannedLinesRendered = plannedTextLayout.LinesRendered,
            PlannedWasShrunk = plannedTextLayout.WasShrunk,
            PlannedWasTruncated = plannedTextLayout.WasTruncated,
            RenderableText = plannedTextLayout.RenderableText,
        };
    }

    private static PlannedRetryTextLayout PlanUnifiedRetryPageBlockTextLayout(
        TranslatedBlockData block,
        double top,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts,
        XRect sourceBoundsTopLeft,
        IReadOnlyList<XRect>? preferredRenderRectsTopLeft)
    {
        var renderableText = PrepareRenderableTextForPdf(block.TranslatedText);
        var renderFont = ResolveRenderFontPlan(
            renderableText,
            fontId,
            fonts,
            block.SourceBlockType,
            block.UsesSourceFallback,
            block.DetectedFontNames,
            block.TextStyle);

        var originalFontSize = block.FontSize > 0 ? block.FontSize : 10.0;
        var baseLineHeight = block.UsesSourceFallback && block.TextStyle?.LineSpacing > 0
            ? block.TextStyle.LineSpacing
            : originalFontSize * 1.2;
        var lineHeightMultiplier = originalFontSize > 0
            ? Math.Max(1.0, baseLineHeight / originalFontSize)
            : 1.2;

        var availableHeight = Math.Max(MinFontSize, pageHeightPoints - top);
        var baseWidths = preferredRenderRectsTopLeft is { Count: > 0 }
            ? preferredRenderRectsTopLeft.Select(rect => Math.Max(10, rect.Width)).ToList()
            : [Math.Max(10, sourceBoundsTopLeft.Width)];
        var baseXs = preferredRenderRectsTopLeft is { Count: > 0 }
            ? preferredRenderRectsTopLeft.Select(rect => rect.X).ToList()
            : [sourceBoundsTopLeft.X];
        var maxPossibleLines = Math.Max(1, (int)Math.Ceiling(availableHeight / MinFontSize));
        var plannedWidths = ExpandLineWidths(baseWidths, maxPossibleLines);

        var fitResult = FontFitSolver.Solve(
            new FontFitRequest
            {
                Text = renderableText,
                StartFontSize = originalFontSize,
                MinFontSize = MinFontSize,
                NormalizeWhitespace = false,
                LineHeightMultiplier = lineHeightMultiplier,
                LineWidths = plannedWidths,
                MaxLineCount = maxPossibleLines,
                MaxHeight = availableHeight,
            },
            TextLayoutEngine.Instance,
            size => CreateGlyphMeasurer(renderFont, fonts, size));

        var chosenFontSize = fitResult.ChosenFontSize;
        var prepared = PrepareLayoutParagraph(renderableText, renderFont, fonts, chosenFontSize);
        var wrappedLines = TextLayoutEngine.Instance.LayoutWithLines(prepared, plannedWidths)
            .Lines
            .Select(line => line.Text)
            .ToList();
        var lineHeight = Math.Max(chosenFontSize, fitResult.ChosenLineHeight);
        var maxVisibleLines = Math.Max(1, (int)Math.Floor(availableHeight / lineHeight));
        var wasTruncated = fitResult.WasTruncated;
        if (wrappedLines.Count > maxVisibleLines)
        {
            wrappedLines = wrappedLines.Take(maxVisibleLines).ToList();
            var lastWidth = plannedWidths[Math.Min(maxVisibleLines, plannedWidths.Count) - 1];
            wrappedLines[^1] = TruncateLineToFitWidth(
                wrappedLines[^1],
                lastWidth,
                renderFont,
                fonts,
                chosenFontSize);
            wasTruncated = true;
        }

        if (wrappedLines.Count == 0)
            wrappedLines = [renderableText];

        var renderRectsTopLeft = new List<XRect>(wrappedLines.Count);
        for (var i = 0; i < wrappedLines.Count; i++)
        {
            var width = plannedWidths[Math.Min(i, plannedWidths.Count - 1)];
            var x = baseXs[Math.Min(i, baseXs.Count - 1)];
            renderRectsTopLeft.Add(new XRect(x, top + i * lineHeight, width, lineHeight));
        }

        var renderBoundsBottomUp = ToBottomUpRect(pageHeightPoints, GetBounds(renderRectsTopLeft));
        var renderLineRects = ToBottomUpRects(pageHeightPoints, renderRectsTopLeft)
            ?? Array.Empty<BlockRect>();
        var operations = BuildBlockTextOperationsFromLines(
            wrappedLines,
            chosenFontSize,
            renderFont,
            fonts,
            block.TextStyle,
            renderBoundsBottomUp,
            renderLineRects,
            lineHeight);

        return new PlannedRetryTextLayout
        {
            Operations = operations,
            RenderRectsTopLeft = renderRectsTopLeft,
            RenderLineRects = renderLineRects,
            ChosenFontSize = chosenFontSize,
            LinesRendered = wrappedLines.Count,
            WasShrunk = chosenFontSize < originalFontSize - 0.01,
            WasTruncated = wasTruncated,
            RenderableText = renderableText,
        };
    }

    private static IReadOnlyList<double> ExpandLineWidths(
        IReadOnlyList<double> widths,
        int count)
    {
        if (widths.Count == 0)
            return Enumerable.Repeat(100d, Math.Max(1, count)).ToList();

        if (widths.Count >= count)
            return widths.Count == count ? widths : widths.Take(count).ToList();

        var result = new List<double>(count);
        result.AddRange(widths);
        var last = widths[^1];
        while (result.Count < count)
            result.Add(last);
        return result;
    }

    private static double FindNextAvailableTop(
        double preferredTop,
        XRect sourceBoundsTopLeft,
        IReadOnlyList<XRect> placedBounds,
        double gap)
    {
        var top = preferredTop;
        while (true)
        {
            var nextTop = top;
            var candidateRect = new XRect(sourceBoundsTopLeft.X, top, sourceBoundsTopLeft.Width, sourceBoundsTopLeft.Height);
            foreach (var placed in placedBounds)
            {
                if (!HorizontallyOverlaps(candidateRect, placed))
                    continue;

                var minAllowedTop = placed.Bottom + gap;
                if (candidateRect.Y < minAllowedTop)
                    nextTop = Math.Max(nextTop, minAllowedTop);
            }

            if (Math.Abs(nextTop - top) < 0.01)
                return top;

            top = nextTop;
        }
    }

    private static bool HorizontallyOverlaps(XRect candidate, XRect placed)
    {
        var overlap = Math.Min(candidate.Right, placed.Right) - Math.Max(candidate.Left, placed.Left);
        return overlap > 5;
    }

    private static bool IsUnifiedRetryLayoutEligible(TranslatedBlockData block)
    {
        return block.SourceBlockType is not SourceBlockType.Formula
            and not SourceBlockType.TableCell;
    }

    private static double GetUnifiedRetryLayoutGap(TranslatedBlockData block)
    {
        var fontSize = block.FontSize > 0
            ? block.FontSize
            : (block.TextStyle?.FontSize > 0 ? block.TextStyle.FontSize : 10.0);
        return Math.Clamp(fontSize * 0.15, 1.5, 6);
    }

    private static XRect GetBounds(IReadOnlyList<XRect> rects)
    {
        var minX = rects.Min(rect => rect.X);
        var minY = rects.Min(rect => rect.Y);
        var maxRight = rects.Max(rect => rect.Right);
        var maxBottom = rects.Max(rect => rect.Bottom);
        return new XRect(minX, minY, maxRight - minX, maxBottom - minY);
    }

    private static IReadOnlyList<XRect> BuildFinalEraseRectsTopLeft(
        IReadOnlyList<XRect> sourceEraseRectsTopLeft,
        IReadOnlyList<XRect> finalRenderRectsTopLeft)
    {
        var combined = sourceEraseRectsTopLeft
            .Concat(finalRenderRectsTopLeft)
            .Where(rect => rect.Width > 0.1 && rect.Height > 0.1)
            .ToList();

        if (combined.Count <= 1)
            return combined;

        var clusters = new List<List<XRect>>();
        foreach (var rect in combined.OrderBy(rect => rect.X).ThenBy(rect => rect.Y))
        {
            List<int>? matchingClusters = null;
            for (var i = 0; i < clusters.Count; i++)
            {
                if (!RectsBelongToSameEraseBand(GetBounds(clusters[i]), rect))
                    continue;

                matchingClusters ??= [];
                matchingClusters.Add(i);
            }

            if (matchingClusters is null || matchingClusters.Count == 0)
            {
                clusters.Add([rect]);
                continue;
            }

            var targetIndex = matchingClusters[0];
            clusters[targetIndex].Add(rect);
            for (var i = matchingClusters.Count - 1; i >= 1; i--)
            {
                var clusterIndex = matchingClusters[i];
                clusters[targetIndex].AddRange(clusters[clusterIndex]);
                clusters.RemoveAt(clusterIndex);
            }
        }

        return clusters
            .Select(cluster => GetBounds(cluster))
            .OrderBy(rect => rect.Y)
            .ThenBy(rect => rect.X)
            .ToList();
    }

    private static bool RectsBelongToSameEraseBand(XRect left, XRect right)
    {
        var horizontalOverlap = Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left);
        if (horizontalOverlap > 3)
            return true;

        var horizontalGap = Math.Max(0, Math.Max(left.Left, right.Left) - Math.Min(left.Right, right.Right));
        var toleratedGap = Math.Clamp(Math.Min(left.Width, right.Width) * 0.2, 4, 24);
        return horizontalGap <= toleratedGap;
    }

    private static void AppendEraseOperations(
        StringBuilder opsErase,
        TranslatedBlockData block,
        bool primaryFontIsCjk,
        IReadOnlyList<BlockRect>? eraseRects = null)
    {
        var fontSize = block.FontSize > 0
            ? block.FontSize
            : (block.TextStyle?.FontSize > 0 ? block.TextStyle.FontSize : 10.0);
        var padding = GetErasePadding(fontSize, primaryFontIsCjk);

        if (eraseRects is { Count: > 0 })
        {
            foreach (var rect in eraseRects)
                AppendEraseRect(opsErase, rect, padding);
            return;
        }

        if (block.BackgroundLineRects is { Count: > 0 })
        {
            foreach (var rect in block.BackgroundLineRects)
                AppendEraseRect(opsErase, rect, padding);
            return;
        }

        if (block.BoundingBox is BlockRect bbox)
            AppendEraseRect(opsErase, bbox, padding);
    }

    private static void AppendEraseRect(StringBuilder opsErase, BlockRect rect, double padding)
    {
        var x = Math.Max(0, rect.X - padding);
        var y = Math.Max(0, rect.Y - padding);
        var width = rect.Width + padding * 2;
        var height = rect.Height + padding * 2;
        opsErase.Append($"1 1 1 rg {x:F6} {y:F6} {width:F6} {height:F6} re f ");
    }

    private static double GetErasePadding(double fontSize, bool primaryFontIsCjk)
    {
        var pad = Math.Clamp(fontSize * 0.25, 2.5, 10);
        if (primaryFontIsCjk)
            pad = Math.Max(pad, Math.Clamp(fontSize * 0.30, 3, 12));
        return pad;
    }

    internal static TranslatedBlockData PrepareBlockForRendering(
        TranslatedBlockData block,
        double pageHeightPoints)
    {
        if (block.BoundingBox is not BlockRect bbox)
            return block;

        var lineHeight = block.TextStyle?.LineSpacing > 0
            ? block.TextStyle.LineSpacing
            : (block.FontSize > 0 ? block.FontSize * 1.2 : 14d);
        var pageRect = ToTopLeftRect(pageHeightPoints, bbox);
        var lineRects = BuildMuPdfLineRects(pageHeightPoints, pageRect, block.TextStyle, lineHeight);
        var (translatedText, renderLineRects, backgroundLineRects, _) =
            PdfExportService.HandleInlineScriptLinesForOverlay(block.SourceText, block.TranslatedText, lineRects);

        if (lineRects is { Count: > 0 } && renderLineRects is { Count: 0 })
        {
            renderLineRects = null;
            backgroundLineRects = null;
        }

        return new TranslatedBlockData
        {
            ChunkIndex = block.ChunkIndex,
            PageNumber = block.PageNumber,
            SourceBlockId = block.SourceBlockId,
            OrderInPage = block.OrderInPage,
            ReadingOrderScore = block.ReadingOrderScore,
            SourceText = block.SourceText,
            TranslatedText = translatedText,
            BoundingBox = block.BoundingBox,
            FontSize = block.FontSize,
            TranslationSkipped = block.TranslationSkipped,
            TextStyle = block.TextStyle,
            SourceBlockType = block.SourceBlockType,
            RetryCount = block.RetryCount,
            UsesSourceFallback = block.UsesSourceFallback,
            DetectedFontNames = block.DetectedFontNames,
            RenderLineRects = ToBottomUpRects(pageHeightPoints, renderLineRects),
            BackgroundLineRects = ToBottomUpRects(pageHeightPoints, backgroundLineRects),
        };
    }

    internal static IReadOnlyList<XRect>? BuildMuPdfLineRects(
        double pageHeightPoints,
        XRect blockRect,
        BlockTextStyle? style,
        double fallbackLineHeight)
    {
        var positions = style?.LinePositions;
        if (positions == null || positions.Count == 0)
            return null;

        if (positions.Count == 1)
            return PdfExportService.TryBuildLineRects(pageHeightPoints, blockRect, style, fallbackLineHeight);

        if (PdfExportService.LooksLikeGridLinePositions(positions))
            return null;

        var lineSpacing = style?.LineSpacing > 0
            ? style.LineSpacing
            : ComputeLineSpacing(positions, fallbackLineHeight);

        var result = new List<XRect>(positions.Count);
        var ordered = positions.OrderByDescending(p => p.BaselineY).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var pos = ordered[i];
            var upperPdf = i == 0
                ? pos.BaselineY + lineSpacing / 2
                : (ordered[i - 1].BaselineY + pos.BaselineY) / 2;
            var lowerPdf = i == ordered.Count - 1
                ? pos.BaselineY - lineSpacing / 2
                : (pos.BaselineY + ordered[i + 1].BaselineY) / 2;
            if (upperPdf <= lowerPdf)
                continue;

            var y = pageHeightPoints - upperPdf;
            var height = upperPdf - lowerPdf;
            var left = Math.Max(blockRect.X, pos.Left);
            var right = Math.Min(blockRect.Right, pos.Right);
            if (right - left < 5)
                continue;

            var yTop = Math.Max(0, y);
            var yBottom = Math.Min(pageHeightPoints, y + height);
            var h = yBottom - yTop;
            if (h < 3)
                continue;

            result.Add(new XRect(left, yTop, right - left, h));
        }

        return result.Count > 0 ? result : null;
    }

    private static double ComputeLineSpacing(
        IReadOnlyList<BlockLinePosition> positions,
        double fallbackLineHeight)
    {
        var sortedBaselines = positions.Select(p => p.BaselineY).OrderByDescending(v => v).ToList();
        var gaps = new List<double>();
        for (var i = 0; i < sortedBaselines.Count - 1; i++)
        {
            var gap = sortedBaselines[i] - sortedBaselines[i + 1];
            if (gap > 0.1)
                gaps.Add(gap);
        }

        if (gaps.Count > 0)
        {
            gaps.Sort();
            return gaps[gaps.Count / 2];
        }

        return Math.Max(8, fallbackLineHeight);
    }

    internal static XRect ToTopLeftRect(double pageHeightPoints, BlockRect box, double minSize = 10) =>
        new(
            Math.Max(0, box.X),
            Math.Max(0, pageHeightPoints - (box.Y + box.Height)),
            Math.Max(minSize, box.Width),
            Math.Max(minSize, box.Height));

    internal static IReadOnlyList<XRect>? ToTopLeftRects(
        double pageHeightPoints,
        IReadOnlyList<BlockRect>? rects,
        double minSize = 0)
    {
        if (rects is null || rects.Count == 0)
            return null;

        return rects
            .Select(rect => ToTopLeftRect(pageHeightPoints, rect, minSize))
            .ToList();
    }

    private static BlockRect ToBottomUpRect(double pageHeightPoints, XRect rect) =>
        new(
            rect.X,
            Math.Max(0, pageHeightPoints - (rect.Y + rect.Height)),
            rect.Width,
            rect.Height);

    private static IReadOnlyList<BlockRect>? ToBottomUpRects(
        double pageHeightPoints,
        IReadOnlyList<XRect>? rects)
    {
        if (rects is null || rects.Count == 0)
            return null;

        return rects
            .Select(rect => new BlockRect(
                rect.X,
                Math.Max(0, pageHeightPoints - (rect.Y + rect.Height)),
                rect.Width,
                rect.Height))
            .ToList();
    }

    /// <summary>
    /// Simplifies LaTeX formula markup to plain text for PDF rendering.
    /// Preserves ^ and _ as rendering signals for super/subscript positioning.
    /// Delegates to <see cref="LatexFormulaSimplifier.Simplify"/> for Unicode symbol mapping.
    /// </summary>
    private static string SimplifyLatexMarkup(string text) =>
        LatexFormulaSimplifier.Simplify(text, preserveScriptSignals: true);

    internal static string PrepareRenderableTextForPdf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return SimplifyLatexMarkup(text);
    }

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
                ChunkIndex = i,
                PageNumber = metadata.PageNumber,
                SourceBlockId = metadata.SourceBlockId,
                OrderInPage = metadata.OrderInPage,
                ReadingOrderScore = metadata.ReadingOrderScore,
                SourceText = checkpoint.SourceChunks[i],
                TranslatedText = translated,
                BoundingBox = metadata.BoundingBox,
                FontSize = metadata.TextStyle?.FontSize ?? 10.0,
                TranslationSkipped = isFormulaSkipped || isVertical,
                TextStyle = metadata.TextStyle,
                SourceBlockType = metadata.SourceBlockType,
                RetryCount = metadata.RetryCount,
                UsesSourceFallback = usesSourceFallback,
                DetectedFontNames = metadata.DetectedFontNames,
                RenderLineRects = null,
                BackgroundLineRects = null,
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

    private sealed record PageRenderResult(
        int RenderedBlocks,
        int MissingBoundingBoxBlocks,
        int ShrinkFontBlocks,
        int TruncatedBlocks,
        IReadOnlyList<BackfillBlockIssue> BlockIssues);

    internal sealed record BlockTextRenderResult(
        string Operations,
        double ChosenFontSize,
        int LinesRendered,
        bool WasShrunk,
        bool WasTruncated);

    internal sealed record TranslatedBlockData
    {
        public required int ChunkIndex { get; init; }
        public required int PageNumber { get; init; }
        public required string SourceBlockId { get; init; }
        public int OrderInPage { get; init; }
        public double ReadingOrderScore { get; init; }
        public required string SourceText { get; init; }
        public required string TranslatedText { get; init; }
        public BlockRect? BoundingBox { get; init; }
        public double FontSize { get; init; }
        public bool TranslationSkipped { get; init; }
        public BlockTextStyle? TextStyle { get; init; }
        public SourceBlockType SourceBlockType { get; init; }
        public int RetryCount { get; init; }
        public bool UsesSourceFallback { get; init; }
        public IReadOnlyList<string>? DetectedFontNames { get; init; }
        public IReadOnlyList<BlockRect>? RenderLineRects { get; init; }
        public IReadOnlyList<BlockRect>? BackgroundLineRects { get; init; }
    }

    internal sealed record PlannedPageBlock
    {
        public required TranslatedBlockData Block { get; init; }
        public BlockRect? LayoutBoundingBox { get; init; }
        public IReadOnlyList<BlockRect>? LayoutRenderLineRects { get; init; }
        public IReadOnlyList<BlockRect>? LayoutBackgroundLineRects { get; init; }
        public IReadOnlyList<BlockRect>? EraseRects { get; init; }
        public XRect? TopLeftBounds { get; init; }
        public string? PlannedOperations { get; init; }
        public double PlannedChosenFontSize { get; init; }
        public int PlannedLinesRendered { get; init; }
        public bool PlannedWasShrunk { get; init; }
        public bool PlannedWasTruncated { get; init; }
        public string? RenderableText { get; init; }
    }

    internal sealed record PlannedRetryTextLayout
    {
        public required string Operations { get; init; }
        public required IReadOnlyList<XRect> RenderRectsTopLeft { get; init; }
        public required IReadOnlyList<BlockRect> RenderLineRects { get; init; }
        public double ChosenFontSize { get; init; }
        public int LinesRendered { get; init; }
        public bool WasShrunk { get; init; }
        public bool WasTruncated { get; init; }
        public required string RenderableText { get; init; }
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
