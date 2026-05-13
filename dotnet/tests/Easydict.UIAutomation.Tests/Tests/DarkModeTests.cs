using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// Tests that switch to dark mode via Settings and capture screenshots
/// of the main window, mini window, and fixed window in dark theme.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class DarkModeTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public DarkModeTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void DarkMode_MainWindow_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Capture light mode baseline first
        var pathLight = ScreenshotHelper.CaptureWindow(window, "30_main_light_mode");
        _output.WriteLine($"Light mode screenshot saved: {pathLight}");

        // Switch to dark mode via settings
        SwitchToDarkMode(window);

        // Navigate back to main window
        NavigateBackToMain(window);

        Thread.Sleep(1000);
        var pathDark = ScreenshotHelper.CaptureWindow(window, "31_main_dark_mode");
        _output.WriteLine($"Dark mode screenshot saved: {pathDark}");

        var result = VisualRegressionHelper.CompareWithBaseline(pathDark, "31_main_dark_mode");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    [Fact]
    public void MainWindow_ExplicitLightAndDarkThemes_ShouldNotLeakOppositePalette()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        SwitchToLightMode(window);
        NavigateBackToMain(window);
        WaitForMainPage(window);
        CaptureAndAssertMainWindowPalette(window, ThemePalette.Light, "37_main_explicit_light_theme");

        SwitchToDarkMode(window);
        NavigateBackToMain(window);
        WaitForMainPage(window);
        CaptureAndAssertMainWindowPalette(window, ThemePalette.Dark, "39_main_explicit_dark_theme");
    }

    [Fact]
    public void DarkMode_SettingsPage_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Switch to dark mode
        SwitchToDarkMode(window);

        // Stay on settings page and capture
        Thread.Sleep(1000);
        var path = ScreenshotHelper.CaptureWindow(window, "32_settings_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll down to show more settings in dark mode
        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (scrollViewer != null)
        {
            ScrollHelper.ScrollToPercent(scrollViewer, 12, _output.WriteLine);
            var pathServices = ScreenshotHelper.CaptureWindow(window, "33_settings_dark_services");
            _output.WriteLine($"Screenshot saved: {pathServices}");

            ScrollHelper.ScrollToPercent(scrollViewer, 35, _output.WriteLine);
            var pathConfig = ScreenshotHelper.CaptureWindow(window, "34_settings_dark_config");
            _output.WriteLine($"Screenshot saved: {pathConfig}");
        }
    }

    [Fact]
    public void DarkMode_MiniWindow_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Switch to dark mode first
        SwitchToDarkMode(window);

        NavigateBackToMain(window);
        Thread.Sleep(1000);

        // Open mini window via hotkey: Ctrl+Alt+M
        _output.WriteLine("Opening mini window with Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
        Thread.Sleep(3000);

        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Mini", _output);
        miniWindow.Should().NotBeNull("Mini window must open after Ctrl+Alt+M hotkey in dark mode");

        miniWindow!.SetForeground();
        Thread.Sleep(500);

        var path = ScreenshotHelper.CaptureWindow(miniWindow, "35_mini_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "35_mini_dark_mode");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    [Fact]
    public void DarkMode_FixedWindow_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Switch to dark mode first
        SwitchToDarkMode(window);

        NavigateBackToMain(window);
        Thread.Sleep(1000);

        // Open fixed window via hotkey: Ctrl+Alt+F
        _output.WriteLine("Opening fixed window with Ctrl+Alt+F");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        Thread.Sleep(3000);

        var fixedWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Fixed", _output);
        fixedWindow.Should().NotBeNull("Fixed window must open after Ctrl+Alt+F hotkey in dark mode");

        fixedWindow!.SetForeground();
        Thread.Sleep(500);

        var path = ScreenshotHelper.CaptureWindow(fixedWindow, "36_fixed_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "36_fixed_dark_mode");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    /// <summary>
    /// Navigate to settings and switch AppThemeCombo to "Dark".
    /// </summary>
    private void SwitchToDarkMode(Window window)
        => SwitchToTheme(window, "Dark", 2);

    private void SwitchToLightMode(Window window)
        => SwitchToTheme(window, "Light", 1);

    private void SwitchToTheme(Window window, string themeName, int themeIndex)
    {
        var themeCombo = FindAppThemeCombo(window);

        _output.WriteLine($"Selecting {themeName} theme by clicking AppThemeCombo item index {themeIndex}");
        themeCombo.Expand();
        Thread.Sleep(500);
        var items = themeCombo.Items;
        if (items.Length > themeIndex)
        {
            items[themeIndex].Click();
        }
        else
        {
            _output.WriteLine($"AppThemeCombo exposed {items.Length} item(s), falling back to SelectionPattern index {themeIndex}");
            themeCombo.Select(themeIndex);
        }

        Thread.Sleep(1500);
        _output.WriteLine($"Persisted AppTheme after selecting {themeName}: {ReadPersistedAppTheme()}");
    }

    private static string ReadPersistedAppTheme()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict",
                "settings.json");
            if (!File.Exists(settingsPath))
            {
                return "<missing settings.json>";
            }

            var json = File.ReadAllText(settingsPath);
            var marker = "\"AppTheme\"";
            var markerIndex = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return "<missing AppTheme>";
            }

            var colonIndex = json.IndexOf(':', markerIndex);
            var firstQuote = json.IndexOf('"', colonIndex + 1);
            var secondQuote = firstQuote >= 0 ? json.IndexOf('"', firstQuote + 1) : -1;
            return firstQuote >= 0 && secondQuote > firstQuote
                ? json[(firstQuote + 1)..secondQuote]
                : "<unreadable AppTheme>";
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    private ComboBox FindAppThemeCombo(Window window)
    {
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(2)).Result;

        if (settingsButton is not null)
        {
            settingsButton.Click();
            Thread.Sleep(2000);
        }
        else
        {
            var existingThemeCombo = window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo"))?.AsComboBox();
            if (existingThemeCombo is not null && !existingThemeCombo.IsOffscreen)
            {
                return existingThemeCombo;
            }
        }

        var scrollViewer = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer")),
            TimeSpan.FromSeconds(15)).Result;

        scrollViewer.Should().NotBeNull(
            "MainScrollViewer must appear on settings page once initialization finishes");

        var element = ScrollHelper.ScrollToFind(
            scrollViewer!, startPercent: 70,
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo")),
            _output.WriteLine);
        var themeCombo = element?.AsComboBox();

        themeCombo.Should().NotBeNull("AppThemeCombo must exist on settings page");

        _output.WriteLine("Found AppThemeCombo");
        return themeCombo!;
    }

    /// <summary>
    /// Navigate back to main page from settings using the back button.
    /// </summary>
    private void NavigateBackToMain(Window window)
    {
        var settingsScrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (settingsScrollViewer != null)
        {
            ScrollHelper.ScrollToPercent(settingsScrollViewer, 0, _output.WriteLine);
            Thread.Sleep(500);
        }

        // Try floating back button first, then fall back to BackButton
        var backButton = window.FindFirstDescendant(cf => cf.ByAutomationId("FloatingBackButton"));
        if (backButton == null)
            backButton = window.FindFirstDescendant(cf => cf.ByAutomationId("BackButton"));

        if (backButton != null)
        {
            backButton.Click();
            Thread.Sleep(1000);

            // Handle unsaved changes dialog if it appears
            DismissUnsavedChangesDialog(window);
            return;
        }

        _output.WriteLine("Back button not found - trying Alt+Left");
        try
        {
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Press(VirtualKeyShort.LEFT);
            Thread.Sleep(100);
        }
        finally
        {
            try { Keyboard.Release(VirtualKeyShort.LEFT); } catch { /* ignore */ }
            try { Keyboard.Release(VirtualKeyShort.ALT); } catch { /* ignore */ }
        }
        Thread.Sleep(1000);
    }

    private void WaitForMainPage(Window window)
    {
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;

        settingsButton.Should().NotBeNull("SettingsButton must be visible on the main page before screenshot validation");
        Thread.Sleep(700);
    }

    private void PrepareMainWindowForScreenshot(Window window, string label)
    {
        _output.WriteLine($"{label} window bounds before move: {window.BoundingRectangle}");
        ResizeMainWindowForScreenshot(window);
        window.Move(0, 0);
        Thread.Sleep(500);
        window.SetForeground();
        Thread.Sleep(500);
        _output.WriteLine($"{label} window bounds after move: {window.BoundingRectangle}");
    }

    private void ResizeMainWindowForScreenshot(Window window)
    {
        try
        {
            if (!window.Patterns.Transform.IsSupported)
            {
                return;
            }

            var transform = window.Patterns.Transform.Pattern;
            if (!transform.CanResize.Value)
            {
                return;
            }

            transform.Resize(900, 1000);
            Thread.Sleep(500);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Window resize skipped: {ex.Message}");
        }
    }

    private void CaptureAndAssertMainWindowPalette(Window window, ThemePalette palette, string baseName)
    {
        ScrollMainContentToPercent(window, 0);
        PrepareMainWindowForScreenshot(window, $"{palette} top");
        var topPath = ScreenshotHelper.CaptureWindowPhysical(window, $"{baseName}_top_palette");
        _output.WriteLine($"Explicit {palette} top palette screenshot saved: {topPath}");
        AssertMainWindowPalette(topPath, palette, TopPaletteProbes);

        ScrollMainContentToPercent(window, 0);
    }

    private void ScrollMainContentToPercent(Window window, double verticalPercent)
    {
        var scrollViewer = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("QuickTranslateContent")),
            TimeSpan.FromSeconds(5)).Result;

        if (scrollViewer != null)
        {
            try
            {
                ScrollHelper.ScrollToPercent(scrollViewer, verticalPercent, _output.WriteLine);
                return;
            }
            catch (InvalidOperationException ex)
            {
                _output.WriteLine($"QuickTranslateContent ScrollPattern failed, falling back to mouse wheel: {ex.Message}");
            }
        }

        if (scrollViewer != null)
        {
            _output.WriteLine("QuickTranslateContent ScrollPattern unavailable, falling back to mouse wheel");
            var rect = scrollViewer.BoundingRectangle;
            Mouse.MoveTo(new Point(rect.Right - 12, rect.Top + (rect.Height / 2)));
        }
        else
        {
            _output.WriteLine("QuickTranslateContent not found, falling back to mouse wheel");
            Mouse.MoveTo(window.GetClickablePoint());
        }

        Mouse.Scroll(verticalPercent > 0 ? -10 : 10);
        Thread.Sleep(800);
    }

    /// <summary>
    /// Dismiss the unsaved changes confirmation dialog by clicking "Don't Save".
    /// </summary>
    private void DismissUnsavedChangesDialog(Window window)
    {
        var dontSaveButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("Don't Save")) ??
                  window.FindFirstDescendant(cf => cf.ByName("不保存")),
            TimeSpan.FromSeconds(3)).Result;

        if (dontSaveButton != null)
        {
            _output.WriteLine("Unsaved changes dialog detected - clicking Don't Save");
            dontSaveButton.Click();
            Thread.Sleep(1000);
        }
    }

    private static readonly PaletteProbe[] TopPaletteProbes =
    {
        new("main background", 0.38, 0.12, 0.40, 0.06, DarkMax: 95, LightMin: 175),
        new("source card background", 0.38, 0.25, 0.40, 0.06, DarkMax: 120, LightMin: 175),
        new("source input fill", 0.24, 0.52, 0.52, 0.08, DarkMax: 120, LightMin: 180)
    };

    private void AssertMainWindowPalette(
        string screenshotPath,
        ThemePalette palette,
        IReadOnlyList<PaletteProbe> probes)
    {
        using var bitmap = new Bitmap(screenshotPath);
        foreach (var probe in probes)
        {
            var sample = AverageRegion(bitmap, probe);
            _output.WriteLine(
                $"{palette} {probe.Name}: avg rgb=({sample.R:0.0}, {sample.G:0.0}, {sample.B:0.0}), brightness={sample.Brightness:0.0}");

            if (palette == ThemePalette.Dark)
            {
                sample.Brightness.Should().BeLessThan(
                    probe.DarkMax,
                    $"{probe.Name} in {Path.GetFileName(screenshotPath)} must stay dark and not use light-mode fill colors");
            }
            else
            {
                sample.Brightness.Should().BeGreaterThan(
                    probe.LightMin,
                    $"{probe.Name} in {Path.GetFileName(screenshotPath)} must stay light and not use dark-mode fill colors");
            }
        }
    }

    private static PaletteSample AverageRegion(Bitmap bitmap, PaletteProbe probe)
    {
        var rect = ToPixelRect(bitmap, probe);
        double r = 0;
        double g = 0;
        double b = 0;
        var count = 0;

        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                r += color.R;
                g += color.G;
                b += color.B;
                count++;
            }
        }

        r /= count;
        g /= count;
        b /= count;
        var brightness = (0.299 * r) + (0.587 * g) + (0.114 * b);
        return new PaletteSample(r, g, b, brightness);
    }

    private static Rectangle ToPixelRect(Bitmap bitmap, PaletteProbe probe)
    {
        var x = Math.Clamp((int)Math.Round(bitmap.Width * probe.X), 0, bitmap.Width - 1);
        var y = Math.Clamp((int)Math.Round(bitmap.Height * probe.Y), 0, bitmap.Height - 1);
        var width = Math.Clamp((int)Math.Round(bitmap.Width * probe.Width), 1, bitmap.Width - x);
        var height = Math.Clamp((int)Math.Round(bitmap.Height * probe.Height), 1, bitmap.Height - y);
        return new Rectangle(x, y, width, height);
    }

    private enum ThemePalette
    {
        Light,
        Dark
    }

    private readonly record struct PaletteProbe(
        string Name,
        double X,
        double Y,
        double Width,
        double Height,
        double DarkMax,
        double LightMin);

    private readonly record struct PaletteSample(
        double R,
        double G,
        double B,
        double Brightness);

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
