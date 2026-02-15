using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

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

public sealed record SourceDocumentBlock
{
    public required string BlockId { get; init; }
    public required SourceBlockType BlockType { get; init; }
    public required string Text { get; init; }
    public BlockRect? BoundingBox { get; init; }
    public string? ParentBlockId { get; init; }
    public bool IsFormulaLike { get; init; }
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
}

public sealed record LongDocumentTranslationOptions
{
    public string ServiceId { get; init; } = "google";
    public Language FromLanguage { get; init; } = Language.Auto;
    public required Language ToLanguage { get; init; }
    public bool EnableFormulaProtection { get; init; } = true;
    public bool EnableOcrFallback { get; init; } = true;
    public int MaxRetriesPerBlock { get; init; } = 1;
    public IReadOnlyDictionary<string, string>? Glossary { get; init; }
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
