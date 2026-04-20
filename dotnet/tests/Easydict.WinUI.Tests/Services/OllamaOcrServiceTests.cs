using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for OllamaOcrService input validation at the <see cref="OllamaOcrService.RecognizeAsync"/>
/// entry point. Matches the <see cref="WindowsOcrService"/> validation pattern so upstream
/// capture / pixel-stride bugs fail fast with a clear message rather than producing a
/// silently corrupted image.
/// </summary>
[Trait("Category", "WinUI")]
public class OllamaOcrServiceTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOcrService _service;

    public OllamaOcrServiceTests()
    {
        _httpClient = new HttpClient();
        _service = new OllamaOcrService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsArgumentException_WhenBufferShorterThanExpected()
    {
        // 100x100 BGRA8 expects 40000 bytes; supply only 100.
        var tooShort = new byte[100];

        var act = async () => await _service.RecognizeAsync(tooShort, 100, 100);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "pixelData");
    }

    [Fact]
    public async Task RecognizeAsync_ThrowsArgumentNullException_WhenBufferIsNull()
    {
        var act = async () => await _service.RecognizeAsync(null!, 10, 10);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .Where(ex => ex.ParamName == "pixelData");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public async Task RecognizeAsync_ThrowsArgumentOutOfRangeException_ForInvalidDimensions(int width, int height)
    {
        var buffer = new byte[4];

        var act = async () => await _service.RecognizeAsync(buffer, width, height);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
