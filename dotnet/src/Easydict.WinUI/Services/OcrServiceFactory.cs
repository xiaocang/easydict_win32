using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Factory that creates the appropriate <see cref="IOcrService"/> implementation
/// for the provided (or currently persisted) OCR configuration.
/// </summary>
public static class OcrServiceFactory
{
    private static readonly HttpClient _sharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Creates an <see cref="IOcrService"/> for the given options.
    /// When <paramref name="options"/> is null, a fresh snapshot of persisted OCR settings is used.
    /// </summary>
    /// <param name="options">OCR engine and request options to use for this service instance.</param>
    /// <param name="httpClient">
    /// Optional shared <see cref="HttpClient"/> for API-based engines.
    /// If null, a shared client with a 60-second timeout is used.
    /// </param>
    /// <returns>An <see cref="IOcrService"/> ready to recognize text.</returns>
    public static IOcrService Create(OcrServiceOptions? options = null, HttpClient? httpClient = null)
    {
        var resolved = options ?? OcrServiceOptions.FromSettings(SettingsService.Instance);
        var client = httpClient ?? _sharedHttpClient;

        return resolved.Engine switch
        {
            OcrEngineType.Ollama => new OllamaOcrService(client, resolved),
            OcrEngineType.CustomApi => new CustomApiOcrService(client, resolved),
            _ => new WindowsOcrService()
        };
    }
}
