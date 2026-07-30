using System.Buffers;
using System.Diagnostics;
using System.Net;
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
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var expectedLength = pixelWidth * pixelHeight * 4; // BGRA8
        if (pixelData.Length < expectedLength)
            throw new ArgumentException(
                $"pixelData length ({pixelData.Length}) is less than expected ({expectedLength}) for {pixelWidth}x{pixelHeight} BGRA8",
                nameof(pixelData));

        var endpoint = _options.Endpoint;
        var model = _options.Model;

        Debug.WriteLine($"[OllamaOcr] Sending {pixelWidth}x{pixelHeight} image to {endpoint} (model: {model})");

        // Convert BGRA8 pixel data to base64-encoded BMP
        var base64Image = ConvertBgraToBase64Bmp(pixelData, pixelWidth, pixelHeight);

        // Thinking models (qwen3, deepseek-r1, ...) reason before answering, which costs a
        // lot of latency for no recognition benefit. Ollama answers 400 "does not support
        // thinking" for models built without it, so the field is dropped after one refusal.
        var disableThinking = !_options.EnableThinking &&
                              !OcrThinkingSupport.IsRejectedBy(endpoint, model);

        while (true)
        {
            var (statusCode, body) = await PostRequestAsync(
                base64Image, disableThinking, cancellationToken);

            if ((int)statusCode is >= 200 and < 300)
            {
                var text = ParseOllamaResponse(body);

                Debug.WriteLine($"[OllamaOcr] Recognized {text.Length} chars");

                return new OcrResult
                {
                    Text = text,
                    Lines = [],
                    TextAngle = null,
                    DetectedLanguage = null
                };
            }

            if (disableThinking && OcrThinkingSupport.IsThinkingFieldRejection(statusCode, body))
            {
                OcrThinkingSupport.MarkRejectedBy(endpoint, model);
                disableThinking = false;
                Debug.WriteLine($"[OllamaOcr] {model} rejected the think field, retrying without it");
                continue;
            }

            throw new HttpRequestException(
                $"Ollama OCR request failed with status {(int)statusCode} ({statusCode}). {Summarize(body)}",
                null,
                statusCode);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => [];

    private async Task<(HttpStatusCode StatusCode, string Body)> PostRequestAsync(
        string base64Image,
        bool disableThinking,
        CancellationToken cancellationToken)
    {
        // Build Ollama /api/generate request
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["prompt"] = _options.SystemPrompt,
            ["images"] = new[] { base64Image },
            ["stream"] = false
        };

        if (disableThinking)
        {
            requestBody["think"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                $"[OllamaOcr] Request timed out. timeout={FormatTimeout(_httpClient.Timeout)}");
            throw new TimeoutException(
                $"Ollama OCR request timed out after {FormatTimeout(_httpClient.Timeout)}.",
                ex);
        }

        using (response)
        {
            // Read before checking status: a rejected think field is only identifiable from
            // the error body.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.StatusCode, body);
        }
    }

    /// <summary>
    /// Extracts text from the Ollama /api/generate JSON response.
    /// Response format: { "response": "extracted text..." }
    /// </summary>
    private static string ParseOllamaResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("response").GetString();

            return OcrTextSanitizer.StripThinkingMarkup(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OllamaOcr] Failed to parse response: {ex.Message}");
            return string.Empty;
        }
    }

    private static string Summarize(string body)
    {
        const int maxLength = 300;
        var collapsed = body.Trim();

        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        return timeout == Timeout.InfiniteTimeSpan
            ? "infinite"
            : $"{timeout.TotalSeconds:0.#}s";
    }

    /// <summary>
    /// Convert BGRA8 pixel data to a base64-encoded BMP string.
    /// Uses a simple uncompressed BMP → base64 approach for portability.
    /// </summary>
    private static string ConvertBgraToBase64Bmp(ReadOnlyMemory<byte> bgraMemory, int width, int height)
    {
        var bgra = bgraMemory.Span;
        // Create a BMP file in memory (simpler than PNG, widely supported by vision APIs)
        var bmpHeaderSize = 54;
        var rowStride = ((width * 3 + 3) / 4) * 4; // BMP rows are 4-byte aligned
        var imageDataSize = rowStride * height;
        var bmpSize = bmpHeaderSize + imageDataSize;

        var bmp = ArrayPool<byte>.Shared.Rent(bmpSize);
        try
        {
            bmp.AsSpan(0, bmpHeaderSize).Clear();

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
                var paddingStart = dstRow + width * 3;
                var paddingLength = rowStride - width * 3;
                if (paddingLength > 0)
                {
                    bmp.AsSpan(paddingStart, paddingLength).Clear();
                }

                for (var x = 0; x < width; x++)
                {
                    var srcIdx = srcRow + x * 4;
                    var dstIdx = dstRow + x * 3;
                    bmp[dstIdx] = bgra[srcIdx];         // B
                    bmp[dstIdx + 1] = bgra[srcIdx + 1]; // G
                    bmp[dstIdx + 2] = bgra[srcIdx + 2]; // R
                }
            }

            return Convert.ToBase64String(bmp, 0, bmpSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bmp, clearArray: true);
        }
    }
}
