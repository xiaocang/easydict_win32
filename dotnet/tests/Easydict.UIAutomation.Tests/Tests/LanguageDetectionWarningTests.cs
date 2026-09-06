using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using static Easydict.UIAutomation.Tests.Tests.SavedItemsVisualTests;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class LanguageDetectionWarningTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Main", "Light", "rate-limited-recovered")]
    [InlineData("Main", "Minimal", "rate-limited-failed")]
    [InlineData("Mini", "Light", "rate-limited-recovered")]
    [InlineData("Fixed", "Light", "rate-limited-failed")]
    public void AutoSourceRateLimit_ShowsWarningAndManualSourceClearsIt(string surface, string theme, string scenario)
    {
        var previousSettings = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        var previousScenario = Environment.GetEnvironmentVariable("EASYDICT_TEST_DETECTION");
        var directory = Path.Combine(Path.GetTempPath(), "Easydict.Detection.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "en-US", AppTheme = theme, HistoryEnabled = false,
            SelectedLanguages = new[] { "zh", "en" }, SourceLanguage = "auto",
            MainWindowEnabledServices = new[] { "bing" },
            MiniWindowEnabledServices = new[] { "bing" },
            FixedWindowEnabledServices = new[] { "bing" },
            MainWindowServiceEnabledQuery = new Dictionary<string, bool> { ["bing"] = false },
            MiniWindowServiceEnabledQuery = new Dictionary<string, bool> { ["bing"] = false },
            FixedWindowServiceEnabledQuery = new Dictionary<string, bool> { ["bing"] = false },
            EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = surface == "Mini", ShowMiniWindowHotkey = "Ctrl+Alt+F10",
            EnableShowFixedWindowHotkey = surface == "Fixed", ShowFixedWindowHotkey = "Ctrl+Alt+F11",
            EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
        Environment.SetEnvironmentVariable("EASYDICT_TEST_DETECTION", scenario);
        try
        {
            using var dpi = new PerMonitorDpiScope();
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            if (surface != "Main")
            {
                UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT,
                    surface == "Mini" ? VirtualKeyShort.F10 : VirtualKeyShort.F11);
                window = Retry.WhileNull(() => UITestHelper.FindSecondaryWindow(
                    launcher.Application, launcher.Automation, surface, output), TimeSpan.FromSeconds(10)).Result!;
                window.Should().NotBeNull();
            }

            Wait(window, "InputTextBox").AsTextBox().Text = "She go to school yesterday.";
            Invoke(Wait(window, "TranslateButton"));
            var expected = scenario == "rate-limited-recovered"
                ? "The backup detector identified" : "Select it manually";
            Retry.WhileFalse(() => WarningText(window).Contains(expected), TimeSpan.FromSeconds(10))
                .Result.Should().BeTrue("the warning must distinguish recovered and failed detection");
            var warning = Wait(window, "LanguageDetectionWarningBar");
            warning.IsOffscreen.Should().BeFalse("429 remains visible even in Minimal mode");
            WarningText(window).Should().Contain("429");
            Thread.Sleep(250); // Let composition catch up with the UIA text update before capture.
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"detection_429_{surface}_{theme}_{scenario}"));

            Wait(window, "SourceLangCombo").AsComboBox().Select(2); // Auto, Chinese, English
            Retry.WhileFalse(() => Find(window, "LanguageDetectionWarningBar") is not { IsOffscreen: false },
                TimeSpan.FromSeconds(5)).Result.Should().BeTrue("an explicit source clears the stale auto-detection warning");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previousSettings);
            Environment.SetEnvironmentVariable("EASYDICT_TEST_DETECTION", previousScenario);
        }
    }

    private static string WarningText(Window window)
    {
        var bar = Find(window, "LanguageDetectionWarningBar");
        return bar is null ? "" : string.Join(" ", bar.FindAllDescendants().Select(element => element.Name));
    }
}
