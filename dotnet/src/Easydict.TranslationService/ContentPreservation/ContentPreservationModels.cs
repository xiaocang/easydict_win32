using Easydict.TranslationService.FormulaProtection;

namespace Easydict.TranslationService.ContentPreservation;

/// <summary>
/// How a block's preserved content should be handled.
/// </summary>
public enum PreservationMode
{
    /// <summary>Normal text — no special preservation needed.</summary>
    None,

    /// <summary>Text contains inline preserved spans (formulas, etc.) replaced with placeholders.</summary>
    InlineProtected,

    /// <summary>Entire block is opaque (standalone formula, table, etc.) — skip translation.</summary>
    Opaque
}

/// <summary>
/// What kind of content is being preserved.
/// </summary>
public enum ProtectedSpanKind
{
    Formula
}

/// <summary>
/// Describes evidence that a span should be protected.
/// </summary>
/// <param name="Start">Character offset in original text.</param>
/// <param name="Length">Span length.</param>
/// <param name="Kind">What kind of content this is.</param>
/// <param name="Confidence">0–1 confidence score.</param>
/// <param name="Source">Detection source: Regex, MathFont, UnicodeMath, ScriptShift, LayoutExcluded, VerticalTM, CID.</param>
public sealed record SpanEvidence(
    int Start,
    int Length,
    ProtectedSpanKind Kind,
    double Confidence,
    string Source);

/// <summary>
/// The decision about how to handle a block's content preservation.
/// </summary>
public sealed record ProtectionPlan
{
    public required PreservationMode Mode { get; init; }
    public required bool SkipTranslation { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// The result of applying content protection to a block.
/// </summary>
public sealed record ProtectedBlock
{
    public required string OriginalText { get; init; }
    public required string ProtectedText { get; init; }
    public required IReadOnlyList<FormulaToken> Tokens { get; init; }
    public required ProtectionPlan Plan { get; init; }
}

/// <summary>
/// Status of a content restoration operation.
/// </summary>
public enum RestoreStatus
{
    /// <summary>All placeholders restored successfully.</summary>
    FullRestore,

    /// <summary>Some placeholders missing but ≥50% present; best-effort restore performed.</summary>
    PartialRestore,

    /// <summary>Restoration failed; fell back to original text.</summary>
    FallbackToOriginal
}

/// <summary>
/// The result of restoring protected content in translated text.
/// </summary>
public sealed record RestoreOutcome
{
    public required string Text { get; init; }
    public required RestoreStatus Status { get; init; }
    public int MissingTokenCount { get; init; }
}
