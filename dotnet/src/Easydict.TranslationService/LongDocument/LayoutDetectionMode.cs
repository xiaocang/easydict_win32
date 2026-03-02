namespace Easydict.TranslationService.LongDocument;

/// <summary>
/// Layout detection strategy for long document translation.
/// </summary>
public enum LayoutDetectionMode
{
    /// <summary>Pure heuristic detection (line spacing, quartile analysis).</summary>
    Heuristic,

    /// <summary>Local ONNX model (DocLayout-YOLO) inference.</summary>
    OnnxLocal,

    /// <summary>Online vision LLM (GPT-4V, Gemini Vision, etc.).</summary>
    VisionLLM,

    /// <summary>Auto: prefer ONNX local → fallback to heuristic.</summary>
    Auto
}
