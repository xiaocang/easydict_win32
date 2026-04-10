using Easydict.TextLayout;
using Easydict.TextLayout.FontFitting;
using Easydict.TextLayout.Layout;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using PdfSharpCore.Drawing;
using static Easydict.WinUI.Services.DocumentExport.MuPdfExportService;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Computes the final layout plan for all blocks on a page.
/// Pipeline: Group blocks by styling → Measure each block's actual size →
/// Arrange groups with whole-page context (overlap prevention, inter-group space borrowing) →
/// Generate pre-computed PDF operations at final positions.
/// </summary>
internal static class PageBlockLayoutPlanner
{
    private const double MinFontSize = 6.0;

    internal static IReadOnlyList<PlannedPageBlock> PlanPageLayout(
        IReadOnlyList<TranslatedBlockData> preparedBlocks,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts)
    {
        // Phase 0: spatially promote table-neighbor blocks to "preserved".
        // The ML layout detector sometimes misses sparse-cell tables (e.g. the
        // Transformer paper Table 3) and routes table cells through as Paragraph
        // blocks. Numeric-data rows are caught by FormulaPreservationService and
        // arrive here as preserved already. Table HEADERS ("train PPL BLEU params"),
        // row labels ("(D)"), and footnotes are NOT numeric and slip through as
        // translatable. We use spatial proximity to a preserved-numeric block plus
        // a "looks like a header / short label" filter to promote them, so the
        // entire visual table is preserved verbatim from the source PDF.
        preparedBlocks = PromoteTableNeighbors(preparedBlocks, pageHeightPoints);

        var ordered = preparedBlocks
            .Select((block, index) => (block, index))
            .OrderBy(pair => pair.block.OrderInPage)
            .ThenByDescending(pair => pair.block.ReadingOrderScore)
            .ThenBy(pair => pair.block.ChunkIndex)
            .ToList();

        // Phase 1: Measure all blocks at source positions (caches font fit results)
        var measurements = new MeasuredBlock[preparedBlocks.Count];
        foreach (var (block, index) in ordered)
            measurements[index] = MeasureBlock(block, pageHeightPoints, fontId, fonts);

        // Phase 2: Group into flow groups
        var groups = GroupIntoFlowGroups(ordered, measurements, preparedBlocks);

        // Phase 3: Arrange groups with whole-page context
        var finalPositions = ArrangeGroups(groups, measurements, preparedBlocks, pageHeightPoints);

        // Phase 4: Generate PlannedPageBlock at final positions
        var results = new PlannedPageBlock[preparedBlocks.Count];
        foreach (var (block, index) in ordered)
        {
            var measurement = measurements[index];
            var finalTop = finalPositions[index];

            if (measurement.IsPassthrough)
            {
                results[index] = BuildPassthroughBlock(block, pageHeightPoints);
            }
            else if (Math.Abs(finalTop - measurement.SourceTop) < 0.5 && !measurement.Overflows)
            {
                results[index] = BuildConstrainedBlock(block, pageHeightPoints);
            }
            else if (Math.Abs(finalTop - measurement.SourceTop) < 0.5 && measurement.CachedLayout is not null)
            {
                // Block overflows but didn't move — reuse cached layout from Phase 1
                results[index] = BuildPlannedBlockFromLayout(
                    block, pageHeightPoints, measurement.SourceBoundsTopLeft,
                    measurement.PreferredEraseRectsTopLeft, measurement.CachedLayout);
            }
            else
            {
                // Block moved — regenerate at final position
                results[index] = BuildPlannedBlock(
                    block, finalTop, pageHeightPoints, fontId, fonts,
                    measurement.SourceBoundsTopLeft, measurement.PreferredRenderRectsTopLeft,
                    measurement.PreferredEraseRectsTopLeft);
            }
        }

        return results;
    }

    #region Phase 1: Measure

    private sealed record MeasuredBlock
    {
        public required double SourceTop { get; init; }
        public required double SourceHeight { get; init; }
        public required double MeasuredHeight { get; init; }
        public required XRect SourceBoundsTopLeft { get; init; }
        public IReadOnlyList<XRect>? PreferredRenderRectsTopLeft { get; init; }
        public IReadOnlyList<XRect>? PreferredEraseRectsTopLeft { get; init; }
        public bool IsPassthrough { get; init; }
        public bool Overflows => MeasuredHeight > SourceHeight + 0.5;
        public double PreferredTop { get; init; }
        public PlannedRetryTextLayout? CachedLayout { get; init; }
    }

