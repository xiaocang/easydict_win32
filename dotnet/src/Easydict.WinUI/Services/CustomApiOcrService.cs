using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// OCR service using an OpenAI Vision-compatible API.
/// Sends the captured image as a base64 data URL and extracts text from
/// the standard choices[0].message.content response field.
/// </summary>
public sealed class CustomApiOcrService : IOcrService
{
    private readonly HttpClient _httpClient;

    public string ServiceId => "custom_api_ocr";

    public string DisplayName => "Custom API OCR";

    public bool IsAvailable => true;

    public CustomApiOcrService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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

        var settings = SettingsService.Instance;
        var endpoint = settings.OcrEndpoint;
        var model = settings.OcrModel;
        var apiKey = settings.OcrApiKey;
        var systemPrompt = settings.OcrSystemPrompt;

        Debug.WriteLine($"[CustomApiOcr] Sending {pixelWidth}x{pixelHeight} image to {endpoint} (model: {model})");

        // Convert BGRA8 pixel data to base64-encoded JPEG
        var base64Image = await ConvertBgraToBase64JpegAsync(pixelData, pixelWidth, pixelHeight);

        // Build OpenAI Vision-compatible request
        var requestBody = new
        {
            model,
            max_tokens = 2048,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/jpeg;base64,{base64Image}" }
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var text = ParseOpenAIVisionResponse(json);

        Debug.WriteLine($"[CustomApiOcr] Recognized {text.Length} chars");

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
    /// Extracts text from an OpenAI Vision-compatible JSON response.
    /// Response format: { "choices": [{ "message": { "content": "..." } }] }
    /// </summary>
    private static string ParseOpenAIVisionResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomApiOcr] Failed to parse response: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Convert BGRA8 pixel data to a base64-encoded JPEG string.
    /// Uses Windows.Graphics.Imaging for high-quality encoding.
    /// </summary>
    private static async Task<string> ConvertBgraToBase64JpegAsync(byte[] pixelData, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            pixelData);

        await encoder.FlushAsync();

        // Convert WinRT stream to Base64
        var streamSize = stream.Size;
        if (streamSize > int.MaxValue)
        {
            throw new InvalidOperationException("Encoded image is too large to convert to Base64.");
        }

        var size = (int)streamSize;
        stream.Seek(0);

        var bytes = new byte[size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)size, InputStreamOptions.None);
        return Convert.ToBase64String(bytes);
    }
}
