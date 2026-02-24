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
    public IReadOnlyList<string>? DetectedFontNames { get; init; }
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
        LongDocumentTranslationStage.Translating => "Translating blocks",
        LongDocumentTranslationStage.Exporting => "Exporting document",
        _ => "Processing"
    };
}
