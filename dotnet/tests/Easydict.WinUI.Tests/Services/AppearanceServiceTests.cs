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
    [InlineData(1.0, 14.0)]
    [InlineData(0.85, 11.9)]
    [InlineData(1.4, 19.6)]
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
        var originalPhonetic = _settings.PhoneticFontScale;
        try
        {
            _settings.ResultFontScale = 1.0;
            _settings.PhoneticFontScale = SettingsService.DefaultPhoneticFontScale;
            var snapshot = AppearanceService.CurrentSnapshot();
            snapshot.ResultFontSize.Should().BeApproximately(14.0, 0.001);
            snapshot.ServiceNameFontSize.Should().BeApproximately(12.0, 0.001);
            snapshot.StatusFontSize.Should().BeApproximately(10.0, 0.001);
            snapshot.PhoneticFontSize.Should().BeApproximately(14.0, 0.001);
        }
        finally
        {
            _settings.ResultFontScale = original;
            _settings.PhoneticFontScale = originalPhonetic;
        }
    }

    /// <summary>
    /// Issue #249: the phonetic badges were hardcoded at 10 DIP and were reported as unreadable.
    /// The default must therefore render them larger than the size that shipped.
    /// </summary>
    [Fact]
    public void PhoneticFontSize_DefaultIsLargerThanTheOldHardcodedSize()
    {
        var original = _settings.ResultFontScale;
        var originalPhonetic = _settings.PhoneticFontScale;
        try
        {
            _settings.ResultFontScale = 1.0;
            _settings.PhoneticFontScale = SettingsService.DefaultPhoneticFontScale;

            AppearanceService.PhoneticFontSize.Should().BeGreaterThan(10.0,
                "the pre-#249 badge size is the readability floor, not the target");
            AppearanceService.PhoneticFontSize.Should().BeApproximately(14.0, 0.001);
        }
        finally
        {
            _settings.ResultFontScale = original;
            _settings.PhoneticFontScale = originalPhonetic;
        }
    }

    [Theory]
    [InlineData(1.0, 10.0)]
    [InlineData(1.4, 14.0)]
    [InlineData(2.0, 20.0)]
    public void PhoneticFontSize_ScalesWithPhoneticSetting(double scale, double expected)
    {
        var original = _settings.ResultFontScale;
        var originalPhonetic = _settings.PhoneticFontScale;
        try
        {
            _settings.ResultFontScale = 1.0;
            _settings.PhoneticFontScale = scale;
            AppearanceService.PhoneticFontSize.Should().BeApproximately(expected, 0.001);
        }
        finally
        {
            _settings.ResultFontScale = original;
            _settings.PhoneticFontScale = originalPhonetic;
        }
    }

    [Theory]
    [InlineData(0.5, 1.0)]   // below min clamps up: phonetics never shrink back to unreadable
    [InlineData(9.0, 2.4)]   // above max clamps down
    public void PhoneticFontScale_IsClamped(double raw, double clamped)
    {
        var original = _settings.PhoneticFontScale;
        try
        {
            _settings.PhoneticFontScale = raw;
            AppearanceService.PhoneticFontScale.Should().Be(clamped);
        }
        finally
        {
            _settings.PhoneticFontScale = original;
        }
    }

    /// <summary>
    /// Phonetics sit inside result text, so enlarging all results must carry them along; the
    /// phonetic slider is an additional multiplier on top, not a replacement for that.
    /// </summary>
    [Fact]
    public void PhoneticFontSize_FollowsTheResultFontScale()
    {
        var original = _settings.ResultFontScale;
        var originalPhonetic = _settings.PhoneticFontScale;
        try
        {
            _settings.PhoneticFontScale = 1.4;
            _settings.ResultFontScale = 1.0;
            var atDefault = AppearanceService.PhoneticFontSize;

            _settings.ResultFontScale = 1.4;
            AppearanceService.PhoneticFontSize.Should().BeApproximately(atDefault * 1.4, 0.001);
        }
        finally
        {
            _settings.ResultFontScale = original;
            _settings.PhoneticFontScale = originalPhonetic;
        }
    }

    [Fact]
    public void PhoneticFontSize_IsCappedWhenBothScalesAreMaximal()
    {
        var original = _settings.ResultFontScale;
        var originalPhonetic = _settings.PhoneticFontScale;
        try
        {
            _settings.ResultFontScale = 10.0;
            _settings.PhoneticFontScale = 10.0;
            AppearanceService.PhoneticFontSize.Should().BeLessThanOrEqualTo(32.0);
        }
        finally
        {
            _settings.ResultFontScale = original;
            _settings.PhoneticFontScale = originalPhonetic;
        }
    }

    [Fact]
    public void PhoneticFontScale_SurvivesSave()
    {
        var originalPhonetic = _settings.PhoneticFontScale;

        try
        {
            _settings.PhoneticFontScale = 1.9;
            _settings.Save();

            _settings.PhoneticFontScale.Should().Be(1.9,
                "the phonetic size must persist across restarts, not reset per session");
        }
        finally
        {
            _settings.PhoneticFontScale = originalPhonetic;
            _settings.Save();
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
