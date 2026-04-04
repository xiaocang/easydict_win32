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
}
