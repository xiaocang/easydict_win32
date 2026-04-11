using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

/// <summary>
/// Optional annotation metadata for source-fallback blocks: a difficult word paired with
/// its short translation. The default PDF fallback path no longer renders these inline,
/// but the model is kept for diagnostics and any future optional UI/export surfaces.
/// </summary>
public sealed record WordAnnotation(string Word, string Translation);

public enum SourceBlockType
{
    Paragraph,
    Heading,
    Caption,
    TableCell,
    Formula,
    Unknown
}

public enum BlockType
{
    Paragraph,
    Heading,
    Caption,
    Table,
    Formula,
    Unknown
}

public readonly record struct BlockRect(double X, double Y, double Width, double Height);

/// <summary>
/// Text alignment detected from source PDF line positions.
/// </summary>
public enum TextAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Text styling extracted from source PDF letters within a block.
/// Values are aggregated (median font size, majority vote for bold/italic, average color).
/// </summary>
public sealed record BlockTextStyle
{
    /// <summary>Median font size in points from source PDF letters.</summary>
    public double FontSize { get; init; }

    /// <summary>Whether the majority of letters use a bold font.</summary>
    public bool IsBold { get; init; }

    /// <summary>Whether the majority of letters use an italic font.</summary>
    public bool IsItalic { get; init; }

    /// <summary>Average red component (0–255) of source text color.</summary>
    public byte ColorR { get; init; }

    /// <summary>Average green component (0–255) of source text color.</summary>
    public byte ColorG { get; init; }

    /// <summary>Average blue component (0–255) of source text color.</summary>
    public byte ColorB { get; init; }

    /// <summary>Text alignment detected from line positions within the block.</summary>
    public TextAlignment Alignment { get; init; }

    /// <summary>Median baseline-to-baseline distance between lines (0 if single-line block).</summary>
    public double LineSpacing { get; init; }

    /// <summary>Per-line baseline coordinates from the source PDF (PDF coordinate space).</summary>
    public IReadOnlyList<BlockLinePosition>? LinePositions { get; init; }

    /// <summary>Rotation angle in degrees. 0 = normal horizontal text, -90 = vertical sidebar text (read bottom-to-top).</summary>
    public double RotationAngle { get; init; }

    /// <summary>True if the color is effectively black (all components ≤ 30).</summary>
    public bool IsBlack => ColorR <= 30 && ColorG <= 30 && ColorB <= 30;
}

/// <summary>
/// Baseline position of a single text line within a block, in PDF coordinate space.
/// </summary>
public readonly record struct BlockLinePosition(double BaselineY, double Left, double Right);

/// <summary>
/// Per-character font/position data extracted from a PDF letter within a formula block.
/// Used for multi-font rendering and subscript/superscript detection in PDF export.
/// </summary>
public readonly record struct FormulaCharacterInfo(
    string Value,
    string FontName,
    double PointSize,
    double GlyphLeft,
    double GlyphBottom,
    double GlyphWidth,
    double GlyphHeight,
    bool IsMathFont,
    bool IsSubscript,
    bool IsSuperscript);

/// <summary>
/// Aggregated formula character data for a block, including per-character info
/// and block-level statistics for rendering decisions.
/// </summary>
public sealed record BlockFormulaCharacters
{
    public required IReadOnlyList<FormulaCharacterInfo> Characters { get; init; }
    public double MedianTextFontSize { get; init; }
    public double MedianBaselineY { get; init; }
    public bool HasMathFontCharacters { get; init; }
}

public sealed record SourceDocumentBlock
{
    public required string BlockId { get; init; }
    public required SourceBlockType BlockType { get; init; }
    public required string Text { get; init; }
    public BlockRect? BoundingBox { get; init; }
    public string? ParentBlockId { get; init; }
    public bool IsFormulaLike { get; init; }
    public IReadOnlyList<string>? DetectedFontNames { get; init; }
    public BlockTextStyle? TextStyle { get; init; }
    public BlockFormulaCharacters? FormulaCharacters { get; init; }
    /// <summary>
    /// Character-level protected text with formula spans replaced by {v0}, {v1}, ...
    /// Populated by CharacterParagraphBuilder when character-level analysis is available.
    /// When set, FormulaPreservationService prefers this over regex-based detection.
    /// </summary>
    public string? CharacterLevelProtectedText { get; init; }
    /// <summary>
    /// Formula tokens from character-level analysis, paired with CharacterLevelProtectedText.
    /// </summary>
    public IReadOnlyList<FormulaProtection.FormulaToken>? CharacterLevelTokens { get; init; }
    /// <summary>
    /// PdfPig's original line-joined text without formula-aware reconstruction.
    /// Used as retry fallback when the primary <see cref="Text"/> causes translation failure.
    /// Preserves correct word spacing but lacks _/^ script markers.
    /// </summary>
    public string? FallbackText { get; init; }
}

