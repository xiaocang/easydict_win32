using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class DictionaryWebViewRenderingTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;
    private readonly string _settingsPath;
    private readonly string? _settingsSnapshot;

    private const int TranslationWaitMs = 10000;

    private static string DictionaryQuery =>
        Environment.GetEnvironmentVariable("EASYDICT_UIA_DICTIONARY_QUERY") ?? "no";

    public DictionaryWebViewRenderingTests(ITestOutputHelper output)
    {
        _output = output;
        _settingsPath = UiaSettingsIsolation.TryGetSettingsFilePath()
            ?? throw new InvalidOperationException(
                "Dictionary UI automation requires an isolated settings directory.");
        _settingsSnapshot = File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath) : null;
        ConfigureGoogleDictionary(_settingsPath);

        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MainWindow_GoogleDictionary_CapturesResultOrFallback()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var inputBox = UITestHelper.FindInputTextBox(window);
        inputBox.Should().NotBeNull("InputTextBox must exist on main window");
        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = DictionaryQuery;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "50_dictionary_result_before_query");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");
        File.Exists(pathBeforeTranslate).Should().BeTrue("the pre-query screenshot should be written");

        Keyboard.Type(VirtualKeyShort.ENTER);
        _output.WriteLine($"Waiting {TranslationWaitMs}ms for Google Dictionary results...");
        Thread.Sleep(TranslationWaitMs);

        _launcher.Application.HasExited.Should().BeFalse(
            "triggering a dictionary query should not terminate the app");

        var serviceName = Retry.WhileNull(
            () => TryFindVisibleDescendant(window, "ServiceNameText"),
            TimeSpan.FromSeconds(5)).Result;
        serviceName.Should().NotBeNull("the configured Google Dictionary service card should be visible");
        serviceName!.Name.Should().Contain(
            "Google Dict",
            "this test must validate the dictionary service rather than a regular translation card");

        var dictionaryPanel = Retry.WhileNull(
            () => TryFindVisibleDescendant(window, "DictionaryPanel"),
            TimeSpan.FromSeconds(5)).Result;
        var fallbackText = dictionaryPanel == null
            ? TryFindVisibleDescendant(window, "ResultText")
            : null;
        if (dictionaryPanel == null && fallbackText == null)
        {
            var errorText = TryFindVisibleDescendant(window, "ErrorText");
            var diagnosticPath = ScreenshotHelper.CaptureWindow(
                window,
                "51_dictionary_query_error_diagnostic");
            _output.WriteLine($"Dictionary query diagnostic screenshot saved: {diagnosticPath}");
            _output.WriteLine($"Visible service error: {errorText?.Name ?? "<none>"}");
        }

        (dictionaryPanel != null || fallbackText != null).Should().BeTrue(
            "Google Dictionary should expose rich definitions or a visible plain-text dictionary fallback");
        if (fallbackText != null)
        {
            fallbackText.Name.Should().NotBeNullOrWhiteSpace(
                "the dictionary fallback must contain visible result text");
        }

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(
            window,
            "51_dictionary_result_after_query");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");
        File.Exists(pathAfterTranslate).Should().BeTrue("the post-query screenshot should be written");

        var resultElement = dictionaryPanel ?? fallbackText!;
        var pathElement = ScreenshotHelper.CaptureElement(resultElement, "52_dictionary_result_element");
        _output.WriteLine($"Dictionary result element screenshot saved: {pathElement}");
        File.Exists(pathElement).Should().BeTrue("the visible dictionary result state should be captured");

        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate,
            "dictionary_result_main_window",
            VisualRegressionHelper.ThresholdText);
        if (comparison == null)
        {
            _output.WriteLine("No baseline found - screenshot saved as baseline candidate for manual review.");
        }
        else
        {
            _output.WriteLine(comparison.ToString());
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
        if (_settingsSnapshot == null)
        {
            File.Delete(_settingsPath);
        }
        else
        {
            File.WriteAllText(_settingsPath, _settingsSnapshot);
        }
    }

    private static void ConfigureGoogleDictionary(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root["EnableInternationalServices"] = true;
        root["HasUserConfiguredServices"] = true;
        root["MainWindowEnabledServices"] = JsonNode.Parse("[\"google_web\"]");
        root["MainWindowServiceEnabledQuery"] = JsonNode.Parse("{\"google_web\":true}");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AutomationElement? TryFindVisibleDescendant(AutomationElement root, string automationId)
    {
        try
        {
            var candidate = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return candidate != null && !candidate.IsOffscreen ? candidate : null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
