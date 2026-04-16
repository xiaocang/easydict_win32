namespace Easydict.WinUI.Models;

/// <summary>
/// Specifies the OCR engine to use for text recognition.
/// </summary>
public enum OcrEngineType
{
    /// <summary>
    /// Use the built-in Windows OCR engine (Windows.Media.Ocr).
    /// </summary>
    WindowsNative = 0,

    /// <summary>
    /// Use a local VLM model via Ollama (e.g., glm-ocr, llama3-vision).
    /// </summary>
    Ollama = 1,

    /// <summary>
    /// Use a custom OpenAI-compatible vision API.
    /// </summary>
    CustomApi = 2
}
