using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Factory that creates the appropriate <see cref="IOcrService"/> implementation
/// based on the user's <see cref="SettingsService.OcrEngine"/> setting.
/// </summary>
public static class OcrServiceFactory
{
    private static readonly HttpClient _sharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Creates an <see cref="IOcrService"/> for the currently configured OCR engine.
    /// </summary>
    /// <param name="httpClient">
    /// Optional shared <see cref="HttpClient"/> for API-based engines.
    /// If null, a shared client with a 60-second timeout is used.
    /// </param>
    /// <returns>An <see cref="IOcrService"/> ready to recognize text.</returns>
    public static IOcrService Create(HttpClient? httpClient = null)
    {
        var engine = SettingsService.Instance.OcrEngine;
        var client = httpClient ?? _sharedHttpClient;

        return engine switch
        {
            OcrEngineType.Ollama => new OllamaOcrService(client),
            OcrEngineType.CustomApi => new CustomApiOcrService(client),
            _ => new WindowsOcrService()
        };
    }
}