public sealed record SourceDocumentPage
{
    public required int PageNumber { get; init; }
    public required IReadOnlyList<SourceDocumentBlock> Blocks { get; init; }
    public bool IsScanned { get; init; }
}

public sealed record SourceDocument
{
    public required string DocumentId { get; init; }
    public required IReadOnlyList<SourceDocumentPage> Pages { get; init; }
}

public sealed record DocumentBlockIr
{
    public required string IrBlockId { get; init; }
    public required int PageNumber { get; init; }
    public required string SourceBlockId { get; init; }
    public required BlockType BlockType { get; init; }
    public required string OriginalText { get; init; }
    public required string ProtectedText { get; init; }
    public required string SourceHash { get; init; }
    public BlockRect? BoundingBox { get; init; }
    public string? ParentIrBlockId { get; init; }
    public bool TranslationSkipped { get; init; }
    public BlockTextStyle? TextStyle { get; init; }
    public BlockFormulaCharacters? FormulaCharacters { get; init; }
    /// <summary>
    /// Formula token map produced during the protection phase.
    /// Stored here so restoration reuses the same tokens without re-running detection.
    /// </summary>
    public IReadOnlyList<FormulaProtection.FormulaToken>? FormulaTokenMap { get; init; }
    /// <summary>
    /// Low-confidence inline spans that remain in the translation request and are
    /// validated after translation.
    /// </summary>
    public IReadOnlyList<ContentPreservation.SoftProtectedSpan>? SoftProtectedSpans { get; init; }
    /// <summary>Character-level protected text from CharacterParagraphBuilder (if available).</summary>
    public string? CharacterLevelProtectedText { get; init; }
    /// <summary>Character-level formula tokens from CharacterParagraphBuilder (if available).</summary>
    public IReadOnlyList<FormulaProtection.FormulaToken>? CharacterLevelTokens { get; init; }
    /// <summary>
    /// Preservation context captured during the initial Protect pass. Used by the retry loop
    /// in <c>TranslateSingleBlockAsync</c> to re-run <see cref="ContentPreservation.IContentPreservationService.Protect"/>
    /// with <see cref="ContentPreservation.BlockContext.RetryAttempt"/> incremented. Null when the
    /// block was built from a test harness or non-PDF source that doesn't carry parser signals.
    /// </summary>
    public ContentPreservation.BlockContext? PreservationContext { get; init; }
    /// <summary>
    /// Fallback text from <see cref="SourceDocumentBlock.FallbackText"/>. Carried through
    /// IR so the retry loop can re-protect and re-translate with PdfPig's original text
    /// when the primary text causes translation failure.
    /// </summary>
    public string? FallbackText { get; init; }
    /// <summary>
    /// True when PDF export should keep the original page text operators for this block
    /// instead of redrawing translated text. Used for hard-confirmed formulas.
    /// </summary>
    public bool PreserveOriginalTextInPdfExport { get; init; }
}

public sealed record DocumentIr
{
    public required string DocumentId { get; init; }
    public required IReadOnlyList<DocumentBlockIr> Blocks { get; init; }
}

public sealed record TranslatedDocumentPage
{
    public required int PageNumber { get; init; }
    public required IReadOnlyList<TranslatedDocumentBlock> Blocks { get; init; }
}

public sealed record TranslatedDocumentBlock
{
    public required string IrBlockId { get; init; }
    public required string SourceBlockId { get; init; }
    public required BlockType BlockType { get; init; }
    public required string OriginalText { get; init; }
    public required string ProtectedText { get; init; }
    public required string TranslatedText { get; init; }
    public required string SourceHash { get; init; }
    public BlockRect? BoundingBox { get; init; }
    public bool TranslationSkipped { get; init; }
    public int RetryCount { get; init; }
    public string? LastError { get; init; }
    public BlockTextStyle? TextStyle { get; init; }
    public BlockFormulaCharacters? FormulaCharacters { get; init; }
    /// <summary>
    /// True when PDF export should preserve this block's original PDF text content instead of redrawing it.
    /// </summary>
    public bool PreserveOriginalTextInPdfExport { get; init; }
}

