using System.Runtime.InteropServices;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;
using Xunit;
using static Easydict.UIAutomation.Tests.Tests.SavedItemsVisualTests;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class HoverLookupSettingsTests
{
    [Fact]
    public void TrayToggle_SynchronizesOpenSettings_AndSurvivesUnrelatedSave()
    {
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 0);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR")!, "settings.json");
        Invoke(Wait(window, "SettingsButton"));
        Invoke(Wait(window, "SettingsTab_General"));
        var hover = Wait(window, "HoverWordLookupToggle");
        var unrelated = Wait(window, "MinimizeToTrayToggle");

        foreach (var enabled in new[] { true, false })
        {
            // Keep an unrelated edit pending while the real tray handler saves.
            unrelated.Patterns.Toggle.Pattern.Toggle();
            var unrelatedValue = unrelated.Patterns.Toggle.Pattern.ToggleState == ToggleState.On;
            SendMessageTimeout(window.Properties.NativeWindowHandle.Value, 0x8000 + 205,
                enabled ? (nint)1 : 0, 0, 2, 5000, out var handled).Should().NotBe(0);
            handled.Should().Be((nuint)1);
            Retry.WhileFalse(() => (hover.Patterns.Toggle.Pattern.ToggleState == ToggleState.On) == enabled,
                TimeSpan.FromSeconds(5)).Result.Should().BeTrue("the tray change must update the open page");
            unrelated.Patterns.Toggle.Pattern.ToggleState.Value.Should().Be(unrelatedValue ? ToggleState.On : ToggleState.Off);
            Invoke(Wait(window, "SaveButton"));
            Retry.WhileFalse(() => SavedValuesMatch(enabled, unrelatedValue), TimeSpan.FromSeconds(5))
                .Result.Should().BeTrue("saving other settings must preserve the tray choice and pending edits");
        }

        bool SavedValuesMatch(bool enabled, bool minimize)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.GetProperty("HoverWordLookupEnabled").GetBoolean() == enabled
                && document.RootElement.GetProperty("MinimizeToTray").GetBoolean() == minimize;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
