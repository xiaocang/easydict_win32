using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Easydict.WinUI.Services;

/// <summary>
/// Uses vision-capable LLMs (GPT-4V, Gemini Vision, etc.) to detect layout regions in PDF page images.
/// </summary>
public sealed class VisionLayoutDetectionService
{
    private const string LayoutDetectionPrompt = """
        Analyze this PDF page image and detect all layout regions.
        For each region, identify its type and bounding box coordinates.

        Return ONLY a JSON array (no other text) with objects having these fields:
        - type: one of "title", "text", "figure", "table", "formula", "caption", "header", "footer", "isolated_formula"
        - x: left coordinate as percentage (0-100) of page width
        - y: top coordinate as percentage (0-100) of page height
        - width: width as percentage (0-100) of page width
        - height: height as percentage (0-100) of page height
        - confidence: detection confidence (0.0-1.0)

        Example: [{"type":"title","x":10,"y":5,"width":80,"height":4,"confidence":0.95}]
        """;

    private static readonly Dictionary<string, LayoutRegionType> TypeMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = LayoutRegionType.Title,
        ["text"] = LayoutRegionType.Body,
        ["plain text"] = LayoutRegionType.Body,
        ["figure"] = LayoutRegionType.Figure,
        ["table"] = LayoutRegionType.Table,
        ["formula"] = LayoutRegionType.Formula,
        ["caption"] = LayoutRegionType.Caption,
        ["header"] = LayoutRegionType.Header,
        ["footer"] = LayoutRegionType.Footer,
        ["isolated_formula"] = LayoutRegionType.IsolatedFormula,
    };

    private readonly HttpClient _httpClient;

    public VisionLayoutDetectionService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Detect layout regions using a vision LLM.
    /// </summary>
    /// <param name="imagePixels">BGRA8 pixel data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="endpoint">API endpoint URL.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="model">Model identifier (e.g., "gpt-4o", "gemini-2.0-flash").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of detected layout regions.</returns>
    public async Task<List<LayoutDetection>> DetectAsync(
        byte[] imagePixels,
        int width,
        int height,
        string endpoint,
        string apiKey,
        string model,
        CancellationToken ct = default)
    {
        var base64Image = ConvertBgraToBase64Bmp(imagePixels, width, height);

        var requestBody = BuildVisionRequest(base64Image, model);
        var jsonContent = JsonSerializer.Serialize(requestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return ParseVisionResponse(responseJson, width, height);
    }

    private static object BuildVisionRequest(string base64Image, string model)
    {
        return new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = LayoutDetectionPrompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/bmp;base64,{base64Image}" }
                        }
                    }
                }
            },
            max_tokens = 4096,
            temperature = 0.1
        };
    }

    internal static List<LayoutDetection> ParseVisionResponse(string responseJson, int imageWidth, int imageHeight)
    {
        var results = new List<LayoutDetection>();

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Extract content from OpenAI-compatible response format
            var content = root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                return results;

            // Extract JSON array from content (may be wrapped in markdown code block)
            var jsonStart = content.IndexOf('[');
            var jsonEnd = content.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return results;

            var jsonArray = content[jsonStart..(jsonEnd + 1)];
            return ParseDetectionArray(jsonArray, imageWidth, imageHeight);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisionLayout] Failed to parse response: {ex.Message}");
            return results;
        }
    }

    internal static List<LayoutDetection> ParseDetectionArray(string jsonArray, int imageWidth, int imageHeight)
    {
        var results = new List<LayoutDetection>();

        try
        {
            using var doc = JsonDocument.Parse(jsonArray);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var typeStr = element.GetProperty("type").GetString() ?? "unknown";
                var xPct = element.GetProperty("x").GetDouble();
                var yPct = element.GetProperty("y").GetDouble();
                var wPct = element.GetProperty("width").GetDouble();
                var hPct = element.GetProperty("height").GetDouble();
                var conf = element.TryGetProperty("confidence", out var confEl)
                    ? (float)confEl.GetDouble()
                    : 0.8f;

                if (!TypeMapping.TryGetValue(typeStr, out var regionType))
                    regionType = LayoutRegionType.Unknown;

                // Convert percentage to pixel coordinates
                var x = xPct / 100.0 * imageWidth;
                var y = yPct / 100.0 * imageHeight;
                var w = wPct / 100.0 * imageWidth;
                var h = hPct / 100.0 * imageHeight;

                results.Add(new LayoutDetection(regionType, conf, x, y, w, h));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisionLayout] Failed to parse detection array: {ex.Message}");
        }

        return results;
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
                if (srcIdx + 2 < bgra.Length && dstIdx + 2 < bmp.Length)
                {
                    bmp[dstIdx] = bgra[srcIdx];         // B
                    bmp[dstIdx + 1] = bgra[srcIdx + 1]; // G
                    bmp[dstIdx + 2] = bgra[srcIdx + 2]; // R
                }
            }
        }

        return Convert.ToBase64String(bmp);
    }
}
