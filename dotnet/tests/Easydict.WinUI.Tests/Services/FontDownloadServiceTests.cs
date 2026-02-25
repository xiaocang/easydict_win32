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
    public void IsFontDownloaded_ReturnsFalse_ForNonCjkLanguage()
    {
        _service.IsFontDownloaded(Language.English).Should().BeFalse();
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
        // CJK font file not downloaded → falls back to Arial
        PdfExportService.ResolveFontFamily(Language.SimplifiedChinese).Should().Be("Arial");
    }

    [Fact]
    public void GetLineHeight_ReturnsDefault_ForNonCjkLanguage()
    {
        PdfExportService.GetLineHeight(Language.English).Should().Be(14d);
        PdfExportService.GetLineHeight(null).Should().Be(14d);
    }

    [Fact]
    public void GetLineHeight_ReturnsMultiplied_ForCjkLanguage()
    {
        PdfExportService.GetLineHeight(Language.SimplifiedChinese).Should().BeApproximately(19.6, 0.01);
        PdfExportService.GetLineHeight(Language.Japanese).Should().BeApproximately(19.6, 0.01);
        PdfExportService.GetLineHeight(Language.Korean).Should().BeApproximately(18.2, 0.01);
    }

    [Fact]
    public void GetLineHeight_UsesCustomBaseLineHeight()
    {
        PdfExportService.GetLineHeight(Language.SimplifiedChinese, 16d).Should().BeApproximately(22.4, 0.01);
    }
}
