namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Outcome of a formula restoration attempt.
/// </summary>
public enum FormulaRestoreStatus
{
    /// <summary>All placeholders were present in the translated text and were restored successfully.</summary>
    FullRestore,
    /// <summary>Some placeholders were dropped by the LLM but ≥50% were present; best-effort restore performed.</summary>
    PartialRestore,
    /// <summary>Restoration failed (no placeholders, &lt;50% present, or post-restore corruption); original text returned.</summary>
    FallbackToOriginal,
}

/// <summary>
/// Detailed result of <see cref="FormulaRestorer.RestoreWithDiagnostics"/>.
/// Carries the restored text plus diagnostics so callers can react to partial/fallback outcomes
/// (e.g. retry translation with softer protection).
/// </summary>
public sealed record FormulaRestoreResult(
    string Text,
    FormulaRestoreStatus Status,
    int DroppedCount,
    IReadOnlyList<int> MissingIndices);