    private static MeasuredBlock MeasureBlock(
        TranslatedBlockData block,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts)
    {
        if (block.BoundingBox is not BlockRect bbox ||
            !ShouldRenderBlockText(block) ||
            !IsLayoutEligible(block))
        {
            var sourceBounds = block.BoundingBox is BlockRect b
                ? ToTopLeftRect(pageHeightPoints, b)
                : new XRect(0, 0, 100, 20);
            return new MeasuredBlock
            {
                SourceTop = sourceBounds.Y,
                SourceHeight = sourceBounds.Height,
                MeasuredHeight = sourceBounds.Height,
                SourceBoundsTopLeft = sourceBounds,
                IsPassthrough = true,
                PreferredTop = sourceBounds.Y,
            };
        }

        var sourceBoundsTopLeft = ToTopLeftRect(pageHeightPoints, bbox);
        var preferredRenderRectsTopLeft = ToTopLeftRects(pageHeightPoints, block.RenderLineRects);
        var preferredEraseRectsTopLeft = ToTopLeftRects(
            pageHeightPoints,
            block.BackgroundLineRects ?? block.RenderLineRects);
        var preferredTop = preferredEraseRectsTopLeft?.Min(r => r.Y)
            ?? preferredRenderRectsTopLeft?.Min(r => r.Y)
            ?? sourceBoundsTopLeft.Y;

        // Full layout at source position — cache result to avoid re-computing in Phase 4
        var layout = PlanBlockTextLayout(
            block, sourceBoundsTopLeft.Y, pageHeightPoints, fontId, fonts,
            sourceBoundsTopLeft, preferredRenderRectsTopLeft);
        var measuredHeight = layout.LinesRendered * Math.Max(layout.ChosenFontSize, layout.ChosenFontSize * 1.2);
        if (layout.RenderRectsTopLeft.Count > 0)
        {
            var bounds = GetBounds(layout.RenderRectsTopLeft);
            measuredHeight = bounds.Height;
        }

        return new MeasuredBlock
        {
            SourceTop = sourceBoundsTopLeft.Y,
            SourceHeight = sourceBoundsTopLeft.Height,
            MeasuredHeight = measuredHeight,
            SourceBoundsTopLeft = sourceBoundsTopLeft,
            PreferredRenderRectsTopLeft = preferredRenderRectsTopLeft,
            PreferredEraseRectsTopLeft = preferredEraseRectsTopLeft,
            PreferredTop = preferredTop,
            CachedLayout = layout,
        };
    }

    #endregion

    #region Phase 2: Group

    private static List<List<int>> GroupIntoFlowGroups(
        List<(TranslatedBlockData block, int index)> ordered,
        MeasuredBlock[] measurements,
        IReadOnlyList<TranslatedBlockData> preparedBlocks)
    {
        var groups = new List<List<int>>();
        List<int>? currentGroup = null;

        foreach (var (block, index) in ordered)
        {
            var measurement = measurements[index];
            if (measurement.IsPassthrough)
            {
                if (currentGroup is { Count: > 0 })
                    groups.Add(currentGroup);
                groups.Add([index]);
                currentGroup = null;
                continue;
            }

            if (currentGroup is null or { Count: 0 })
            {
                currentGroup = [index];
                continue;
            }

            var prevIndex = currentGroup[^1];
            if (CanJoinFlowGroup(preparedBlocks[prevIndex], measurements[prevIndex], block, measurement))
            {
                currentGroup.Add(index);
            }
            else
            {
                groups.Add(currentGroup);
                currentGroup = [index];
            }
        }

        if (currentGroup is { Count: > 0 })
            groups.Add(currentGroup);

        return groups;
    }

