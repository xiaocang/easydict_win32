using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

public enum DocumentBlockType
{
    Paragraph,
    Caption,
    Table,
    Formula,
    FormulaPlaceholder,
    Heading,
    Unknown,
}

public sealed record DocumentCoordinates(float X, float Y, float Width, float Height);

public sealed record SourceDocumentBlock
{
    public required string Id { get; init; }
    public required int PageNumber { get; init; }
    public required int ReadingOrder { get; init; }
    public required DocumentBlockType BlockType { get; init; }
    public required string Text { get; init; }
    public DocumentCoordinates? Coordinates { get; init; }
    public string? ParentBlockId { get; init; }
}

public sealed record SourceDocumentPage
{
    public required int PageNumber { get; init; }
    public required IReadOnlyList<SourceDocumentBlock> Blocks { get; init; }
}

public sealed record LongDocumentTranslationRequest
{
    public required Language FromLanguage { get; init; }
    public required Language ToLanguage { get; init; }
    public required IReadOnlyList<SourceDocumentPage> Pages { get; init; }
    public bool IsScannedPdf { get; init; }
    public IReadOnlyList<SourceDocumentPage>? OcrFallbackPages { get; init; }
    public LongDocumentTranslationOptions Options { get; init; } = new();
}

public sealed record LongDocumentTranslationOptions
{
    public bool EnableOcrFallback { get; init; } = true;
    public bool EnableGlossaryConsistency { get; init; }
    public IReadOnlyDictionary<string, string>? Glossary { get; init; }
    public int MaxRetriesPerBlock { get; init; } = 1;
    public int TimeoutMs { get; init; } = 30000;
}

public sealed record DocumentBlockIr
{
    public required string Id { get; init; }
    public required int PageNumber { get; init; }
    public required int ReadingOrder { get; init; }
    public required DocumentBlockType BlockType { get; init; }
    public required string Text { get; init; }
    public required string SourceHash { get; init; }
    public DocumentCoordinates? Coordinates { get; init; }
    public string? ParentBlockId { get; init; }

    public static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}

public sealed record DocumentPageIr
{
    public required int PageNumber { get; init; }
    public required IReadOnlyList<DocumentBlockIr> Blocks { get; init; }
}

public sealed record DocumentIr
{
    public required IReadOnlyList<DocumentPageIr> Pages { get; init; }
    public bool UsedOcrFallback { get; init; }
}

public sealed record TranslatedDocumentBlock
{
    public required string Id { get; init; }
    public required int PageNumber { get; init; }
    public required int ReadingOrder { get; init; }
    public required DocumentBlockType BlockType { get; init; }
    public required string SourceText { get; init; }
    public required string SourceHash { get; init; }
    public required string TranslatedText { get; init; }
    public DocumentCoordinates? Coordinates { get; init; }
    public string? ParentBlockId { get; init; }
}

public sealed record TranslatedDocumentPage
{
    public required int PageNumber { get; init; }
    public required IReadOnlyList<TranslatedDocumentBlock> Blocks { get; init; }
}

public sealed record FailedDocumentBlock(int PageNumber, string BlockId, int Attempts, string ErrorMessage);
public sealed record StageTiming(string Stage, long DurationMs);

public sealed record LongDocumentQualityReport
{
    public required IReadOnlyList<int> FailedPages { get; init; }
    public required IReadOnlyList<FailedDocumentBlock> FailedBlocks { get; init; }
    public required int RetryCount { get; init; }
    public required IReadOnlyList<StageTiming> StageTimings { get; init; }
}

public sealed record LongDocumentTranslationResult
{
    public required DocumentIr IntermediateRepresentation { get; init; }
    public required IReadOnlyList<TranslatedDocumentPage> Pages { get; init; }
    public required string StructuredOutputText { get; init; }
    public required LongDocumentQualityReport QualityReport { get; init; }
}

internal static partial class FormulaPatterns
{
    [GeneratedRegex("\\$\\$(?<m>[\\s\\S]*?)\\$\\$", RegexOptions.Compiled)]
    public static partial Regex DisplayMathRegex();

    [GeneratedRegex("\\\\\\[(?<m>[\\s\\S]*?)\\\\\\]", RegexOptions.Compiled)]
    public static partial Regex BracketMathRegex();
}

internal static class DocumentPipelineStopwatch
{
    public static (T Value, StageTiming Timing) Measure<T>(string stage, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        var value = action();
        sw.Stop();
        return (value, new StageTiming(stage, sw.ElapsedMilliseconds));
    }

    public static async Task<(T Value, StageTiming Timing)> MeasureAsync<T>(string stage, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        var value = await action().ConfigureAwait(false);
        sw.Stop();
        return (value, new StageTiming(stage, sw.ElapsedMilliseconds));
    }
}