/// <summary>
/// Result of the optional Pass 1 "document context" extraction. The LLM reads the
/// whole document page-by-page (no truncation) and produces a glossary, a 1-3 sentence
/// summary, and a list of source-text snippets that should NOT be translated. Pass 2
/// then prepends Summary + Glossary to every per-block prompt, and the IR is rewritten
/// so that any block matching a preservation hint is marked
/// <c>TranslationSkipped = true, PreserveOriginalTextInPdfExport = true</c>.
/// </summary>
public sealed record DocumentContext
{
    /// <summary>1-3 sentence overview of topic, domain, terminology style.</summary>
    public required string Summary { get; init; }

    /// <summary>Source-language term → chosen target-language rendering.</summary>
    public required IReadOnlyDictionary<string, string> Glossary { get; init; }

    /// <summary>
    /// LLM-suggested verbatim source snippets that should bypass translation: code,
    /// commands, URLs, identifiers, table cells, product names, etc.
    /// </summary>
    public required IReadOnlyList<string> PreservationHints { get; init; }

    /// <summary>Total wall time in milliseconds spent extracting this context.</summary>
    public long ExtractionTimeMs { get; init; }

    /// <summary>An empty context — used as the graceful-degradation fallback.</summary>
    public static DocumentContext Empty { get; } = new()
    {
        Summary = string.Empty,
        Glossary = new Dictionary<string, string>(),
        PreservationHints = Array.Empty<string>(),
        ExtractionTimeMs = 0,
    };
}

public sealed record LongDocumentTranslationOptions
{
    public string ServiceId { get; init; } = "google";
    public Language FromLanguage { get; init; } = Language.Auto;
    public required Language ToLanguage { get; init; }
    public bool EnableFormulaProtection { get; init; } = true;
    /// <summary>
    /// When true, run a Pass 1 page-by-page LLM read of the document to extract a
    /// glossary, summary, and preservation hints, then prepend them to every Pass 2
    /// translation prompt. Adds latency (one LLM round-trip per page + one reduce
    /// call) but improves terminology consistency and protects table/code regions
    /// the ML detector misses. Default false at the core service level so unit tests
    /// see baseline single-pass behavior; the WinUI wrapper enables it explicitly
    /// from <c>SettingsService.LongDocEnableDocumentContextPass</c> (default true).
    /// </summary>
    public bool EnableDocumentContextPass { get; init; } = false;
    public bool EnableOcrFallback { get; init; } = true;
    public int MaxRetriesPerBlock { get; init; } = 1;
    /// <summary>
    /// When true, the retry loop in <c>TranslateSingleBlockAsync</c> detects partial-restore
    /// (LLM dropped <c>{vN}</c> placeholders) and re-runs the block with softer protection
    /// (demoted confidence) before giving up. Shares the <see cref="MaxRetriesPerBlock"/> budget
    /// with exception retries. Default off to preserve existing behavior.
    /// </summary>
    public bool EnableQualityFeedbackRetry { get; init; } = false;
    public IReadOnlyDictionary<string, string>? Glossary { get; init; }
    public LayoutDetectionMode LayoutDetection { get; init; } = LayoutDetectionMode.Auto;
    public int MaxConcurrency { get; init; } = 1;
    public string? FormulaFontPattern { get; init; }
    public string? FormulaCharPattern { get; init; }
    public string? PageRange { get; init; }
    public string? CustomPrompt { get; init; }
    public System.IProgress<LongDocumentTranslationProgress>? Progress { get; init; }
}

/// <summary>
/// Parses page range strings into a set of 1-based page numbers.
/// Supports formats: "1-3,5,7-10", "1-5", "3", "all", null/empty (= all pages).
/// </summary>
public static class PageRangeParser
{
    public static HashSet<int>? Parse(string? pageRange, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(pageRange))
            return null;

        var trimmed = pageRange.Trim();
        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            return null;

        var result = new HashSet<int>();
        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var dashIndex = part.IndexOf('-');
            if (dashIndex > 0 && dashIndex < part.Length - 1)
            {
                if (int.TryParse(part.AsSpan(0, dashIndex), out var start) &&
                    int.TryParse(part.AsSpan(dashIndex + 1), out var end))
                {
                    start = Math.Max(1, start);
                    end = Math.Min(totalPages, end);
                    for (var i = start; i <= end; i++)
                        result.Add(i);
                }
            }
            else if (int.TryParse(part, out var page))
            {
                if (page >= 1 && page <= totalPages)
                    result.Add(page);
            }
        }

        return result.Count > 0 ? result : null;
    }
}

