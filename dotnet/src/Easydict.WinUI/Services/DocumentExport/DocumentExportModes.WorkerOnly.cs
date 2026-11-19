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
    /// Legacy wire/API value retained for compatibility. Requests now use Overlay mode.
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
