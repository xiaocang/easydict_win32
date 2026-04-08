using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.LongDocument;

namespace Easydict.TranslationService.ContentPreservation;

/// <summary>
/// Evidence bundle for a single document block, used by <see cref="IContentPreservationService.Analyze"/>.
/// Carries all upstream signals (block type, fonts, character stats, text) without encoding policy.
/// </summary>
public sealed record BlockContext
{
    /// <summary>Original text of the block.</summary>
    public required string Text { get; init; }

    /// <summary>Source block type from the document parser.</summary>
    public required SourceBlockType BlockType { get; init; }

    /// <summary>Whether the block was flagged as formula-like by the parser.</summary>
    public bool IsFormulaLike { get; init; }

    /// <summary>Font names detected in this block (may contain subset prefixes).</summary>
    public IReadOnlyList<string>? DetectedFontNames { get; init; }

    /// <summary>Character-level formula statistics from the PDF character stream.</summary>
    public BlockFormulaCharacters? FormulaCharacters { get; init; }

    /// <summary>Custom regex pattern for math font matching (user override).</summary>
    public string? FormulaFontPattern { get; init; }

    /// <summary>Custom regex pattern for math character matching (user override).</summary>
    public string? FormulaCharPattern { get; init; }

    /// <summary>
    /// Character-level protected text (from CharacterParagraphBuilder), if available.
    /// When set, FormulaPreservationService prefers this over regex-based detection.
    /// </summary>
    public string? CharacterLevelProtectedText { get; init; }

    /// <summary>
    /// Character-level formula tokens, paired with CharacterLevelProtectedText.
    /// </summary>
    public IReadOnlyList<FormulaToken>? CharacterLevelTokens { get; init; }

    /// <summary>
    /// Retry attempt number, incremented when the translation pipeline re-invokes
    /// <see cref="IContentPreservationService.Protect"/> after detecting placeholder loss.
    /// Level 0 (default) is the first attempt and uses strict confidence split.
    /// Level ≥1 demotes ambiguous formula types (subscripts, superscripts, fractions,
    /// square roots) from hard <c>{vN}</c> protection to soft <c>$...$</c> protection
    /// and bypasses the character-level preemption path.
    /// </summary>
    public int RetryAttempt { get; init; } = 0;

    /// <summary>
    /// Optional source block identifier for DEBUG logging.
    /// </summary>
    public string? DebugBlockId { get; init; }

    /// <summary>
    /// Optional source page number for DEBUG logging.
    /// </summary>
    public int? DebugPageNumber { get; init; }
}
