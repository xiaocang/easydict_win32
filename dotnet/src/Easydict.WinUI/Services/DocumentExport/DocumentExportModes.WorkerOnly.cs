namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Controls the PDF rendering strategy for translated output.
/// </summary>
public enum PdfExportMode
{
    /// <summary>
    /// Overlay mode: draws white rectangles over source text, then draws translated text.
    /// </summary>
    Overlay,

    /// <summary>
    /// Content stream replacement mode. In Release artifacts this is performed by
    /// the long-document worker so MuPDF native assets stay out of the host process.
    /// </summary>
    ContentStreamReplacement,
}

/// <summary>
/// Controls the output format for long document translation.
/// </summary>
public enum DocumentOutputMode
{
    /// <summary>Translated-only output.</summary>
    Monolingual,

    /// <summary>Original + translated interleaved.</summary>
    Bilingual,

    /// <summary>Generates both monolingual and bilingual outputs.</summary>
    Both
}