    private static bool CanJoinFlowGroup(
        TranslatedBlockData prevBlock,
        MeasuredBlock prevMeasurement,
        TranslatedBlockData block,
        MeasuredBlock measurement)
    {
        if (prevBlock.SourceBlockType != block.SourceBlockType)
            return false;

        var prevFontSize = prevBlock.FontSize > 0 ? prevBlock.FontSize : 10.0;
        var currFontSize = block.FontSize > 0 ? block.FontSize : 10.0;
        var maxFs = Math.Max(prevFontSize, currFontSize);
        if (maxFs > 0 && Math.Abs(prevFontSize - currFontSize) / maxFs > 0.2)
            return false;

        if (prevBlock.TextStyle?.IsBold != block.TextStyle?.IsBold ||
            prevBlock.TextStyle?.IsItalic != block.TextStyle?.IsItalic)
            return false;

        if (prevBlock.TextStyle is not null && block.TextStyle is not null &&
            (prevBlock.TextStyle.ColorR != block.TextStyle.ColorR ||
             prevBlock.TextStyle.ColorG != block.TextStyle.ColorG ||
             prevBlock.TextStyle.ColorB != block.TextStyle.ColorB))
            return false;

        var prevBounds = prevMeasurement.SourceBoundsTopLeft;
        var currBounds = measurement.SourceBoundsTopLeft;

        var lineHeight = currFontSize * 1.2;
        var verticalGap = currBounds.Y - prevBounds.Bottom;
        if (verticalGap > 1.5 * lineHeight || verticalGap < -0.5 * lineHeight)
            return false;

        if (Math.Abs(prevBounds.Left - currBounds.Left) > 5)
            return false;

        var maxWidth = Math.Max(prevBounds.Width, currBounds.Width);
        if (maxWidth > 0 && Math.Abs(prevBounds.Width - currBounds.Width) / maxWidth > 0.15)
            return false;

        return true;
    }

    #endregion

    #region Phase 3: Arrange

    private static Dictionary<int, double> ArrangeGroups(
        List<List<int>> groups,
        MeasuredBlock[] measurements,
        IReadOnlyList<TranslatedBlockData> preparedBlocks,
        double pageHeightPoints)
    {
        var finalPositions = new Dictionary<int, double>();
        var placedBounds = new List<XRect>();

        // Pre-seed every preserved (passthrough) block as an immovable obstacle.
        // This guarantees translated text blocks see them during collision detection
        // regardless of reading-order position. ArrangeGroup short-circuits passthrough
        // blocks below so they are not double-added.
        for (var i = 0; i < preparedBlocks.Count; i++)
        {
            if (measurements[i].IsPassthrough)
                placedBounds.Add(measurements[i].SourceBoundsTopLeft);
        }

        foreach (var group in groups)
            ArrangeGroup(group, measurements, preparedBlocks, pageHeightPoints, placedBounds, finalPositions);

        return finalPositions;
    }

    private static void ArrangeGroup(
        List<int> groupIndices,
        MeasuredBlock[] measurements,
        IReadOnlyList<TranslatedBlockData> preparedBlocks,
        double pageHeightPoints,
        List<XRect> placedBounds,
        Dictionary<int, double> finalPositions)
    {
        var currentTop = 0.0;

        foreach (var index in groupIndices)
        {
            var measurement = measurements[index];
            var block = preparedBlocks[index];

            // Passthrough blocks are pinned to their source position. They were
            // pre-seeded into placedBounds in ArrangeGroups, so we don't add them
            // again here — and we MUST NOT subject them to collision pushing.
            if (measurement.IsPassthrough)
            {
                finalPositions[index] = measurement.SourceTop;
                currentTop = Math.Max(currentTop, measurement.SourceBoundsTopLeft.Bottom);
                continue;
            }

            var preferredTop = Math.Max(currentTop, measurement.PreferredTop);

            var gap = GetLayoutGap(block);
            var blockHeight = measurement.Overflows
                ? measurement.MeasuredHeight
                : measurement.SourceHeight;
            var candidateRect = new XRect(
                measurement.SourceBoundsTopLeft.X,
                preferredTop,
                measurement.SourceBoundsTopLeft.Width,
                blockHeight);
            var adjustedTop = FindNextAvailableTop(
                preferredTop,
                candidateRect,
                placedBounds,
                gap,
                candidateSourceBounds: measurement.SourceBoundsTopLeft);

            finalPositions[index] = adjustedTop;

            var placedRect = new XRect(
                measurement.SourceBoundsTopLeft.X,
                adjustedTop,
                measurement.SourceBoundsTopLeft.Width,
                blockHeight);
            placedBounds.Add(placedRect);

            currentTop = adjustedTop + blockHeight;
        }
    }

