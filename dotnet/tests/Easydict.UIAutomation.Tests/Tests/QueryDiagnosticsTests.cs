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
public sealed class QueryDiagnosticsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Light", "completed")]
    [InlineData("Minimal", "completed")]
    [InlineData("Light", "timeout")]
    [InlineData("Minimal", "timeout")]
    [InlineData("Light", "running")]
    [InlineData("Minimal", "running")]
    public void ResultMenuOpensTimingsAndTimeoutEntryShowsSameRecord(string theme, string scenario)
    {
        var previousSeed = Environment.GetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS");
        var previousScenario = Environment.GetEnvironmentVariable("EASYDICT_QUERY_DIAGNOSTICS_TEST");
        Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", "1");
        Environment.SetEnvironmentVariable("EASYDICT_QUERY_DIAGNOSTICS_TEST", scenario);
        try
        {
            using var dpi = new PerMonitorDpiScope();
            using var fixture = new Fixture(theme, 0);
            var settingsDirectory = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR")!;
            var settingsPath = Path.Combine(settingsDirectory, "settings.json");
            var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
            settings["UILanguage"] = "en-US";
            settings["MainWindowEnabledServices"] = JsonSerializer.SerializeToNode(new[] { "ollama" });
            File.WriteAllText(settingsPath, settings.ToJsonString());
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            Resize(window, 1280);
            window.SetForeground();
            Wait(window, "InputTextBox").Focus();
            UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
            var reportPath = Path.Combine(settingsDirectory, "live-query-favorites.json");
            Retry.WhileFalse(() => File.Exists(reportPath), TimeSpan.FromSeconds(10)).Result.Should().BeTrue();
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            report.RootElement.GetProperty("Error").ValueKind.Should().Be(JsonValueKind.Null);
            var provider = report.RootElement.GetProperty("Providers")[0].GetString();
            var card = Wait(window, $"ServiceResultItem_{provider}");
            Invoke(Wait(card, "SavedMoreButton"));
            Invoke(Wait(window, "ViewDiagnosticsMenuItem"));
            Wait(window, "QueryDiagnosticsDialog");
            var text = Wait(window, "QueryDiagnosticsText").Name;
            text.Should().Contain("Query preparation").And.Contain("Waiting for response headers").And.Contain("Total elapsed");
            text.Should().NotContain("Successful local fixture result");
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"query_diagnostics_{theme}_{scenario}"));
            Invoke(window.FindFirstDescendant(condition => condition.ByAutomationId("CloseButton")));
            if (scenario == "timeout")
            {
                Retry.WhileFalse(() => window.FindFirstDescendant(condition => condition.ByAutomationId("QueryDiagnosticsDialog")) is null,
                    TimeSpan.FromSeconds(5)).Result.Should().BeTrue("the first dialog must finish closing before reopening it");
                Thread.Sleep(250); // Let the modal close animation and ShowAsync continuation finish.
                Invoke(Wait(card, "TimeoutDiagnosticsButton"));
                var reopenedText = "";
                Retry.WhileFalse(() =>
                {
                    reopenedText = launcher.GetMainWindow().FindAllDescendants()
                        .FirstOrDefault(element => element.Properties.AutomationId.ValueOrDefault == "QueryDiagnosticsText")?.Name ?? "";
                    return reopenedText == text;
                }, TimeSpan.FromSeconds(5)).Result.Should().BeTrue(
                    "timeout and menu entries share the frozen query record; reopened text: {0}", reopenedText);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", previousSeed);
            Environment.SetEnvironmentVariable("EASYDICT_QUERY_DIAGNOSTICS_TEST", previousScenario);
        }
    }
}
