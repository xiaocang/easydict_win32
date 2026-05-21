using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class ThemeRegressionMemoryAutomationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string ProbeScriptPath = Path.Combine(ProjectRoot, "scripts", "perf", "Invoke-ThemeRegressionMemoryProbe.ps1");
    private static readonly string ThemeContrastTestPath = Path.Combine(ProjectRoot, "tests", "Easydict.UIAutomation.Tests", "Tests", "ThemeContrastTests.cs");
    private static readonly string AppPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "App.xaml.cs");

    [Fact]
    public void ThemeRegressionMemoryProbe_RunsThemeMatrixAndSummarizesMemoryCsv()
    {
        var script = File.ReadAllText(ProbeScriptPath);

        script.Should().Contain("ThemeContrastTests.ThemeMatrix_LightAndDarkAppThemes_OnLightAndDarkWindowsThemes_ShouldCaptureNamedScreenshots",
            "the probe should use the broad light/dark theme matrix regression");
        script.Should().Contain("SCREENSHOT_OUTPUT_DIR",
            "theme screenshots and memory CSV should be isolated under the requested artifact directory");
        script.Should().Contain("theme-contrast-regression\\theme-matrix");
        script.Should().Contain("theme-contrast_memory.csv");
        script.Should().Contain("theme-memory-summary.json");
        script.Should().Contain("Import-Csv");
        script.Should().Contain("Group-Object case");
        script.Should().Contain("peakWorkingSetMb");
        script.Should().Contain("deltaPrivateMb");
    }

    [Fact]
    public void ThemeMatrixTest_CapturesMemorySamplesAcrossThemeSwitchMarkers()
    {
        var test = File.ReadAllText(ThemeContrastTestPath);

        test.Should().Contain("PrepareThemeMatrixMemoryCsv");
        test.Should().Contain("CaptureThemeMatrixMemory(testCase, \"after-launch\")");
        test.Should().Contain("CaptureThemeMatrixMemory(testCase, \"after-theme-select\")");
        test.Should().Contain("CaptureThemeMatrixMemory(testCase, \"after-settings-general\")");
        test.Should().Contain("CaptureThemeMatrixMemory(testCase, \"after-main\")");
        test.Should().Contain("WorkingSetMb");
        test.Should().Contain("PrivateMb");
        test.Should().Contain("EmitThemeMatrixMemorySummary");
    }

    [Fact]
    public void ThemeContrastTest_CoversRuntimeFollowSystemWindowsThemeChanges()
    {
        var test = File.ReadAllText(ThemeContrastTestPath);

        test.Should().Contain("MainWindow_FollowSystemTheme_WhenWindowsThemeChanges_ShouldUpdateWhileRunning",
            "issue 161 regressed while the app was already running in Follow System mode");
        test.Should().Contain("SnapshotAndSetPersistedAppTheme(\"System\")");
        test.Should().Contain("ForceWindowsTheme(light: true)");
        test.Should().Contain("ForceWindowsTheme(light: false)");
        test.Should().Contain("WaitForMainPalette(",
            "the regression should verify the live window palette after each Windows theme broadcast");
    }

    [Fact]
    public void App_ListensForWindowsThemeChangesWhenFollowingSystem()
    {
        var app = File.ReadAllText(AppPath);

        app.Should().Contain("SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged");
        app.Should().Contain("SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged");
        app.Should().Contain("SetWindowSubclass(_themeSubclassHwnd");
        app.Should().Contain("RemoveWindowSubclass(_themeSubclassHwnd");
        app.Should().Contain("WM_SETTINGCHANGE");
        app.Should().Contain("WM_THEMECHANGED");
        app.Should().Contain("IsSystemTheme(SettingsService.Instance.AppTheme)",
            "explicit Light/Dark themes must stay pinned while Follow System reacts to Windows theme changes");
        app.Should().Contain("QueueSystemThemeRefresh");
        app.Should().Contain("RefreshSystemThemeIfChanged");
        app.Should().Contain("SystemThemeProbe.IsSystemDark()");
        app.Should().Contain("ApplyTheme(SettingsService.Instance.AppTheme)");
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