    internal static double FindNextAvailableTop(
        double preferredTop,
        XRect candidateBounds,
        IReadOnlyList<XRect> placedBounds,
        double gap,
        XRect? candidateSourceBounds = null)
    {
        var top = preferredTop;
        while (true)
        {
            var nextTop = top;
            var candidateRect = new XRect(candidateBounds.X, top, candidateBounds.Width, candidateBounds.Height);
            foreach (var placed in placedBounds)
            {
                if (!HorizontallyOverlaps(candidateRect, placed))
                    continue;

                // Source-position guard (only when candidateSourceBounds is known):
                // Skip obstacles whose source top is at or below the candidate's
                // source bottom — they live BELOW the candidate in document order,
                // so they should not push the candidate further down. Without this
                // guard, pre-seeded preserved blocks (which include obstacles from
                // across the entire page) would shove paragraphs above them down
                // by the height of the obstacle below.
                if (candidateSourceBounds is XRect srcBounds &&
                    placed.Top >= srcBounds.Bottom - 0.5)
                    continue;

                // Containment skip: when the obstacle is fully enclosed by the
                // candidate's source bounding box, this is an inline-within-paragraph
                // situation (e.g. an inline formula whose bbox sits inside the
                // surrounding paragraph's bbox). The previous attempt at obstacle-
                // aware layout pushed the paragraph below such inline obstacles,
                // cascading every following block to the page bottom. Refuse to
                // push for nested obstacles — the paragraph stays at its source
                // position; per-line carving (future work) will route text around
                // the obstacle inside the paragraph if needed.
                if (candidateSourceBounds is XRect src &&
                    src.Left <= placed.Left + 0.5 &&
                    src.Right >= placed.Right - 0.5 &&
                    src.Top <= placed.Top + 0.5 &&
                    src.Bottom >= placed.Bottom - 0.5)
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

    internal static bool HorizontallyOverlaps(XRect candidate, XRect placed)
    {
        var overlap = Math.Min(candidate.Right, placed.Right) - Math.Max(candidate.Left, placed.Left);
        return overlap > 5;
    }

    /// <summary>
    /// True when a block must be preserved verbatim from the source PDF.
    /// Includes blocks the upstream pipeline already flagged via PreserveOriginalTextInPdfExport
    /// (display formulas) and any block whose type is Formula or TableCell. The export honors
    /// this via PlannedPageBlock.IsPreserved (no erase, no text generation).
    /// </summary>
    internal static bool IsPreservedBlock(TranslatedBlockData block) =>
        block.PreserveOriginalTextInPdfExport ||
        block.SourceBlockType is SourceBlockType.Formula
            or SourceBlockType.TableCell;

    private static bool IsLayoutEligible(TranslatedBlockData block) => !IsPreservedBlock(block);

    /// <summary>
    /// Phase 0: walk the page once and promote any non-preserved short header /
    /// label block that sits inside or immediately next to an already-preserved
    /// block (numeric-data row, formula, etc). Returns a new list with promoted
    /// blocks rewritten as <c>PreserveOriginalTextInPdfExport=true,
    /// TranslationSkipped=true</c>.
    ///
    /// Two predicates have to fire together:
    ///   1. The candidate's source bbox is vertically close to a preserved block
    ///      (overlap, or vertical gap &lt;= ~24pt). Translates to "in the same
    ///      visual table block".
    ///   2. The candidate's text "looks like" a header / row label / short
    ///      identifier — no natural-language word of 7+ letters and a low total
    ///      letter count. Prevents prose paragraphs near a formula from being
    ///      silently dropped from translation.
    /// </summary>
    private static IReadOnlyList<TranslatedBlockData> PromoteTableNeighbors(
        IReadOnlyList<TranslatedBlockData> blocks,
        double pageHeightPoints)
    {
        // Step 1: collect top-left rects of every already-preserved block.
        var preservedRects = new List<XRect>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (!IsPreservedBlock(b)) continue;
            if (b.BoundingBox is not BlockRect bbox) continue;
            preservedRects.Add(ToTopLeftRect(pageHeightPoints, bbox));
        }

        if (preservedRects.Count == 0)
            return blocks;

        const double VerticalAdjacencyTolerance = 24.0;

        var promoted = new TranslatedBlockData[blocks.Count];
        var anyChanged = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            promoted[i] = block;

            if (IsPreservedBlock(block)) continue;
            if (block.BoundingBox is not BlockRect bbox) continue;
            if (!LooksLikeShortTableLabel(block.SourceText ?? block.TranslatedText ?? "")) continue;

            var rect = ToTopLeftRect(pageHeightPoints, bbox);

            var nearPreserved = false;
            foreach (var p in preservedRects)
            {
                // Vertical adjacency: rects either overlap vertically or are
                // separated by no more than VerticalAdjacencyTolerance points.
                var verticalGap = Math.Max(0, Math.Max(rect.Top - p.Bottom, p.Top - rect.Bottom));
                if (verticalGap > VerticalAdjacencyTolerance) continue;

                // Horizontal proximity: rects either overlap horizontally or share
                // some column space within the table region (within 30pt).
                var horizontalGap = Math.Max(0, Math.Max(rect.Left - p.Right, p.Left - rect.Right));
                if (horizontalGap > 30.0) continue;

                nearPreserved = true;
                break;
            }

            if (!nearPreserved) continue;

            promoted[i] = block with
            {
                PreserveOriginalTextInPdfExport = true,
                TranslationSkipped = true,
            };
            anyChanged = true;
        }

        return anyChanged ? promoted : blocks;
    }

    /// <summary>
    /// Heuristic: returns true when the text is a plausible table header / row
    /// label / short identifier — i.e. ≤ 40 characters, contains no
    /// natural-language word of 7+ letters, and the total letter count is small
    /// enough to rule out short prose. Used together with the spatial-adjacency
    /// check above; this filter alone is intentionally permissive.
    /// </summary>
    private static bool LooksLikeShortTableLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40) return false;

