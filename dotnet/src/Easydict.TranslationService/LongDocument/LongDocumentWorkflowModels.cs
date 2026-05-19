using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

public enum LongDocumentInputMode
{
    PlainText,
    Markdown,
    Pdf
}

public enum LongDocumentJobState
{
    Completed,
    PartialSuccess,
    Failed
}

public enum LayoutRegionType
{
    Unknown,
    Header,
    Footer,
    Body,
    LeftColumn,
    RightColumn,
    TableLike,
    Figure,
    Table,
    Formula,
    Caption,
    Title,
    IsolatedFormula
}

public enum LayoutRegionSource
{
    Unknown,
    Heuristic,
    BlockIdFallback,
    OnnxModel,
    VisionLLM
}

public sealed class LongDocumentTranslationCheckpoint
{
    public required LongDocumentInputMode InputMode { get; init; }
    public string? SourceFilePath { get; init; }
    public Language? TargetLanguage { get; init; }
    public string? PageRange { get; init; }
    public required List<string> SourceChunks { get; init; }
    public required List<LongDocumentChunkMetadata> ChunkMetadata { get; init; }
    public required Dictionary<int, string> TranslatedChunks { get; init; }
    public required HashSet<int> FailedChunkIndexes { get; init; }

    /// <summary>
    /// Optional per-chunk annotations for source-fallback blocks. The default PDF export
    /// pipeline does not currently populate or render these.
    /// </summary>
    public Dictionary<int, IReadOnlyList<WordAnnotation>>? WordAnnotations { get; set; }
}

public sealed class LongDocumentChunkMetadata
{
    public required int ChunkIndex { get; init; }
    public required int PageNumber { get; init; }
    public required string SourceBlockId { get; init; }
    public required SourceBlockType SourceBlockType { get; init; }
    public bool IsFormulaLike { get; init; }
    public required int OrderInPage { get; init; }
    public required LayoutRegionType RegionType { get; init; }
    public double RegionConfidence { get; init; }
    public LayoutRegionSource RegionSource { get; init; }
    public double ReadingOrderScore { get; init; }
    public BlockRect? BoundingBox { get; init; }
    public BlockTextStyle? TextStyle { get; init; }
    public BlockFormulaCharacters? FormulaCharacters { get; init; }
    public bool TranslationSkipped { get; init; }
    public bool PreserveOriginalTextInPdfExport { get; init; }
    public int RetryCount { get; set; }
    public string? FallbackText { get; init; }
    public IReadOnlyList<string>? DetectedFontNames { get; init; }
}
