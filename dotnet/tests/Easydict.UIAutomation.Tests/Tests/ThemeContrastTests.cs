using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class ThemeContrastTests : IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const string SystemUsesLightThemeValue = "SystemUsesLightTheme";
    private const string ThemeContrastScreenshotRootName = "theme-contrast-regression";
    private const string ThemeMatrixScreenshotDirectoryName = "theme-matrix";
    private const string ThemeContrastScreenshotFilePrefix = "theme-contrast";
    private static readonly ThemeMatrixCase[] ThemeMatrixCases =
    [
        new("light", true, "light", "Light", 1, true),
        new("light", true, "dark", "Dark", 2, false),
        new("dark", false, "light", "Light", 1, true),
        new("dark", false, "dark", "Dark", 2, false)
    ];
    private static readonly SettingsTabScreenshot[] ThemeMatrixSettingsTabs =
    [
        new(
            "SettingsTab_Services",
            "DeepLKeyRevealButton",
            "settings-services-credentials",
            "DeepL API key reveal button",
            "DeepLServiceExpander",
            45),
        new("SettingsTab_Views", "MainWindowReorderModeButton", "settings-views", "views reorder button"),
        new("SettingsTab_Hotkeys", "ShowHotkeyBox", "settings-hotkeys", "hotkey text box"),
        new("SettingsTab_Language", "FirstLanguageCombo", "settings-language", "language combo"),
        new("SettingsTab_Advanced", "OcrEngineCombo", "settings-advanced", "advanced OCR engine combo")
    ];

    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, int?> _originalThemeValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _settingsSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private AppLauncher? _launcher;

    public ThemeContrastTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SettingsPage_ExplicitLightTheme_OnDarkWindowsTheme_ShouldRenderLightControls()
    {
        SnapshotAndSetPersistedAppTheme("System");
        ForceWindowsTheme(light: false);

        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));

        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var themeCombo = FindAppThemeCombo(window);
        SelectThemeComboItem(themeCombo, themeIndex: 1);

        WaitForPersistedAppTheme("Light", TimeSpan.FromSeconds(5))
            .Should().Be("Light", "explicit Light must persist before screenshot validation");

        Thread.Sleep(1200);
        PrepareSettingsWindowForScreenshot(window);

        var path = ScreenshotHelper.CaptureWindowPhysical(
            window,
            "40_settings_light_on_dark_system_contrast");
        _output.WriteLine($"Light-on-dark-system settings screenshot saved: {path}");

        AssertSettingsLightPalette(window, path);
        CaptureSettingsTabAndAssertElementLight(
            window,
            "SettingsTab_Services",
            "DeepLKeyRevealButton",
            "42_settings_services_credentials_light_on_dark_system_contrast",
            "DeepL API key reveal button",
            "DeepLServiceExpander",
            45);
        CaptureSettingsTabAndAssertElementLight(
            window,
            "SettingsTab_Views",
            "MainWindowReorderModeButton",
            "43_settings_views_light_on_dark_system_contrast",
            "views reorder button");
        CaptureSettingsTabAndAssertElementLight(
            window,
            "SettingsTab_Hotkeys",
            "ShowHotkeyBox",
            "44_settings_hotkeys_light_on_dark_system_contrast",
            "hotkey text box");
        CaptureSettingsTabAndAssertElementLight(
            window,
            "SettingsTab_Language",
            "FirstLanguageCombo",
            "45_settings_language_light_on_dark_system_contrast",
            "language combo");
        CaptureSettingsTabAndAssertElementLight(
            window,
            "SettingsTab_Advanced",
            "OcrEngineCombo",
            "46_settings_advanced_light_on_dark_system_contrast",
            "advanced OCR engine combo");
    }

    [Fact]
    public void MainWindow_ExplicitLightTheme_OnDarkWindowsTheme_ShouldRenderLightChrome()
    {
        SnapshotAndSetPersistedAppTheme("System");
        ForceWindowsTheme(light: false);

        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));

        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var themeCombo = FindAppThemeCombo(window);
        SelectThemeComboItem(themeCombo, themeIndex: 1);

        WaitForPersistedAppTheme("Light", TimeSpan.FromSeconds(5))
            .Should().Be("Light", "explicit Light must persist before main-window screenshot validation");

        Thread.Sleep(1200);
        NavigateBackToMain(window);
        WaitForMainPage(window);
        PrepareMainWindowForScreenshot(window);

        var path = ScreenshotHelper.CaptureWindowPhysical(
            window,
            "41_main_light_on_dark_system_contrast");
        _output.WriteLine($"Light-on-dark-system main screenshot saved: {path}");

        AssertMainLightPalette(window, path);
    }

    [Fact]
    public void ThemeMatrix_LightAndDarkAppThemes_OnLightAndDarkWindowsThemes_ShouldCaptureNamedScreenshots()
    {
        var previousOutputDir = ScreenshotHelper.OutputDir;
        ScreenshotHelper.OutputDir = PrepareThemeMatrixScreenshotDirectory();

        try
        {
            foreach (var testCase in ThemeMatrixCases)
            {
                CaptureThemeMatrixCase(testCase);
            }
        }
        finally
        {
            ScreenshotHelper.OutputDir = previousOutputDir;
        }
    }

    private void CaptureThemeMatrixCase(ThemeMatrixCase testCase)
    {
        _output.WriteLine(
            $"Theme matrix case: Windows={testCase.OsSlug}, App={testCase.AppSlug}");

        _launcher?.Dispose();
        _launcher = null;

        SnapshotAndSetPersistedAppTheme("System");
        ForceWindowsTheme(testCase.WindowsLight);

        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));

        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var themeCombo = FindAppThemeCombo(window);
        SelectThemeComboItem(themeCombo, testCase.ThemeIndex);

        WaitForPersistedAppTheme(testCase.AppTheme, TimeSpan.FromSeconds(5))
            .Should().Be(
                testCase.AppTheme,
                "the app theme must persist before theme-matrix screenshots are captured");

        Thread.Sleep(1200);
        CaptureThemeMatrixSettingsGeneral(window, testCase);
        foreach (var tab in ThemeMatrixSettingsTabs)
        {
            CaptureThemeMatrixSettingsTab(window, testCase, tab);
        }

        NavigateBackToMain(window);
        WaitForMainPage(window);
        PrepareMainWindowForScreenshot(window);

        var mainPath = ScreenshotHelper.CaptureWindowPhysical(
            window,
            $"{testCase.ScreenshotPrefix}_page-main");
        _output.WriteLine($"Theme matrix main screenshot saved: {mainPath}");
        AssertMainPalette(window, mainPath, testCase.ExpectedLight);

        _launcher.Dispose();
        _launcher = null;
    }

    private void CaptureThemeMatrixSettingsGeneral(Window window, ThemeMatrixCase testCase)
    {
        PrepareSettingsWindowForScreenshot(window);

        var path = ScreenshotHelper.CaptureWindowPhysical(
            window,
            $"{testCase.ScreenshotPrefix}_page-settings-general");
        _output.WriteLine($"Theme matrix settings General screenshot saved: {path}");
        AssertSettingsPalette(window, path, testCase.ExpectedLight);
    }

    private void CaptureThemeMatrixSettingsTab(
        Window window,
        ThemeMatrixCase testCase,
        SettingsTabScreenshot tab)
    {
        var tabElement = FindRequired(window, tab.TabAutomationId);
        InvokeOrClick(tabElement);
        Thread.Sleep(1200);
        PrepareSettingsWindowForScreenshot(window);
        ExpandSettingsExpanderIfNeeded(window, tab.ExpanderAutomationId);
        ScrollSettingsElementIntoView(window, tab.ElementAutomationId, tab.InitialScrollPercent);
        MoveFocusAwayFromTabs(window, tab.ElementAutomationId);

        var path = ScreenshotHelper.CaptureWindowPhysical(
            window,
            $"{testCase.ScreenshotPrefix}_page-{tab.PageSlug}");
        _output.WriteLine($"Theme matrix {tab.PageSlug} screenshot saved: {path}");

        using var bitmap = new Bitmap(path);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        AssertElementRegionMatchesPalette(
            tab.Label,
            FindRequired(window, tab.ElementAutomationId),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.12,
            relativeY: 0.20,
            relativeWidth: 0.48,
            relativeHeight: 0.50,
            expectedLight: testCase.ExpectedLight,
            minLightBrightness: 150,
            maxDarkBrightness: 130);
    }

    private ComboBox FindAppThemeCombo(Window window)
    {
        ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, 900, 900));
        window.SetForeground();
        Thread.Sleep(500);

        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(5)).Result;

        settingsButton.Should().NotBeNull("main page SettingsButton must be available");
        InvokeOrClick(settingsButton!);
        Thread.Sleep(2000);

        var scrollViewer = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer")),
            TimeSpan.FromSeconds(15)).Result;

        scrollViewer.Should().NotBeNull("settings MainScrollViewer must appear after opening settings");

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
                    scrollViewer!,
                    startPercent: 70,
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo")),
                    _output.WriteLine);
            }
            catch (InvalidOperationException ex)
            {
                _output.WriteLine($"ScrollToFind AppThemeCombo failed, falling back to existing UIA element: {ex.Message}");
                element = window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo"));
                TryScrollIntoView(element);
            }
        }

        var themeCombo = element?.AsComboBox();

        themeCombo.Should().NotBeNull("AppThemeCombo must be visible on the General settings tab");
        return themeCombo!;
    }

    private void TryScrollIntoView(AutomationElement? element)
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
                Thread.Sleep(800);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"ScrollItem fallback failed: {ex.Message}");
        }
    }

    private void SelectThemeComboItem(ComboBox themeCombo, int themeIndex)
    {
        themeCombo.Expand();
        Thread.Sleep(500);

        var items = themeCombo.Items;
        _output.WriteLine(
            $"AppThemeCombo exposed {items.Length} item(s): {string.Join(", ", items.Select(i => $"'{i.Name}'"))}");

        items.Length.Should().BeGreaterThan(themeIndex, "Light theme item must be available at index 1");
        items[themeIndex].Click();
    }

    private void PrepareSettingsWindowForScreenshot(Window window)
    {
        ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, 900, 900));
        Thread.Sleep(500);
        window.SetForeground();
        MovePointerAwayFromTabs(window);
        Thread.Sleep(500);
    }

    private void ExpandSettingsExpanderIfNeeded(Window window, string? expanderAutomationId)
    {
        if (string.IsNullOrWhiteSpace(expanderAutomationId))
        {
            return;
        }

        var expander = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(expanderAutomationId)),
            TimeSpan.FromSeconds(5)).Result;

        expander.Should().NotBeNull($"{expanderAutomationId} expander must be available before screenshot capture");
        TryScrollIntoView(expander);
        if (expander!.Patterns.ExpandCollapse.IsSupported)
        {
            expander.Patterns.ExpandCollapse.Pattern.Expand();
        }
        else
        {
            InvokeOrClick(expander);
        }

        Thread.Sleep(1000);
    }

    private void ScrollSettingsElementIntoView(
        Window window,
        string automationId,
        double? initialScrollPercent = null)
    {
        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (scrollViewer is not null)
        {
            try
            {
                if (initialScrollPercent.HasValue)
                {
                    ScrollHelper.ScrollToPercent(scrollViewer, initialScrollPercent.Value, _output.WriteLine);
                }

                var element = ScrollHelper.ScrollToFind(
                    scrollViewer,
                    startPercent: initialScrollPercent ?? 45,
                    () => FindVisibleInWindow(window, automationId),
                    _output.WriteLine);

                if (element is not null)
                {
                    return;
                }
            }
            catch (InvalidOperationException ex)
            {
                _output.WriteLine($"ScrollToFind {automationId} failed, falling back to ScrollItem: {ex.Message}");
            }
        }

        TryScrollIntoView(window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)));
    }

    private static AutomationElement? FindVisibleInWindow(Window window, string automationId)
    {
        var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is null || element.IsOffscreen)
        {
            return null;
        }

        var windowBounds = window.BoundingRectangle;
        var elementBounds = element.BoundingRectangle;
        return elementBounds.Top >= windowBounds.Top
            && elementBounds.Bottom <= windowBounds.Bottom
            && elementBounds.Left >= windowBounds.Left
            && elementBounds.Right <= windowBounds.Right
            ? element
            : null;
    }

    private void CaptureSettingsTabAndAssertElementLight(
        Window window,
        string tabAutomationId,
        string elementAutomationId,
        string screenshotName,
        string label,
        string? expanderAutomationId = null,
        double? initialScrollPercent = null)
    {
        var tab = FindRequired(window, tabAutomationId);
        InvokeOrClick(tab);
        Thread.Sleep(1200);
        PrepareSettingsWindowForScreenshot(window);
        ExpandSettingsExpanderIfNeeded(window, expanderAutomationId);
        ScrollSettingsElementIntoView(window, elementAutomationId, initialScrollPercent);
        MoveFocusAwayFromTabs(window, elementAutomationId);

        var path = ScreenshotHelper.CaptureWindowPhysical(window, screenshotName);
        _output.WriteLine($"{label} settings screenshot saved: {path}");

        using var bitmap = new Bitmap(path);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        AssertElementRegionIsLight(
            label,
            FindRequired(window, elementAutomationId),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.12,
            relativeY: 0.20,
            relativeWidth: 0.48,
            relativeHeight: 0.50,
            minBrightness: 150);
    }

    private void PrepareMainWindowForScreenshot(Window window)
    {
        ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, 900, 900));
        Thread.Sleep(500);
        window.SetForeground();
        MovePointerAwayFromTabs(window);
        Thread.Sleep(500);
    }

    private static void MovePointerAwayFromTabs(Window window)
    {
        try
        {
            var bounds = window.BoundingRectangle;
            Mouse.MoveTo(new Point(bounds.Left + 20, bounds.Bottom - 20));
        }
        catch
        {
            // The screenshot assertions do not depend on pointer placement.
        }
    }

    private void MoveFocusAwayFromTabs(Window window, string fallbackAutomationId)
    {
        var focusTarget = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"))
            ?? window.FindFirstDescendant(cf => cf.ByAutomationId(fallbackAutomationId));

        try
        {
            focusTarget?.Focus();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Unable to move focus away from settings tab: {ex.Message}");
        }

        MovePointerAwayFromTabs(window);
        Thread.Sleep(1000);
    }

    private void NavigateBackToMain(Window window)
    {
        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (scrollViewer is not null)
        {
            ScrollHelper.ScrollToPercent(scrollViewer, 0, _output.WriteLine);
            Thread.Sleep(500);
        }

        var backButton = window.FindFirstDescendant(cf => cf.ByAutomationId("FloatingBackButton"))
            ?? window.FindFirstDescendant(cf => cf.ByAutomationId("BackButton"));

        backButton.Should().NotBeNull("settings back button must be available before returning to the main page");
        InvokeOrClick(backButton!);
        Thread.Sleep(1000);
        DismissUnsavedChangesDialog(window);
    }

    private void WaitForMainPage(Window window)
    {
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;

        settingsButton.Should().NotBeNull("SettingsButton must be visible on the main page before screenshot validation");
        Thread.Sleep(700);
    }

    private void DismissUnsavedChangesDialog(Window window)
    {
        var dontSaveButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("Don't Save"))
                ?? window.FindFirstDescendant(cf => cf.ByName("不保存")),
            TimeSpan.FromSeconds(3)).Result;

        if (dontSaveButton is not null)
        {
            _output.WriteLine("Unsaved changes dialog detected - clicking Don't Save");
            dontSaveButton.Click();
            Thread.Sleep(1000);
        }
    }

    private void AssertSettingsLightPalette(Window window, string screenshotPath)
    {
        AssertSettingsPalette(window, screenshotPath, expectedLight: true);
    }

    private void AssertSettingsPalette(Window window, string screenshotPath, bool expectedLight)
    {
        using var bitmap = new Bitmap(screenshotPath);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        _output.WriteLine($"Settings theme-matrix DPI scale: {dpiScale:0.###}");

        AssertElementRegionMatchesPalette(
            "selected General settings tab",
            FindRequired(window, "SettingsTab_General"),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.12,
            relativeY: 0.12,
            relativeWidth: 0.25,
            relativeHeight: 0.25,
            expectedLight: expectedLight,
            minLightBrightness: 170,
            maxDarkBrightness: 130);

        AssertElementRegionMatchesPalette(
            "unselected Services settings tab",
            FindRequired(window, "SettingsTab_Services"),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.12,
            relativeY: 0.12,
            relativeWidth: 0.25,
            relativeHeight: 0.25,
            expectedLight: expectedLight,
            minLightBrightness: 170,
            maxDarkBrightness: 130);

        AssertElementRegionMatchesPalette(
            "AppThemeCombo field",
            FindRequired(window, "AppThemeCombo"),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.12,
            relativeY: 0.20,
            relativeWidth: 0.52,
            relativeHeight: 0.55,
            expectedLight: expectedLight,
            minLightBrightness: 150,
            maxDarkBrightness: 130);
    }

    private void AssertMainLightPalette(Window window, string screenshotPath)
    {
        AssertMainPalette(window, screenshotPath, expectedLight: true);
    }

    private void AssertMainPalette(Window window, string screenshotPath, bool expectedLight)
    {
        using var bitmap = new Bitmap(screenshotPath);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        _output.WriteLine($"Main theme-matrix DPI scale: {dpiScale:0.###}");

        AssertElementRegionMatchesPalette(
            "source input field",
            FindRequired(window, "InputTextBox"),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.12,
            relativeY: 0.42,
            relativeWidth: 0.42,
            relativeHeight: 0.26,
            expectedLight: expectedLight,
            minLightBrightness: 175,
            maxDarkBrightness: 130);

        if (expectedLight)
        {
            AssertElementRegionHasDarkPixels(
                "main mode title",
                FindRequired(window, "ModeTitleText"),
                bitmap,
                windowBounds,
                dpiScale,
                minDarkPixelRatio: 0.04);
        }
        else
        {
            AssertElementRegionHasLightPixels(
                "main mode title",
                FindRequired(window, "ModeTitleText"),
                bitmap,
                windowBounds,
                dpiScale,
                minLightPixelRatio: 0.04);
        }

        AssertElementRegionMatchesPalette(
            "source language combo",
            FindRequired(window, "SourceLangCombo"),
            bitmap,
            windowBounds,
            dpiScale,
            relativeX: 0.14,
            relativeY: 0.22,
            relativeWidth: 0.40,
            relativeHeight: 0.46,
            expectedLight: expectedLight,
            minLightBrightness: 150,
            maxDarkBrightness: 130);

        AssertBitmapRegionMatchesPalette(
            "quick output card",
            bitmap,
            relativeX: 0.12,
            relativeY: 0.72,
            relativeWidth: 0.24,
            relativeHeight: 0.045,
            expectedLight: expectedLight,
            minLightBrightness: 175,
            maxDarkBrightness: 130);
    }

    private static AutomationElement FindRequired(Window window, string automationId)
    {
        var element = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(5)).Result;

        element.Should().NotBeNull($"{automationId} must be present for screenshot palette validation");
        return element!;
    }

    private void AssertElementRegionIsLight(
        string label,
        AutomationElement element,
        Bitmap bitmap,
        Rectangle windowBounds,
        double dpiScale,
        double relativeX,
        double relativeY,
        double relativeWidth,
        double relativeHeight,
        double minBrightness)
    {
        var elementRect = ToScreenshotPixelRect(bitmap, element.BoundingRectangle, windowBounds, dpiScale);
        var sampleRect = ToRelativeSampleRect(
            bitmap,
            elementRect,
            relativeX,
            relativeY,
            relativeWidth,
            relativeHeight);
        var sample = AverageRegion(bitmap, sampleRect);

        _output.WriteLine(
            $"{label}: element={elementRect}, sample={sampleRect}, avg rgb=({sample.R:0.0}, {sample.G:0.0}, {sample.B:0.0}), brightness={sample.Brightness:0.0}");

        sample.Brightness.Should().BeGreaterThan(
            minBrightness,
            $"{label} must use the explicit Light palette when Windows app theme is dark");
    }

    private void AssertElementRegionMatchesPalette(
        string label,
        AutomationElement element,
        Bitmap bitmap,
        Rectangle windowBounds,
        double dpiScale,
        double relativeX,
        double relativeY,
        double relativeWidth,
        double relativeHeight,
        bool expectedLight,
        double minLightBrightness,
        double maxDarkBrightness)
    {
        var elementRect = ToScreenshotPixelRect(bitmap, element.BoundingRectangle, windowBounds, dpiScale);
        var sampleRect = ToRelativeSampleRect(
            bitmap,
            elementRect,
            relativeX,
            relativeY,
            relativeWidth,
            relativeHeight);
        var sample = AverageRegion(bitmap, sampleRect);

        _output.WriteLine(
            $"{label}: element={elementRect}, sample={sampleRect}, avg rgb=({sample.R:0.0}, {sample.G:0.0}, {sample.B:0.0}), brightness={sample.Brightness:0.0}, expected={(expectedLight ? "Light" : "Dark")}");

        if (expectedLight)
        {
            sample.Brightness.Should().BeGreaterThan(
                minLightBrightness,
                $"{label} must use the explicit Light palette");
            return;
        }

        sample.Brightness.Should().BeLessThan(
            maxDarkBrightness,
            $"{label} must use the explicit Dark palette");
    }

    private void AssertBitmapRegionIsLight(
        string label,
        Bitmap bitmap,
        double relativeX,
        double relativeY,
        double relativeWidth,
        double relativeHeight,
        double minBrightness)
    {
        var rect = new Rectangle(
            (int)Math.Round(bitmap.Width * relativeX),
            (int)Math.Round(bitmap.Height * relativeY),
            Math.Max(1, (int)Math.Round(bitmap.Width * relativeWidth)),
            Math.Max(1, (int)Math.Round(bitmap.Height * relativeHeight)));
        rect = ClipRect(rect, bitmap.Width, bitmap.Height);
        var sample = AverageRegion(bitmap, rect);

        _output.WriteLine(
            $"{label}: sample={rect}, avg rgb=({sample.R:0.0}, {sample.G:0.0}, {sample.B:0.0}), brightness={sample.Brightness:0.0}");

        sample.Brightness.Should().BeGreaterThan(
            minBrightness,
            $"{label} must use the explicit Light palette when Windows app theme is dark");
    }

    private void AssertBitmapRegionMatchesPalette(
        string label,
        Bitmap bitmap,
        double relativeX,
        double relativeY,
        double relativeWidth,
        double relativeHeight,
        bool expectedLight,
        double minLightBrightness,
        double maxDarkBrightness)
    {
        var rect = new Rectangle(
            (int)Math.Round(bitmap.Width * relativeX),
            (int)Math.Round(bitmap.Height * relativeY),
            Math.Max(1, (int)Math.Round(bitmap.Width * relativeWidth)),
            Math.Max(1, (int)Math.Round(bitmap.Height * relativeHeight)));
        rect = ClipRect(rect, bitmap.Width, bitmap.Height);
        var sample = AverageRegion(bitmap, rect);

        _output.WriteLine(
            $"{label}: sample={rect}, avg rgb=({sample.R:0.0}, {sample.G:0.0}, {sample.B:0.0}), brightness={sample.Brightness:0.0}, expected={(expectedLight ? "Light" : "Dark")}");

        if (expectedLight)
        {
            sample.Brightness.Should().BeGreaterThan(
                minLightBrightness,
                $"{label} must use the explicit Light palette");
            return;
        }

        sample.Brightness.Should().BeLessThan(
            maxDarkBrightness,
            $"{label} must use the explicit Dark palette");
    }

    private void AssertElementRegionHasDarkPixels(
        string label,
        AutomationElement element,
        Bitmap bitmap,
        Rectangle windowBounds,
        double dpiScale,
        double minDarkPixelRatio)
    {
        var elementRect = ToScreenshotPixelRect(bitmap, element.BoundingRectangle, windowBounds, dpiScale);
        var darkPixels = 0;
        var count = 0;

        for (var y = elementRect.Top; y < elementRect.Bottom; y++)
        {
            for (var x = elementRect.Left; x < elementRect.Right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var brightness = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
                if (brightness < 130)
                {
                    darkPixels++;
                }

                count++;
            }
        }

        var ratio = count == 0 ? 0 : darkPixels / (double)count;
        _output.WriteLine($"{label}: element={elementRect}, dark-pixel ratio={ratio:0.000}");

        ratio.Should().BeGreaterThan(
            minDarkPixelRatio,
            $"{label} must render dark foreground text on the explicit Light main window");
    }

    private void AssertElementRegionHasLightPixels(
        string label,
        AutomationElement element,
        Bitmap bitmap,
        Rectangle windowBounds,
        double dpiScale,
        double minLightPixelRatio)
    {
        var elementRect = ToScreenshotPixelRect(bitmap, element.BoundingRectangle, windowBounds, dpiScale);
        var lightPixels = 0;
        var count = 0;

        for (var y = elementRect.Top; y < elementRect.Bottom; y++)
        {
            for (var x = elementRect.Left; x < elementRect.Right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var brightness = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
                if (brightness > 170)
                {
                    lightPixels++;
                }

                count++;
            }
        }

        var ratio = count == 0 ? 0 : lightPixels / (double)count;
        _output.WriteLine($"{label}: element={elementRect}, light-pixel ratio={ratio:0.000}");

        ratio.Should().BeGreaterThan(
            minLightPixelRatio,
            $"{label} must render light foreground text on the explicit Dark main window");
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

        return ClipRect(Rectangle.FromLTRB(left, top, right, bottom), bitmap.Width, bitmap.Height);
    }

    private static Rectangle ToRelativeSampleRect(
        Bitmap bitmap,
        Rectangle elementRect,
        double relativeX,
        double relativeY,
        double relativeWidth,
        double relativeHeight)
    {
        var sample = new Rectangle(
            elementRect.Left + (int)Math.Round(elementRect.Width * relativeX),
            elementRect.Top + (int)Math.Round(elementRect.Height * relativeY),
            Math.Max(1, (int)Math.Round(elementRect.Width * relativeWidth)),
            Math.Max(1, (int)Math.Round(elementRect.Height * relativeHeight)));

        return ClipRect(sample, bitmap.Width, bitmap.Height);
    }

    private static Rectangle ClipRect(Rectangle rect, int maxWidth, int maxHeight)
    {
        var left = Math.Clamp(rect.Left, 0, maxWidth - 1);
        var top = Math.Clamp(rect.Top, 0, maxHeight - 1);
        var right = Math.Clamp(rect.Right, left + 1, maxWidth);
        var bottom = Math.Clamp(rect.Bottom, top + 1, maxHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
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

    private static string PrepareThemeMatrixScreenshotDirectory()
    {
        var screenshotRoot = Environment.GetEnvironmentVariable("SCREENSHOT_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(screenshotRoot))
        {
            screenshotRoot = Path.Combine(FindRepositoryRoot(), "artifacts");
        }

        var outputDir = Path.Combine(
            screenshotRoot,
            ThemeContrastScreenshotRootName,
            ThemeMatrixScreenshotDirectoryName);
        Directory.CreateDirectory(outputDir);

        foreach (var path in Directory.GetFiles(outputDir, $"{ThemeContrastScreenshotFilePrefix}_*.png"))
        {
            File.Delete(path);
        }

        return outputDir;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private void ForceWindowsTheme(bool light)
    {
        CaptureOriginalThemeValues();

        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeRegistryPath);
        key.Should().NotBeNull("Windows theme registry key must be writable for UI regression setup");

        var value = light ? 1 : 0;
        key!.SetValue(AppsUseLightThemeValue, value, RegistryValueKind.DWord);
        key.SetValue(SystemUsesLightThemeValue, value, RegistryValueKind.DWord);
        BroadcastThemeChange();
    }

    private void CaptureOriginalThemeValues()
    {
        if (_originalThemeValues.Count > 0)
        {
            return;
        }

        _originalThemeValues[AppsUseLightThemeValue] = ReadThemeDword(AppsUseLightThemeValue);
        _originalThemeValues[SystemUsesLightThemeValue] = ReadThemeDword(SystemUsesLightThemeValue);
    }

    private static int? ReadThemeDword(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
        return key?.GetValue(name) is int value ? value : null;
    }

    private void RestoreWindowsTheme()
    {
        if (_originalThemeValues.Count == 0)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeRegistryPath);
        if (key is null)
        {
            return;
        }

        foreach (var (name, value) in _originalThemeValues)
        {
            if (value.HasValue)
            {
                key.SetValue(name, value.Value, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        BroadcastThemeChange();
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

    private static void InvokeOrClick(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
    }

    private static void BroadcastThemeChange()
    {
        SendMessageTimeout(
            new IntPtr(0xffff),
            0x001a,
            UIntPtr.Zero,
            "ImmersiveColorSet",
            0x0002,
            1000,
            out _);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    private readonly record struct ThemeMatrixCase(
        string OsSlug,
        bool WindowsLight,
        string AppSlug,
        string AppTheme,
        int ThemeIndex,
        bool ExpectedLight)
    {
        public string ScreenshotPrefix => $"{ThemeContrastScreenshotFilePrefix}_os-{OsSlug}_app-{AppSlug}";
    }

    private readonly record struct SettingsTabScreenshot(
        string TabAutomationId,
        string ElementAutomationId,
        string PageSlug,
        string Label,
        string? ExpanderAutomationId = null,
        double? InitialScrollPercent = null);

    private readonly record struct PaletteSample(
        double R,
        double G,
        double B,
        double Brightness);

    public void Dispose()
    {
        _launcher?.Dispose();
        RestoreSettingsSnapshots();
        RestoreWindowsTheme();
    }
}
