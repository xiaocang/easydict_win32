using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for LanguageDetectionService.
/// Verifies language detection and intelligent target language selection.
/// </summary>
[Trait("Category", "WinUI")]
public class LanguageDetectionServiceTests : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LanguageDetectionService _service;

    public LanguageDetectionServiceTests()
    {
        _settings = SettingsService.Instance;
        _service = new LanguageDetectionService(_settings);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    [Fact]
    public void Constructor_WithValidSettings_CreatesInstance()
    {
        using var service = new LanguageDetectionService(_settings);

        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithNullSettings_ThrowsArgumentNullException()
    {
        var act = () => new LanguageDetectionService(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("settings");
    }

    [Fact]
    public async Task DetectAsync_WithEmptyText_ReturnsAuto()
    {
        var result = await _service.DetectAsync("");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithNullText_ReturnsAuto()
    {
        var result = await _service.DetectAsync(null!);

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithWhitespaceText_ReturnsAuto()
    {
        var result = await _service.DetectAsync("   ");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithShortText_ReturnsAuto()
    {
        // Non-CJK text shorter than 4 characters should return Auto
        var result = await _service.DetectAsync("Hi");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithShortCjkText_ReturnsAuto()
    {
        // CJK text shorter than 2 characters should return Auto
        var result = await _service.DetectAsync("你");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public void GetTargetLanguage_WhenSourceMatchesFirst_ReturnsSecond()
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        var result = _service.GetTargetLanguage(firstLang);

        result.Should().Be(secondLang);
    }

    [Fact]
    public void GetTargetLanguage_WhenSourceMatchesSecond_ReturnsFirst()
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        var result = _service.GetTargetLanguage(secondLang);

        // When source matches second language, should return first language
        result.Should().Be(firstLang);
    }

    [Fact]
    public void GetTargetLanguage_WithAuto_ReturnsFirstLanguage()
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);

        var result = _service.GetTargetLanguage(Language.Auto);

        // Auto doesn't match first language, so default target is first language
        result.Should().Be(firstLang);
    }

    [Fact]
    public void GetTargetLanguage_PreventsSameSourceAndTarget()
    {
        // Test the fallback logic when source equals target
        // If source is English, target should be SimplifiedChinese (fallback)
        var result = _service.GetTargetLanguage(Language.English);

        // The result should never equal the source
        result.Should().NotBe(Language.English);
    }

    [Fact]
    public void ClearCache_DoesNotThrow()
    {
        var act = () => _service.ClearCache();

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearCache_CanBeCalledMultipleTimes()
    {
        var act = () =>
        {
            _service.ClearCache();
            _service.ClearCache();
            _service.ClearCache();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        using var service = new LanguageDetectionService(_settings);

        var act = () =>
        {
            service.Dispose();
            service.Dispose();
        };

        act.Should().NotThrow();
    }
}
