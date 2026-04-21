using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// OCR service using a local Ollama VLM model via the /api/generate endpoint.
/// Sends the captured image as base64 and extracts text from the model's response.
/// </summary>
public sealed class OllamaOcrService : IOcrService
{
    private readonly HttpClient _httpClient;
    private readonly OcrServiceOptions _options;

    public string ServiceId => "ollama_ocr";

    public string DisplayName => "Ollama OCR";

    public bool IsAvailable => true;

    public OllamaOcrService(HttpClient httpClient)
        : this(httpClient, OcrServiceOptions.FromSettings(SettingsService.Instance))
    {
    }

    public OllamaOcrService(HttpClient httpClient, OcrServiceOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(
        byte[] pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixelData);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var expectedLength = pixelWidth * pixelHeight * 4; // BGRA8
        if (pixelData.Length < expectedLength)
            throw new ArgumentException(
                $"pixelData length ({pixelData.Length}) is less than expected ({expectedLength}) for {pixelWidth}x{pixelHeight} BGRA8",
                nameof(pixelData));

        var endpoint = _options.Endpoint;
        var model = _options.Model;
        var prompt = _options.SystemPrompt;

        Debug.WriteLine($"[OllamaOcr] Sending {pixelWidth}x{pixelHeight} image to {endpoint} (model: {model})");

        // Convert BGRA8 pixel data to base64-encoded BMP
        var base64Image = ConvertBgraToBase64Bmp(pixelData, pixelWidth, pixelHeight);

        // Build Ollama /api/generate request
        var requestBody = new
        {
            model,
            prompt,
            images = new[] { base64Image },
            stream = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var text = ParseOllamaResponse(json);

        Debug.WriteLine($"[OllamaOcr] Recognized {text.Length} chars");

        return new OcrResult
        {
            Text = text,
            Lines = [],
            TextAngle = null,
            DetectedLanguage = null
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => [];

    /// <summary>
    /// Extracts text from the Ollama /api/generate JSON response.
    /// Response format: { "response": "extracted text..." }
    /// </summary>
    private static string ParseOllamaResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("response").GetString()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OllamaOcr] Failed to parse response: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Convert BGRA8 pixel data to a base64-encoded BMP string.
    /// Uses a simple uncompressed BMP → base64 approach for portability.
    /// </summary>
    private static string ConvertBgraToBase64Bmp(byte[] bgra, int width, int height)
    {
        // Create a BMP file in memory (simpler than PNG, widely supported by vision APIs)
        var bmpHeaderSize = 54;
        var rowStride = ((width * 3 + 3) / 4) * 4; // BMP rows are 4-byte aligned
        var imageDataSize = rowStride * height;
        var bmpSize = bmpHeaderSize + imageDataSize;

        var bmp = new byte[bmpSize];

        // BMP file header
        bmp[0] = 0x42; bmp[1] = 0x4D; // 'BM'
        BitConverter.GetBytes(bmpSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(bmpHeaderSize).CopyTo(bmp, 10);

        // DIB header (BITMAPINFOHEADER)
        BitConverter.GetBytes(40).CopyTo(bmp, 14); // header size
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22); // positive = bottom-up
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26); // planes
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28); // bpp
        BitConverter.GetBytes(imageDataSize).CopyTo(bmp, 34);

        // Pixel data (BGRA8 → BGR24, bottom-up)
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * width * 4;
            var dstRow = bmpHeaderSize + (height - 1 - y) * rowStride;
            for (var x = 0; x < width; x++)
            {
                var srcIdx = srcRow + x * 4;
                var dstIdx = dstRow + x * 3;
                bmp[dstIdx] = bgra[srcIdx];         // B
                bmp[dstIdx + 1] = bgra[srcIdx + 1]; // G
                bmp[dstIdx + 2] = bgra[srcIdx + 2]; // R
            }
        }

        return Convert.ToBase64String(bmp);
    }
}