        var letterCount = 0;
        var currentRun = 0;
        var maxRun = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsLetter(ch))
            {
                letterCount++;
                currentRun++;
                if (currentRun > maxRun) maxRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }

        // Reject prose: any 7+ letter natural word disqualifies (e.g. "Section",
        // "Variations", "Transformer", "introduces"). Allows "params" (6).
        if (maxRun >= 7) return false;

        // Reject sentences: too many letters total means it's not a label.
        if (letterCount > 30) return false;

        return true;
    }

    private static double GetLayoutGap(TranslatedBlockData block)
    {
        var fontSize = block.FontSize > 0
            ? block.FontSize
            : (block.TextStyle?.FontSize > 0 ? block.TextStyle.FontSize : 10.0);
        return Math.Clamp(fontSize * 0.15, 1.5, 6);
    }

    #endregion

    #region Phase 4: Generate

    private static PlannedPageBlock BuildPassthroughBlock(
        TranslatedBlockData block,
        double pageHeightPoints)
    {
        // Preserved (passthrough) block: the source PDF content under this block's
        // bbox must show through verbatim. EraseRects MUST be null — generating
        // a white-rect erase here would cover the original cell text / formula
        // glyphs and leave the area visually empty.
        return new PlannedPageBlock
        {
            Block = block,
            IsPreserved = true,
            LayoutBoundingBox = block.BoundingBox,
            LayoutRenderLineRects = block.RenderLineRects,
            LayoutBackgroundLineRects = block.BackgroundLineRects,
            EraseRects = null,
            TopLeftBounds = block.BoundingBox is BlockRect bbox
                ? ToTopLeftRect(pageHeightPoints, bbox)
                : null,
            PlannedOperations = null,
            PlannedChosenFontSize = 0,
            PlannedLinesRendered = 0,
            PlannedWasShrunk = false,
            PlannedWasTruncated = false,
            RenderableText = PrepareRenderableTextForPdf(block.TranslatedText),
            UsedGlyphs = null,
        };
    }

    private static PlannedPageBlock BuildConstrainedBlock(
        TranslatedBlockData block,
        double pageHeightPoints)
    {
        return new PlannedPageBlock
        {
            Block = block,
            IsPreserved = false,
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
            UsedGlyphs = null,
        };
    }

    private static PlannedPageBlock BuildPlannedBlockFromLayout(
        TranslatedBlockData block,
        double pageHeightPoints,
        XRect sourceBoundsTopLeft,
        IReadOnlyList<XRect>? preferredEraseRectsTopLeft,
        PlannedRetryTextLayout layout)
    {
        var renderBoundsTopLeft = GetBounds(layout.RenderRectsTopLeft);
        var sourceEraseRectsTopLeft = preferredEraseRectsTopLeft is { Count: > 0 }
            ? preferredEraseRectsTopLeft
            : [sourceBoundsTopLeft];
        var eraseRectsTopLeft = BuildFinalEraseRectsTopLeft(sourceEraseRectsTopLeft, layout.RenderRectsTopLeft);
        var eraseRects = MuPdfExportService.ToBottomUpRects(pageHeightPoints, eraseRectsTopLeft);

        return new PlannedPageBlock
        {
            Block = block,
            IsPreserved = false,
            LayoutBoundingBox = MuPdfExportService.ToBottomUpRect(pageHeightPoints, renderBoundsTopLeft),
            LayoutRenderLineRects = layout.RenderLineRects,
            LayoutBackgroundLineRects = eraseRects,
            EraseRects = eraseRects,
            TopLeftBounds = renderBoundsTopLeft,
            PlannedOperations = layout.Operations,
            PlannedChosenFontSize = layout.ChosenFontSize,
            PlannedLinesRendered = layout.LinesRendered,
            PlannedWasShrunk = layout.WasShrunk,
            PlannedWasTruncated = layout.WasTruncated,
            RenderableText = layout.RenderableText,
            UsedGlyphs = layout.UsedGlyphs,
        };
    }

    private static PlannedPageBlock BuildPlannedBlock(
        TranslatedBlockData block,
        double top,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts,
        XRect sourceBoundsTopLeft,
        IReadOnlyList<XRect>? preferredRenderRectsTopLeft,
        IReadOnlyList<XRect>? preferredEraseRectsTopLeft)
    {
        var layout = PlanBlockTextLayout(
            block, top, pageHeightPoints, fontId, fonts,
            sourceBoundsTopLeft, preferredRenderRectsTopLeft);
        return BuildPlannedBlockFromLayout(
            block, pageHeightPoints, sourceBoundsTopLeft, preferredEraseRectsTopLeft, layout);
    }

    private static PlannedRetryTextLayout PlanBlockTextLayout(
        TranslatedBlockData block,
        double top,
        double pageHeightPoints,
        string fontId,
        EmbeddedFontInfo fonts,
        XRect sourceBoundsTopLeft,
        IReadOnlyList<XRect>? preferredRenderRectsTopLeft)
    {
        var renderableText = PrepareRenderableTextForPdf(block.TranslatedText);
        var renderFont = MuPdfExportService.ResolveRenderFontPlan(
            renderableText, fontId, fonts, block.SourceBlockType,
            block.UsesSourceFallback, block.DetectedFontNames, block.TextStyle);

        var originalFontSize = block.FontSize > 0 ? block.FontSize : 10.0;
        var baseLineHeight = block.UsesSourceFallback && block.TextStyle?.LineSpacing > 0
            ? block.TextStyle.LineSpacing
            : originalFontSize * 1.2;
        var lineHeightMultiplier = originalFontSize > 0
            ? Math.Max(1.0, baseLineHeight / originalFontSize)
            : 1.2;

        var availableHeight = Math.Max(MinFontSize, pageHeightPoints - top);
        var baseWidths = preferredRenderRectsTopLeft is { Count: > 0 }
            ? preferredRenderRectsTopLeft.Select(r => Math.Max(10, r.Width)).ToList()
            : [Math.Max(10, sourceBoundsTopLeft.Width)];
        var baseXs = preferredRenderRectsTopLeft is { Count: > 0 }
            ? preferredRenderRectsTopLeft.Select(r => r.X).ToList()
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
            size => MuPdfExportService.CreateGlyphMeasurer(renderFont, fonts, size));

        var chosenFontSize = fitResult.ChosenFontSize;
        var prepared = MuPdfExportService.PrepareLayoutParagraph(renderableText, renderFont, fonts, chosenFontSize);
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
            wrappedLines[^1] = MuPdfExportService.TruncateLineToFitWidth(
                wrappedLines[^1], lastWidth, renderFont, fonts, chosenFontSize);
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

        var renderBoundsBottomUp = MuPdfExportService.ToBottomUpRect(
            pageHeightPoints, GetBounds(renderRectsTopLeft));
        var renderLineRects = MuPdfExportService.ToBottomUpRects(pageHeightPoints, renderRectsTopLeft)
            ?? Array.Empty<BlockRect>();
        var usedGlyphs = new List<UsedGlyph>();
        var operations = MuPdfExportService.BuildBlockTextOperationsFromLines(
            wrappedLines, chosenFontSize, renderFont, fonts,
            block.TextStyle, renderBoundsBottomUp, renderLineRects, lineHeight, usedGlyphs);

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
            UsedGlyphs = usedGlyphs,
        };
    }

    #endregion
}
