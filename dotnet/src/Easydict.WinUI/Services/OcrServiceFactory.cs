using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Factory that creates the appropriate <see cref="IOcrService"/> implementation
/// based on the user's <see cref="SettingsService.OcrEngine"/> setting.
/// </summary>
public static class OcrServiceFactory
{
    /// <summary>
    /// Creates an <see cref="IOcrService"/> for the currently configured OCR engine.
    /// </summary>
    /// <param name="httpClient">
    /// Optional shared <see cref="HttpClient"/> for API-based engines.
    /// If null, a new client with a 60-second timeout is created.
    /// </param>
    /// <returns>An <see cref="IOcrService"/> ready to recognize text.</returns>
    public static IOcrService Create(HttpClient? httpClient = null)
    {
        var engine = SettingsService.Instance.OcrEngine;

        return engine switch
        {
            OcrEngineType.Ollama => new OllamaOcrService(
                httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }),
            OcrEngineType.CustomApi => new CustomApiOcrService(
                httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }),
            _ => new WindowsOcrService()
        };
    }
}
