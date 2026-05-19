using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Methods specific to the long-document translation worker.
/// </summary>
public static class LongDocMethods
{
    /// <summary>
    /// Translate an entire document (PDF / Markdown / TXT). Streams progress,
    /// status, and per-block events. Single response on completion or error.
    /// Long-running — caller must pass timeoutMs &lt;= 0 (infinite).
    /// </summary>
    public const string TranslateDocument = "translate_document";
}

/// <summary>
/// Event names emitted by the long-document worker during translate_document.
/// All events carry id == the originating translate_document request id.
/// </summary>
public static class LongDocEvents
{
    /// <summary>String status update (mirrors the in-proc onProgress callback).</summary>
    public const string Status = "status";

    /// <summary>Structured progress update — payload: LongDocumentTranslationProgress JSON.</summary>
    public const string Progress = "progress";

    /// <summary>Per-block translation completed — payload: BlockTranslatedEventData.</summary>
    public const string BlockTranslated = "block_translated";
}

/// <summary>
/// Parameters for the "translate_document" request.
/// </summary>
public sealed class TranslateDocumentParams
{
    [JsonPropertyName("inputPath")]
    public required string InputPath { get; init; }

    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; init; }

    /// <summary>
    /// One of: "Pdf", "Markdown", "PlainText".
    /// </summary>
    [JsonPropertyName("inputMode")]
    public required string InputMode { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    /// <summary>
    /// One of: "Bilingual", "TargetOnly".
    /// </summary>
    [JsonPropertyName("outputMode")]
    public required string OutputMode { get; init; }

    /// <summary>
    /// One of: "ContentStreamReplacement", "Reconstruct".
    /// </summary>
    [JsonPropertyName("pdfExportMode")]
    public string? PdfExportMode { get; init; }

    /// <summary>
    /// One of: "OnnxLocal", "VisionLlm", "Heuristic".
    /// </summary>
    [JsonPropertyName("layoutDetection")]
    public string? LayoutDetection { get; init; }

    /// <summary>
    /// PDF page range in 1-based numbering, e.g. "1-10" or "all".
    /// </summary>
    [JsonPropertyName("pageRange")]
    public string? PageRange { get; init; }

    [JsonPropertyName("visionEndpoint")]
    public string? VisionEndpoint { get; init; }

    [JsonPropertyName("visionApiKey")]
    public string? VisionApiKey { get; init; }

    [JsonPropertyName("visionModel")]
    public string? VisionModel { get; init; }
}

/// <summary>
/// Result of the "translate_document" request (on success).
/// </summary>
public sealed class TranslateDocumentResult
{
    /// <summary>
    /// One of: "Completed", "PartiallyCompleted", "Failed".
    /// </summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; init; }

    [JsonPropertyName("bilingualOutputPath")]
    public string? BilingualOutputPath { get; init; }

    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; init; }

    [JsonPropertyName("succeededChunks")]
    public int SucceededChunks { get; init; }

    [JsonPropertyName("failedChunkIndexes")]
    public IReadOnlyList<int>? FailedChunkIndexes { get; init; }

    [JsonPropertyName("qualityReport")]
    public string? QualityReport { get; init; }
}

/// <summary>
/// Payload for a "block_translated" event (per-block streaming).
/// </summary>
public sealed class BlockTranslatedEventData
{
    [JsonPropertyName("chunkIndex")]
    public required int ChunkIndex { get; init; }

    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; init; }

    [JsonPropertyName("sourceBlockId")]
    public string? SourceBlockId { get; init; }

    [JsonPropertyName("translatedText")]
    public required string TranslatedText { get; init; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}

/// <summary>
/// Payload for a "status" event. Mirrors the in-proc onProgress string callback.
/// </summary>
public sealed class StatusEventData
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Structured progress envelope used by the "progress" event. The shape mirrors
/// the in-proc LongDocumentTranslationProgress record so the UI's existing
/// IProgress&lt;T&gt; subscribers can consume it without conversion.
/// </summary>
public sealed class ProgressEventData
{
    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("currentBlock")]
    public int CurrentBlock { get; init; }

    [JsonPropertyName("totalBlocks")]
    public int TotalBlocks { get; init; }

    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; init; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; init; }

    [JsonPropertyName("percentage")]
    public double Percentage { get; init; }

    [JsonPropertyName("currentBlockPreview")]
    public string? CurrentBlockPreview { get; init; }
}
