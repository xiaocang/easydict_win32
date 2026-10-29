using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using Xunit;
using static Easydict.UIAutomation.Tests.Tests.SavedItemsVisualTests;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class AnkiSettingsTests
{
    [Fact]
    public void Labs_AnkiOptionsAreOptInAndPersist()
    {
        var previous = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        var directory = Path.Combine(Path.GetTempPath(), "Easydict.AnkiSettings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "zh-CN", AppTheme = "Light", EnableShowWindowHotkey = false,
            EnableTranslateSelectionHotkey = false, EnableShowMiniWindowHotkey = false,
            EnableShowFixedWindowHotkey = false, EnableOcrTranslateHotkey = false,
            EnableSilentOcrHotkey = false, MouseSelectionTranslate = false
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
        try
        {
            using var dpi = new PerMonitorDpiScope();
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            Invoke(Wait(window, "SettingsButton"));
            Invoke(Wait(window, "SettingsTab_Labs"));
            var toggle = Wait(window, "AnkiEnabledToggle").Patterns.Toggle.Pattern;
            toggle.ToggleState.Value.Should().Be(ToggleState.Off);
            toggle.Toggle();
            var quick = Wait(window, "AnkiQuickAddToggle").Patterns.Toggle.Pattern;
            quick.ToggleState.Value.Should().Be(ToggleState.Off);
            Wait(window, "AnkiConfigureButton");
            window.SetForeground();
            ScreenshotHelper.CaptureWindow(window, "anki_labs_settings");
            if (Environment.GetEnvironmentVariable("EASYDICT_TEST_ANKI") == "1")
            {
                Invoke(Wait(window, "AnkiConfigureButton"));
                AutomationElement ByName(string name) => Retry.WhileNull(
                    () => window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button))),
                    TimeSpan.FromSeconds(15)).Result ?? throw new InvalidOperationException($"Button not found: {name}");
                Invoke(ByName("连接并加载选项"));
                Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName("Front")), TimeSpan.FromSeconds(30))
                    .Result.Should().NotBeNull("AnkiConnect should load real note fields");
                ScreenshotHelper.CaptureWindow(window, "anki_configuration_connected");
                Invoke(ByName("保存"));
            }
            quick.Toggle();
            Invoke(Wait(window, "SaveButton"));
            Retry.WhileFalse(() =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
                return json.RootElement.TryGetProperty("AnkiEnabled", out var enabled) && enabled.GetBoolean()
                    && json.RootElement.TryGetProperty("AnkiQuickAdd", out var fast) && fast.GetBoolean();
            }, TimeSpan.FromSeconds(10)).Result.Should().BeTrue();
        }
        finally { Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previous); }
    }
}
