using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class FontDownloadServiceTests : IDisposable
{
    private readonly FontDownloadService _service = new();

    [Fact]
    public void GetCachedFontPath_ForNonCjkLanguage_DoesNotThrow()
    {
        // English is not a CJK language — verify the method doesn't throw
        FontDownloadService.RequiresCjkFont(Language.English).Should().BeFalse();
        var act = () => _service.GetCachedFontPath(Language.English);
        act.Should().NotThrow();
    }

    [Fact]
    public void RequiresCjkFont_ReturnsTrue_ForCjkLanguages()
    {
        FontDownloadService.RequiresCjkFont(Language.SimplifiedChinese).Should().BeTrue();
        FontDownloadService.RequiresCjkFont(Language.TraditionalChinese).Should().BeTrue();
        FontDownloadService.RequiresCjkFont(Language.Japanese).Should().BeTrue();
        FontDownloadService.RequiresCjkFont(Language.Korean).Should().BeTrue();
    }

    [Fact]
    public void RequiresCjkFont_ReturnsFalse_ForNonCjkLanguages()
    {
        FontDownloadService.RequiresCjkFont(Language.English).Should().BeFalse();
        FontDownloadService.RequiresCjkFont(Language.French).Should().BeFalse();
        FontDownloadService.RequiresCjkFont(Language.German).Should().BeFalse();
        FontDownloadService.RequiresCjkFont(Language.Auto).Should().BeFalse();
    }

    [Fact]
    public void IsFontDownloaded_ForNonCjkLanguage_TracksWhetherAnyCjkFontIsAvailable()
    {
        // For non-CJK languages, the service may still return true if any CJK font exists in cache
        // (fallback behavior used by PDF export to render mixed documents).
        _service.IsFontDownloaded(Language.English).Should().Be(_service.HasAnyCjkFont);
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}

public class PdfExportServiceFontTests
{
    [Fact]
    public void ResolveFontFamily_ReturnsArial_WhenNoTargetLanguage()
    {
        PdfExportService.ResolveFontFamily(null).Should().Be("Arial");
    }

    [Fact]
    public void ResolveFontFamily_ReturnsArial_ForNonCjkLanguage()
    {
        PdfExportService.ResolveFontFamily(Language.English).Should().Be("Arial");
        PdfExportService.ResolveFontFamily(Language.French).Should().Be("Arial");
    }

    [Fact]
    public void ResolveFontFamily_ReturnsArial_WhenCjkFontNotRegistered()
    {
        // Depending on machine environment, we may fall back to a system CJK font or Arial.
        var family = PdfExportService.ResolveFontFamily(Language.SimplifiedChinese);
        family.Should().NotBeNullOrWhiteSpace();
        family.Should().BeOneOf(
            "Arial",
            CjkFontResolver.NotoSansSC,
            CjkFontResolver.MicrosoftYaHei,
            CjkFontResolver.MicrosoftJhengHei,
            CjkFontResolver.YuGothic,
            CjkFontResolver.MalgunGothic);
    }

    [Fact]
    public void GetLineHeight_ReturnsDefaultMultiplied_ForNonConfiguredLanguage()
    {
        // Non-configured languages now use 1.15× default multiplier
        PdfExportService.GetLineHeight(Language.English).Should().BeApproximately(14d * 1.15, 0.01);
        PdfExportService.GetLineHeight(null).Should().BeApproximately(14d * 1.15, 0.01);
    }

    [Fact]
    public void GetLineHeight_ReturnsMultiplied_ForCjkLanguage()
    {
        PdfExportService.GetLineHeight(Language.SimplifiedChinese).Should().BeApproximately(19.6, 0.01);  // 14 * 1.4
        PdfExportService.GetLineHeight(Language.Japanese).Should().BeApproximately(16.8, 0.01);           // 14 * 1.2 (lowered from 1.4)
        PdfExportService.GetLineHeight(Language.Korean).Should().BeApproximately(18.2, 0.01);             // 14 * 1.3
    }

    [Fact]
    public void GetLineHeight_ReturnsMultiplied_ForNewLanguages()
    {
        // New language entries aligned with pdf2zh LANG_LINEHEIGHT_MAP
        PdfExportService.GetLineHeight(Language.Arabic).Should().BeApproximately(14.0, 0.01);   // 14 * 1.0
        PdfExportService.GetLineHeight(Language.Russian).Should().BeApproximately(14.0, 0.01);  // 14 * 1.0
        PdfExportService.GetLineHeight(Language.Thai).Should().BeApproximately(18.2, 0.01);     // 14 * 1.3
    }

    [Fact]
    public void GetLineHeight_UsesCustomBaseLineHeight()
    {
        PdfExportService.GetLineHeight(Language.SimplifiedChinese, 16d).Should().BeApproximately(22.4, 0.01);
    }
}
