using Easydict.TranslationService.LongDocument;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Controls the PDF rendering strategy for translated output.
/// </summary>
public enum PdfExportMode
{
    /// <summary>
    /// Overlay mode (PdfSharpCore): draws white rectangles over source text, then draws translated text.
    /// More compatible but lower quality for formula rendering. Default.
    /// </summary>
    Overlay,

    /// <summary>
    /// Content stream replacement mode (MuPDF.NET): replaces the page's content stream,
    /// preserving graphics and embedding fonts. Higher quality, matches pdf2zh output.
    /// Requires MuPDF.NET native binaries.
    /// </summary>
    ContentStreamReplacement,
}

/// <summary>
/// Controls the output format for long document translation (cross-format).
/// </summary>
public enum DocumentOutputMode
{
    /// <summary>Translated-only output (default, current behavior).</summary>
    Monolingual,

    /// <summary>Original + translated interleaved (PDF: interleaved pages; Text/Markdown: interleaved blocks).</summary>
    Bilingual,

    /// <summary>Generates both monolingual and bilingual outputs.</summary>
    Both
}

/// <summary>
/// Result of a document export operation.
/// </summary>
public sealed record DocumentExportResult
{
    /// <summary>Primary output file path (monolingual for Both mode, bilingual for Bilingual mode).</summary>
    public required string OutputPath { get; init; }

    /// <summary>Bilingual output path (non-null for Bilingual and Both modes).</summary>
    public string? BilingualOutputPath { get; init; }

    /// <summary>
    /// Optional export/backfill quality metrics (e.g., PDF coordinate backfill issues).
    /// Null for formats that don't support such metrics.
    /// </summary>
    public BackfillQualityMetrics? BackfillMetrics { get; init; }
}

/// <summary>
/// Interface for exporting translated long documents.
/// Each input format (PDF / Markdown / PlainText) has its own implementation.
/// Output preserves the original format.
/// </summary>
public interface IDocumentExportService
{
    /// <summary>Supported file extensions (with dot), e.g. [".pdf"].</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Exports the translation checkpoint as a document in the same format as the source file.
    /// </summary>
    DocumentExportResult Export(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual);
}