public sealed record FailedBlockInfo
{
    public required string IrBlockId { get; init; }
    public required string SourceBlockId { get; init; }
    public required int PageNumber { get; init; }
    public required int RetryCount { get; init; }
    public required string Error { get; init; }
}

public sealed record BackfillPageMetrics
{
    public required long CandidateBlocks { get; init; }
    public required long RenderedBlocks { get; init; }
    public required long MissingBoundingBoxBlocks { get; init; }
    public required long ShrinkFontBlocks { get; init; }
    public required long TruncatedBlocks { get; init; }
    public required long ObjectReplaceBlocks { get; init; }
    public required long OverlayModeBlocks { get; init; }
    public required long StructuredFallbackBlocks { get; init; }
}

public sealed record BackfillQualityMetrics
{
    public required long CandidateBlocks { get; init; }
    public required long RenderedBlocks { get; init; }
    public required long MissingBoundingBoxBlocks { get; init; }
    public required long ShrinkFontBlocks { get; init; }
    public required long TruncatedBlocks { get; init; }
    public required long ObjectReplaceBlocks { get; init; }
    public required long OverlayModeBlocks { get; init; }
    public required long StructuredFallbackBlocks { get; init; }
    public IReadOnlyDictionary<int, BackfillPageMetrics>? PageMetrics { get; init; }
    public string? RetryMergeStrategy { get; init; }

    /// <summary>
    /// Optional per-block issues encountered during PDF coordinate backfill (e.g., skipped grid blocks, truncation).
    /// Kept optional to avoid bloating reports in normal cases.
    /// </summary>
    public IReadOnlyList<BackfillBlockIssue>? BlockIssues { get; init; }
}

/// <summary>
/// Per-block rendering/backfill issue for diagnostics.
/// </summary>
public sealed record BackfillBlockIssue
{
    public required int ChunkIndex { get; init; }
    public required string SourceBlockId { get; init; }
    public required int PageNumber { get; init; }

    /// <summary>
    /// Short machine-readable kind, e.g. "skipped-rotated", "skipped-grid", "skipped-table-like", "truncated".
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Optional human-readable detail.
    /// </summary>
    public string? Detail { get; init; }
}

public sealed record LongDocumentQualityReport
{
    public required IReadOnlyDictionary<string, long> StageTimingsMs { get; init; }
    public BackfillQualityMetrics? BackfillMetrics { get; init; }
    public required int TotalBlocks { get; init; }
    public required int TranslatedBlocks { get; init; }
    public required int SkippedBlocks { get; init; }
    public required IReadOnlyList<FailedBlockInfo> FailedBlocks { get; init; }
}

public sealed record LongDocumentTranslationResult
{
    public required DocumentIr Ir { get; init; }
    public required IReadOnlyList<TranslatedDocumentPage> Pages { get; init; }
    public required LongDocumentQualityReport QualityReport { get; init; }
}

/// <summary>
/// Progress stage for long document translation.
/// </summary>
public enum LongDocumentTranslationStage
{
    Parsing,
    BuildingIr,
    FormulaProtection,
    DocumentContext,
    Translating,
    Exporting
}

/// <summary>
/// Progress information for long document translation.
/// </summary>
public sealed record LongDocumentTranslationProgress
{
    public required LongDocumentTranslationStage Stage { get; init; }
    public int CurrentBlock { get; init; }
    public int TotalBlocks { get; init; }
    public int CurrentPage { get; init; }
    public int TotalPages { get; init; }
    public double Percentage { get; init; }
    public string? CurrentBlockPreview { get; init; }

    public string GetStageDisplayName() => Stage switch
    {
        LongDocumentTranslationStage.Parsing => "Parsing document",
        LongDocumentTranslationStage.BuildingIr => "Building intermediate representation",
        LongDocumentTranslationStage.FormulaProtection => "Applying formula protection",
        LongDocumentTranslationStage.DocumentContext => "Analyzing document (glossary + summary)",
        LongDocumentTranslationStage.Translating => "Translating blocks",
        LongDocumentTranslationStage.Exporting => "Exporting document",
        _ => "Processing"
    };
}
