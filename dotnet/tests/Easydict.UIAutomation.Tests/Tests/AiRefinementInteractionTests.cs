using System.Drawing;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FluentAssertions;
using Xunit;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class AiRefinementInteractionTests
{
    [Fact]
    public void ClickWord_ShowsAndAppliesAlternatives()
    {
        var previousSettings = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        var directory = Path.Combine(Path.GetTempPath(), "Easydict.Refinement.UIA", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "en-US", AppTheme = "Light", QuickStartAutoShowPending = false,
            AiContextLabEnabled = true, AiWordRefinementLabEnabled = true,
            EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false,
            EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false,
            ClipboardMonitoring = false, HoverWordLookupEnabled = false, MouseSelectionTranslate = false
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
        using var launcher = new AppLauncher();
        Window? window = null;
        try
        {
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            window = launcher.GetMainWindow();
            window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            Wait(window, "SettingsButton").Patterns.Invoke.Pattern.Invoke();
            Wait(window, "SettingsTab_Labs").Patterns.Invoke.Pattern.Invoke();
            Wait(window, "AiRefinementDemoOpenButton").Patterns.Invoke.Pattern.Invoke();
            var text = Wait(window, "AiRefinementDemoInteractiveTranslation");
            var ready = Retry.WhileFalse(() =>
            {
                try { return !text.IsOffscreen && text.Patterns.Text.Pattern.DocumentRange.GetText(-1).Contains("安静的空间", StringComparison.Ordinal); }
                catch { return false; }
            }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));
            ready.Success.Should().BeTrue("the offline demo must finish translating before selecting a word");
            // Real mouse input is essential: setting SelectedWordId directly bypasses pointer/capture bugs.
            window.Focus();
            var bounds = text.BoundingRectangle;
            Mouse.Click(new Point(bounds.Left + 8, bounds.Top + Math.Min(10, bounds.Height / 2)));
            var options = Retry.WhileNull(() => FindAlternatives(launcher),
                TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100)).Result;
            options.Should().NotBeNull("clicking an interactive word must open its alternatives flyout");
            var choice = Retry.WhileNull(() => options!.FindFirstDescendant(cf => cf.ByName("宁静的工作环境")),
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)).Result;
            choice.Should().NotBeNull("the clicked word must select the matching alternatives");
            // Only the screenshot waits for the native opening animation; assertions use UIA polling.
            Thread.Sleep(300);
            ScreenshotHelper.CaptureWindowWithPopup(window, "ai-refinement-word-alternatives", options!);
            choice!.Click();
            var applied = Retry.WhileFalse(() => text.Patterns.Text.Pattern.DocumentRange.GetText(-1)
                .Contains("宁静的工作环境", StringComparison.Ordinal), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            applied.Success.Should().BeTrue("choosing an alternative must update the translation");
            ScreenshotHelper.CaptureWindow(window, "ai-refinement-word-applied");
            bounds = text.BoundingRectangle;
            Mouse.Click(new Point(bounds.Left + 8, bounds.Top + Math.Min(10, bounds.Height / 2)));
            Retry.WhileNull(() => FindAlternatives(launcher), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100))
                .Result.Should().NotBeNull("the same word must remain clickable after its translation changes");
        }
        catch
        {
            try { if (window is not null) ScreenshotHelper.CaptureWindow(window, "ai-refinement-click-failure"); }
            catch (Exception screenshotError) { Console.WriteLine($"Failure screenshot unavailable: {screenshotError.GetType().Name}"); }
            throw;
        }
        finally { Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previousSettings); }
    }

    private static AutomationElement Wait(Window window, string id) => Retry.WhileNull(
        () => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100)).Result
        ?? throw new InvalidOperationException($"Element '{id}' was not found.");

    private static AutomationElement? FindAlternatives(AppLauncher launcher) => launcher.Automation.GetDesktop()
        .FindAllDescendants(cf => cf.ByAutomationId("AiRefinementAlternatives").And(cf.ByProcessId(launcher.Application.ProcessId)))
        .FirstOrDefault(element => !element.IsOffscreen);
}
