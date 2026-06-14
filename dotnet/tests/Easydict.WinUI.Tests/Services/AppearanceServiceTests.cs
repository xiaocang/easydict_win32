using Easydict.WinUI.Services;
using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the appearance personalization added in issue #172:
/// the <see cref="AppearanceService"/> font-size math and round-trip
/// persistence of the new <see cref="SettingsService"/> properties.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class AppearanceServiceTests
{
    [Theory]
    [InlineData(1.0, 13.0)]
    [InlineData(0.85, 11.05)]
    [InlineData(1.4, 18.2)]
    public void ResultFontSize_ScalesWithSetting(double scale, double expected)
    {
        var settings = SettingsService.Instance;
        var original = settings.ResultFontScale;
        try
        {
            settings.ResultFontScale = scale;
            AppearanceService.ResultFontSize.Should().BeApproximately(expected, 0.001);
        }
        finally
        {
            settings.ResultFontScale = original;
        }
    }

    [Theory]
    [InlineData(0.5, 0.85)]   // below min clamps up
    [InlineData(2.0, 1.4)]    // above max clamps down
    public void FontScale_IsClamped(double raw, double clamped)
    {
        var settings = SettingsService.Instance;
        var original = settings.ResultFontScale;
        try
        {
            settings.ResultFontScale = raw;
            AppearanceService.FontScale.Should().Be(clamped);
        }
        finally
        {
            settings.ResultFontScale = original;
        }
    }

    [Fact]
    public void CurrentSnapshot_DerivesHeaderAndStatusSizesFromScale()
    {
        var settings = SettingsService.Instance;
        var original = settings.ResultFontScale;
        try
        {
            settings.ResultFontScale = 1.0;
            var snapshot = AppearanceService.CurrentSnapshot();
            snapshot.ResultFontSize.Should().BeApproximately(13.0, 0.001);
            snapshot.ServiceNameFontSize.Should().BeApproximately(12.0, 0.001);
            snapshot.StatusFontSize.Should().BeApproximately(10.0, 0.001);
        }
        finally
        {
            settings.ResultFontScale = original;
        }
    }

    [Fact]
    public void MinFloatingWindowHeight_IsCompact()
    {
        AppearanceService.MinFloatingWindowHeightDips.Should().BeLessThan(200.0);
    }

    [Fact]
    public void AppearanceSettings_RoundTripThroughSettingsFile()
    {
        using var testDirectory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", testDirectory.Path);

            var writer = CreateIsolatedSettingsService();
            writer.ResultFontScale = 1.25;
            writer.ShowOcrButton = false;
            writer.ShowPinButton = false;
            writer.ShowSourcePlayButton = false;
            writer.ShowSwapButton = false;
            writer.FixedWindowIsPinned = false;
            writer.Save();

            var reader = CreateIsolatedSettingsService();
            reader.ResultFontScale.Should().Be(1.25);
            reader.ShowOcrButton.Should().BeFalse();
            reader.ShowPinButton.Should().BeFalse();
            reader.ShowSourcePlayButton.Should().BeFalse();
            reader.ShowSwapButton.Should().BeFalse();
            reader.FixedWindowIsPinned.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previous);
        }
    }

    [Fact]
    public void AppearanceDefaults_PreserveExistingLook()
    {
        using var testDirectory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", testDirectory.Path);

            var settings = CreateIsolatedSettingsService();
            settings.ResultFontScale.Should().Be(1.0);
            settings.ShowOcrButton.Should().BeTrue();
            settings.ShowPinButton.Should().BeTrue();
            settings.ShowSourcePlayButton.Should().BeTrue();
            settings.ShowSwapButton.Should().BeTrue();
            settings.FixedWindowIsPinned.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previous);
        }
    }

    private static SettingsService CreateIsolatedSettingsService()
    {
        var constructor = typeof(SettingsService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        constructor.Should().NotBeNull();
        return (SettingsService)constructor!.Invoke(null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Easydict.WinUI.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
