using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private AppLauncher? _launcher;
    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, string?> _settingsSnapshots = new(StringComparer.OrdinalIgnoreCase);

    public DarkModeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DarkMode_MainWindow_ShouldRenderCorrectly()
    {
        var window = LaunchWithPersistedAppTheme("Light");

        // Capture light mode baseline first
        var pathLight = ScreenshotHelper.CaptureWindow(window, "30_main_light_mode");
        _output.WriteLine($"Light mode screenshot saved: {pathLight}");

        window = LaunchWithPersistedAppTheme("Dark");

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
        var window = LaunchWithPersistedAppTheme("Light");
        WaitForMainPage(window);
        CaptureAndAssertMainWindowPalette(window, ThemePalette.Light, "37_main_explicit_light_theme");

        window = LaunchWithPersistedAppTheme("Dark");
        WaitForMainPage(window);
        CaptureAndAssertMainWindowPalette(window, ThemePalette.Dark, "39_main_explicit_dark_theme");
    }

    [Fact]
    public void DarkMode_SettingsPage_ShouldRenderCorrectly()
    {
        var window = LaunchWithPersistedAppTheme("Dark");
        OpenSettingsPage(window);

        // Stay on settings page and capture
        Thread.Sleep(1000);
        var path = ScreenshotHelper.CaptureWindow(window, "32_settings_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        CaptureSettingsTab(
            window,
            "SettingsTab_Services",
            "DeepLServiceExpander",
            "33_settings_dark_services",
            "Services");
        CaptureSettingsTab(
            window,
            "SettingsTab_Advanced",
            "OcrEngineCombo",
            "34_settings_dark_advanced_config",
            "Advanced configuration");
    }

    [Fact]
    public void DarkMode_MiniWindow_ShouldRenderCorrectly()
    {
        var window = LaunchWithPersistedAppTheme("Dark");
        Thread.Sleep(1000);

        // Open mini window via hotkey: Ctrl+Alt+M
        _output.WriteLine("Opening mini window with Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
        Thread.Sleep(3000);

        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher!.Application, _launcher.Automation, "Mini", _output);
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
        var window = LaunchWithPersistedAppTheme("Dark");
        Thread.Sleep(1000);

        // Open fixed window via hotkey: Ctrl+Alt+F
        _output.WriteLine("Opening fixed window with Ctrl+Alt+F");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        Thread.Sleep(3000);

        var fixedWindow = UITestHelper.FindSecondaryWindow(
            _launcher!.Application, _launcher.Automation, "Fixed", _output);
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

    private Window LaunchWithPersistedAppTheme(string themeName)
    {
        _launcher?.Dispose();
        _launcher = null;

        SnapshotAndSetPersistedAppTheme(themeName);
        WaitForPersistedAppTheme(themeName, TimeSpan.FromSeconds(5)).Should().Be(
            themeName,
            $"the persisted AppTheme must be prepared before launching {themeName} screenshot coverage");

        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));

        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);
        return window;
    }

    private void OpenSettingsPage(Window window)
        => _ = FindAppThemeCombo(window);

    private void CaptureSettingsTab(
        Window window,
        string tabAutomationId,
        string expectedElementAutomationId,
        string screenshotName,
        string label)
    {
        var tab = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(tabAutomationId)),
            TimeSpan.FromSeconds(5)).Result;

        tab.Should().NotBeNull($"{label} settings tab must be available before dark-mode screenshot capture");
        ActivateSettingsTab(tab!, label);

        var expectedElement = Retry.WhileNull(
            () =>
            {
                var element = window.FindFirstDescendant(cf => cf.ByAutomationId(expectedElementAutomationId));
                return element is { IsOffscreen: false } ? element : null;
            },
            TimeSpan.FromSeconds(8)).Result;

        if (expectedElement is null)
        {
            TryScrollElementIntoView(
                window.FindFirstDescendant(cf => cf.ByAutomationId(expectedElementAutomationId)),
                expectedElementAutomationId);
            expectedElement = window.FindFirstDescendant(cf => cf.ByAutomationId(expectedElementAutomationId));
        }

        expectedElement.Should().NotBeNull(
            $"{label} settings screenshot must show {expectedElementAutomationId} instead of stale General tab content");

        var path = ScreenshotHelper.CaptureWindow(window, screenshotName);
        _output.WriteLine($"{label} settings screenshot saved: {path}");
    }

    private void ActivateSettingsTab(AutomationElement tab, string label)
    {
        _output.WriteLine($"Activating {label} settings tab at {tab.BoundingRectangle}");

        try
        {
            if (tab.Patterns.SelectionItem.IsSupported)
            {
                tab.Patterns.SelectionItem.Pattern.Select();
                Thread.Sleep(1200);
                return;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"{label} settings tab SelectionItem activation failed: {ex.Message}");
        }

        InvokeOrClick(tab);
        Thread.Sleep(1200);
    }

    private static string WaitForPersistedAppTheme(string expectedTheme, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string current;
        do
        {
            current = ReadPersistedAppTheme();
            if (string.Equals(current, expectedTheme, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return current;
    }

    private void SnapshotAndSetPersistedAppTheme(string theme)
    {
        var candidates = GetSettingsFileCandidates()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in candidates)
        {
            if (!_settingsSnapshots.ContainsKey(path))
            {
                _settingsSnapshots[path] = File.Exists(path) ? File.ReadAllText(path) : null;
            }

            if (File.Exists(path))
            {
                WriteAppTheme(path, theme);
            }
        }

        if (candidates.LastOrDefault() is { } localSettingsPath)
        {
            WriteAppTheme(localSettingsPath, theme);
        }
    }

    private static void WriteAppTheme(string path, string theme)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonNode root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root["AppTheme"] = theme;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RestoreSettingsSnapshots()
    {
        foreach (var (path, content) in _settingsSnapshots)
        {
            if (content is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    private static string ReadPersistedAppTheme()
    {
        try
        {
            var settingsPath = GetSettingsFileCandidates()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(File.Exists);
            if (!File.Exists(settingsPath))
            {
                return "<missing settings.json>";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("AppTheme", out var appTheme))
            {
                return "<missing AppTheme>";
            }

            return appTheme.ValueKind == JsonValueKind.String
                ? appTheme.GetString() ?? "<missing AppTheme>"
                : "<unreadable AppTheme>";
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    private static IEnumerable<string> GetSettingsFileCandidates()
    {
        if (UiaSettingsIsolation.TryGetSettingsFilePath() is { } isolatedSettingsPath)
        {
            yield return isolatedSettingsPath;
            yield break;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageFamilyName = Environment.GetEnvironmentVariable("EASYDICT_PACKAGE_FAMILY_NAME");

        foreach (var familyName in GetPackageFamilyNameCandidates(localAppData, packageFamilyName))
        {
            yield return Path.Combine(
                localAppData,
                "Packages",
                familyName,
                "LocalCache",
                "Local",
                "Easydict",
                "settings.json");
        }

        yield return Path.Combine(localAppData, "Easydict", "settings.json");
    }

    private static IEnumerable<string> GetPackageFamilyNameCandidates(
        string localAppData,
        string? packageFamilyName)
    {
        if (!string.IsNullOrWhiteSpace(packageFamilyName))
        {
            yield return packageFamilyName;
        }

        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packagesRoot))
        {
            yield break;
        }

        string[] packagePaths;
        try
        {
            packagePaths = Directory.GetDirectories(packagesRoot, "xiaocang.EasydictforWindows_*");
        }
        catch
        {
            yield break;
        }

        foreach (var path in packagePaths)
        {
            var familyName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                yield return familyName;
            }
        }
    }

    private ComboBox FindAppThemeCombo(Window window)
    {
        ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, 900, 900));
        window.SetForeground();
        Thread.Sleep(500);

        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(2)).Result;

        if (settingsButton is not null)
        {
            _output.WriteLine($"Clicking SettingsButton at {settingsButton.BoundingRectangle}");
            InvokeOrClick(settingsButton);
            Thread.Sleep(2000);
        }
        else
        {
            _output.WriteLine("SettingsButton not found before opening settings");
            var existingThemeCombo = window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo"))?.AsComboBox();
            if (existingThemeCombo is not null && !existingThemeCombo.IsOffscreen)
            {
                return existingThemeCombo;
            }
        }

        var scrollViewer = Retry.WhileNull(
            () =>
            {
                var viewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
                return viewer is { IsOffscreen: false } ? viewer : null;
            },
            TimeSpan.FromSeconds(20)).Result;

        if (scrollViewer is null)
        {
            var path = ScreenshotHelper.CaptureWindow(window, "darkmode_settings_navigation_failed");
            _output.WriteLine($"Settings navigation failed screenshot saved: {path}");
        }

        scrollViewer.Should().NotBeNull(
            "MainScrollViewer must appear on settings page once initialization finishes");

        Thread.Sleep(500);

        var element = Retry.WhileNull(
            () =>
            {
                var combo = window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo"));
                return combo is { IsOffscreen: false } ? combo : null;
            },
            TimeSpan.FromSeconds(5)).Result;

        if (element is null)
        {
            try
            {
                element = ScrollHelper.ScrollToFind(
                    scrollViewer!, startPercent: 70,
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo")),
                    _output.WriteLine);
            }
            catch (InvalidOperationException ex)
            {
                _output.WriteLine($"ScrollToFind AppThemeCombo failed, falling back to existing UIA element: {ex.Message}");
                element = window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo"));
                TryScrollElementIntoView(element, "AppThemeCombo");
            }
        }

        var themeCombo = element?.AsComboBox();

        themeCombo.Should().NotBeNull("AppThemeCombo must exist on settings page");

        _output.WriteLine($"Found AppThemeCombo at {themeCombo!.BoundingRectangle}");
        return themeCombo!;
    }

    private static void InvokeOrClick(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
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
            InvokeOrClick(backButton);
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
        var positionedByNativeWindow = ResizeMainWindowForScreenshot(window);
        if (!positionedByNativeWindow)
        {
            window.Move(0, 0);
        }

        Thread.Sleep(500);
        window.SetForeground();
        Thread.Sleep(500);
        _output.WriteLine($"{label} window bounds after move: {window.BoundingRectangle}");
    }

    private bool ResizeMainWindowForScreenshot(Window window)
    {
        try
        {
            if (ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, 900, 900)))
            {
                Thread.Sleep(500);
                return true;
            }

            if (!window.Patterns.Transform.IsSupported)
            {
                return false;
            }

            var transform = window.Patterns.Transform.Pattern;
            if (!transform.CanResize.Value)
            {
                return false;
            }

            transform.Resize(900, 1000);
            Thread.Sleep(500);
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Window resize skipped: {ex.Message}");
            return false;
        }
    }

    private void CaptureAndAssertMainWindowPalette(Window window, ThemePalette palette, string baseName)
    {
        ScrollMainContentToPercent(window, 0);
        PrepareMainWindowForScreenshot(window, $"{palette} top");
        var topPath = ScreenshotHelper.CaptureWindowPhysical(window, $"{baseName}_top_palette");
        _output.WriteLine($"Explicit {palette} top palette screenshot saved: {topPath}");
        AssertMainWindowPalette(topPath, palette, TopPaletteProbes);

        var serviceRows = ScrollServiceRowsIntoView(window);
        var servicePath = ScreenshotHelper.CaptureWindowPhysical(window, $"{baseName}_service_rows_palette");
        _output.WriteLine($"Explicit {palette} service rows screenshot saved: {servicePath}");
        AssertServiceResultRowsPalette(window, serviceRows, servicePath, palette);

        ScrollMainContentToPercent(window, 0);
    }

    private IReadOnlyList<AutomationElement> ScrollServiceRowsIntoView(Window window)
    {
        TryScrollElementIntoView(
            window.FindFirstDescendant(cf => cf.ByAutomationId("QuickOutputCard")),
            "QuickOutputCard");

        var firstServiceRow = FindServiceResultRows(window).FirstOrDefault();
        TryScrollElementIntoView(firstServiceRow, firstServiceRow is null ? "first service row" : GetAutomationIdOrEmpty(firstServiceRow));

        var bestRows = Array.Empty<AutomationElement>();
        foreach (var percent in new[] { 0d, 35d, 50d, 65d, 80d, 100d })
        {
            ScrollMainContentToPercent(window, percent);
            var rows = FindVisibleServiceResultRows(window);
            if (rows.Count > bestRows.Length)
            {
                bestRows = rows.ToArray();
            }

            if (rows.Count >= 3)
            {
                return rows.Take(3).ToArray();
            }
        }

        return bestRows.Take(3).ToArray();
    }

    private void TryScrollElementIntoView(AutomationElement? element, string label)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            if (element.Patterns.ScrollItem.IsSupported)
            {
                element.Patterns.ScrollItem.Pattern.ScrollIntoView();
                _output.WriteLine($"ScrollItem: scrolled {label} into view");
                Thread.Sleep(800);
                return;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"ScrollItem for {label} failed: {ex.Message}");
        }

        try
        {
            element.Focus();
            _output.WriteLine($"Focus: focused {label}");
            Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Focus for {label} failed: {ex.Message}");
        }
    }

    private IReadOnlyList<AutomationElement> FindVisibleServiceResultRows(Window window)
    {
        var windowBounds = window.BoundingRectangle;
        var rows = FindServiceResultRows(window)
            .Where(element => IsVisibleWithinWindow(element, windowBounds))
            .OrderBy(element => element.BoundingRectangle.Top)
            .ToArray();

        _output.WriteLine($"Visible service result rows: {string.Join(", ", rows.Select(row => $"{GetAutomationIdOrEmpty(row)}@{row.BoundingRectangle}"))}");
        return rows;
    }

    private static IReadOnlyList<AutomationElement> FindServiceResultRows(Window window)
    {
        var headers = FindServiceResultElementsByPrefix(window, "ServiceResultHeader_");
        return headers.Count >= 3
            ? headers
            : FindServiceResultElementsByPrefix(window, "ServiceResultItem_");
    }

    private static IReadOnlyList<AutomationElement> FindServiceResultElementsByPrefix(
        Window window,
        string automationIdPrefix)
    {
        return window.FindAllDescendants()
            .Where(element => GetAutomationIdOrEmpty(element).StartsWith(
                automationIdPrefix,
                StringComparison.Ordinal))
            .OrderBy(element => element.BoundingRectangle.Top)
            .ToArray();
    }

    private static string GetAutomationIdOrEmpty(AutomationElement element)
    {
        try
        {
            return element.AutomationId ?? string.Empty;
        }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return string.Empty;
        }
    }

    private static bool IsVisibleWithinWindow(
        AutomationElement element,
        Rectangle windowBounds)
    {
        var bounds = element.BoundingRectangle;
        return !element.IsOffscreen
            && bounds.Width > 40
            && bounds.Height > 10
            && bounds.Right > windowBounds.Left
            && bounds.Left < windowBounds.Right
            && bounds.Bottom > windowBounds.Top
            && bounds.Top < windowBounds.Bottom;
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
            Mouse.MoveTo(new Point(rect.Right - 24, rect.Top + (rect.Height / 2)));
        }
        else
        {
            _output.WriteLine("QuickTranslateContent not found, falling back to mouse wheel");
            Mouse.MoveTo(window.GetClickablePoint());
        }

        var scrollDelta = verticalPercent > 0 ? -5 : 5;
        var wheelTicks = verticalPercent > 0
            ? Math.Clamp((int)Math.Ceiling(verticalPercent / 5d), 8, 24)
            : 18;
        for (var i = 0; i < wheelTicks; i++)
        {
            Mouse.Scroll(scrollDelta);
            Thread.Sleep(40);
        }

        Thread.Sleep(800);
    }

    /// <summary>
    /// Dismiss the unsaved changes confirmation dialog by clicking "Don't Save".
    /// FindFirstDescendant can throw COMException(E_UNEXPECTED) while the UIA
    /// tree is transitioning between Settings and Main — swallow it inside the
    /// retry loop so the operation retries instead of failing the test outright.
    /// </summary>
    private void DismissUnsavedChangesDialog(Window window)
    {
        var dontSaveButton = Retry.WhileNull(
            () =>
            {
                try
                {
                    return window.FindFirstDescendant(cf => cf.ByName("Don't Save"))
                        ?? window.FindFirstDescendant(cf => cf.ByName("不保存"));
                }
                catch (COMException)
                {
                    return null;
                }
            },
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

    private void AssertServiceResultRowsPalette(
        Window window,
        IReadOnlyList<AutomationElement> serviceRows,
        string screenshotPath,
        ThemePalette palette)
    {
        serviceRows.Count.Should().BeGreaterThanOrEqualTo(
            1,
            $"at least one visible service result row must be present in {Path.GetFileName(screenshotPath)} for palette assertion");

        using var bitmap = new Bitmap(screenshotPath);
        var windowBounds = window.BoundingRectangle;
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        _output.WriteLine($"{palette} service row DPI scale: {dpiScale:0.###}");

        foreach (var row in serviceRows.Take(3))
        {
            var automationId = GetAutomationIdOrEmpty(row);
            var rowRect = ToScreenshotPixelRect(bitmap, row.BoundingRectangle, windowBounds, dpiScale);
            rowRect.Top.Should().BeGreaterThanOrEqualTo(
                0,
                $"{automationId} must be inside the captured main-window screenshot");
            rowRect.Top.Should().BeLessThan(
                bitmap.Height - 8,
                $"{automationId} must be visible in the captured main-window screenshot");
            rowRect.Height.Should().BeGreaterThan(
                20,
                $"{automationId} must have a visible service-row height in the captured main-window screenshot");

            var sampleRect = ToServiceRowSampleRect(bitmap, rowRect);
            var sample = AverageRegion(bitmap, sampleRect);
            _output.WriteLine(
                $"{palette} {automationId}: row={rowRect}, sample={sampleRect}, avg rgb=({sample.R:0.0}, {sample.G:0.0}, {sample.B:0.0}), brightness={sample.Brightness:0.0}");

            if (palette == ThemePalette.Dark)
            {
                sample.Brightness.Should().BeLessThan(
                    135,
                    $"{automationId} in {Path.GetFileName(screenshotPath)} must use dark service-row chrome instead of light-mode white fill");
            }
            else
            {
                sample.Brightness.Should().BeGreaterThan(
                    248,
                    $"{automationId} in {Path.GetFileName(screenshotPath)} must use light service-row chrome instead of dark-mode fill");
            }
        }
    }

    private static PaletteSample AverageRegion(Bitmap bitmap, PaletteProbe probe)
    {
        var rect = ToPixelRect(bitmap, probe);
        return AverageRegion(bitmap, rect);
    }

    private static PaletteSample AverageRegion(Bitmap bitmap, Rectangle rect)
    {
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

    private static Rectangle ToScreenshotPixelRect(
        Bitmap bitmap,
        Rectangle elementBounds,
        Rectangle windowBounds,
        double dpiScale)
    {
        var left = (int)Math.Round((elementBounds.Left - windowBounds.Left) * dpiScale);
        var top = (int)Math.Round((elementBounds.Top - windowBounds.Top) * dpiScale);
        var right = (int)Math.Round((elementBounds.Right - windowBounds.Left) * dpiScale);
        var bottom = (int)Math.Round((elementBounds.Bottom - windowBounds.Top) * dpiScale);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static Rectangle ToServiceRowSampleRect(Bitmap bitmap, Rectangle rowRect)
    {
        var left = rowRect.Left + (int)Math.Round(rowRect.Width * 0.42);
        var top = rowRect.Top + (int)Math.Round(rowRect.Height * 0.30);
        var width = Math.Max(12, (int)Math.Round(rowRect.Width * 0.24));
        var height = Math.Max(6, (int)Math.Round(rowRect.Height * 0.40));

        return ClipRect(
            new Rectangle(left, top, width, height),
            bitmap.Width,
            bitmap.Height);
    }

    private static Rectangle ClipRect(Rectangle rect, int maxWidth, int maxHeight)
    {
        var left = Math.Clamp(rect.Left, 0, maxWidth - 1);
        var top = Math.Clamp(rect.Top, 0, maxHeight - 1);
        var right = Math.Clamp(rect.Right, left + 1, maxWidth);
        var bottom = Math.Clamp(rect.Bottom, top + 1, maxHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
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
        _launcher?.Dispose();
        RestoreSettingsSnapshots();
    }
}
