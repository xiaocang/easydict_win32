using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the appearance personalization added in issue #172:
/// the <see cref="AppearanceService"/> font-size math and persistence of the
/// new <see cref="SettingsService"/> properties.
///
/// These tests use the <see cref="SettingsService"/> singleton and restore any
/// mutated state in a finally block — mirroring the existing
/// <c>TtsSettings_RoundTripThroughSave</c> pattern. They intentionally avoid the
/// EASYDICT_SETTINGS_DIR + temporary-directory dance, which can poison the
/// lazily-constructed singleton's settings path for other tests in the shared
/// collection.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class AppearanceServiceTests
{
    private readonly SettingsService _settings = SettingsService.Instance;

    [Theory]
    [InlineData(1.0, 13.0)]
    [InlineData(0.85, 11.05)]
    [InlineData(1.4, 18.2)]
    public void ResultFontSize_ScalesWithSetting(double scale, double expected)
    {
        var original = _settings.ResultFontScale;
        try
        {
            _settings.ResultFontScale = scale;
            AppearanceService.ResultFontSize.Should().BeApproximately(expected, 0.001);
        }
        finally
        {
            _settings.ResultFontScale = original;
        }
    }

    [Theory]
    [InlineData(0.5, 0.85)]   // below min clamps up
    [InlineData(2.0, 1.4)]    // above max clamps down
    public void FontScale_IsClamped(double raw, double clamped)
    {
        var original = _settings.ResultFontScale;
        try
        {
            _settings.ResultFontScale = raw;
            AppearanceService.FontScale.Should().Be(clamped);
        }
        finally
        {
            _settings.ResultFontScale = original;
        }
    }

    [Fact]
    public void CurrentSnapshot_DerivesHeaderAndStatusSizesFromScale()
    {
        var original = _settings.ResultFontScale;
        try
        {
            _settings.ResultFontScale = 1.0;
            var snapshot = AppearanceService.CurrentSnapshot();
            snapshot.ResultFontSize.Should().BeApproximately(13.0, 0.001);
            snapshot.ServiceNameFontSize.Should().BeApproximately(12.0, 0.001);
            snapshot.StatusFontSize.Should().BeApproximately(10.0, 0.001);
        }
        finally
        {
            _settings.ResultFontScale = original;
        }
    }

    [Fact]
    public void MinFloatingWindowHeight_IsCompact()
    {
        AppearanceService.MinFloatingWindowHeightDips.Should().BeLessThan(200.0);
    }

    [Fact]
    public void AppearanceSettings_SurviveSave()
    {
        var originalFontScale = _settings.ResultFontScale;
        var originalCompactMode = _settings.CompactMode;
        var originalOcr = _settings.ShowOcrButton;
        var originalPin = _settings.ShowPinButton;
        var originalPlay = _settings.ShowSourcePlayButton;
        var originalSwap = _settings.ShowSwapButton;
        var originalFixedPinned = _settings.FixedWindowIsPinned;

        try
        {
            _settings.ResultFontScale = 1.25;
            _settings.CompactMode = true;
            _settings.ShowOcrButton = false;
            _settings.ShowPinButton = false;
            _settings.ShowSourcePlayButton = false;
            _settings.ShowSwapButton = false;
            _settings.FixedWindowIsPinned = false;
            _settings.Save();

            _settings.ResultFontScale.Should().Be(1.25);
            _settings.CompactMode.Should().BeTrue();
            _settings.ShowOcrButton.Should().BeFalse();
            _settings.ShowPinButton.Should().BeFalse();
            _settings.ShowSourcePlayButton.Should().BeFalse();
            _settings.ShowSwapButton.Should().BeFalse();
            _settings.FixedWindowIsPinned.Should().BeFalse();
        }
        finally
        {
            _settings.ResultFontScale = originalFontScale;
            _settings.CompactMode = originalCompactMode;
            _settings.ShowOcrButton = originalOcr;
            _settings.ShowPinButton = originalPin;
            _settings.ShowSourcePlayButton = originalPlay;
            _settings.ShowSwapButton = originalSwap;
            _settings.FixedWindowIsPinned = originalFixedPinned;
            _settings.Save();
        }
    }
}
