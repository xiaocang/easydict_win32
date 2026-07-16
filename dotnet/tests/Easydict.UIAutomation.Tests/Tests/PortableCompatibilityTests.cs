using System.Diagnostics;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class PortableCompatibilityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly AppLauncher _launcher = new();

    public PortableCompatibilityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PortableBuild_SettingsPageOpensAndDegradesGracefullyOnDownlevelWindows()
    {
        var exePath = Environment.GetEnvironmentVariable("EASYDICT_EXE_PATH");
        var expected = Environment.GetEnvironmentVariable("EASYDICT_EXPECTED_OS_BUILD");
        var settingsDir = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        exePath.Should().NotBeNullOrWhiteSpace();
        expected.Should().NotBeNullOrWhiteSpace();
        settingsDir.Should().NotBeNullOrWhiteSpace();
        var build = int.Parse(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "0")?.ToString() ?? "0");
        build.Should().Be(int.Parse(expected!));
        build.Should().BeLessThan(22000);
        Directory.CreateDirectory(settingsDir!);
        File.WriteAllText(Path.Combine(settingsDir!, "settings.json"), JsonSerializer.Serialize(new { UILanguage = "en-US", HasUserConfiguredServices = true }));

        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = _launcher.GetMainWindow();
        var settingsButton = UITestHelper.WaitForSettingsButton(window, TimeSpan.FromSeconds(15));
        settingsButton.Should().NotBeNull();
        UITestHelper.ClickElement(settingsButton!);
        var scroll = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer")), TimeSpan.FromSeconds(20)).Result;
        scroll.Should().NotBeNull("Settings must open without a loader dialog");
        var services = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsTab_Services")), TimeSpan.FromSeconds(15)).Result;
        services.Should().NotBeNull();
        UITestHelper.ClickElement(services!);
        var expander = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("WindowsLocalAIExpander")), TimeSpan.FromSeconds(15)).Result;
        expander.Should().NotBeNull();
        expander!.Patterns.ExpandCollapse.PatternOrDefault?.Expand();
        var status = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("WindowsLocalAIStatusBar")), TimeSpan.FromSeconds(15)).Result;
        status.Should().NotBeNull("Windows AI status must render");
        var statusBar = status!;
        var statusText = string.Join(
            " ",
            statusBar.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(element => element.Name));
        statusText.Should().Contain("Windows AI baseline");
        _output.WriteLine(ScreenshotHelper.CaptureWindow(window, "portable-settings-success"));
    }

    public void Dispose() => _launcher.Dispose();
}
