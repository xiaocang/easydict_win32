using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class ThemeRegressionMemoryAutomationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string ProbeScriptPath = Path.Combine(ProjectRoot, "scripts", "perf", "Invoke-ThemeRegressionMemoryProbe.ps1");
    private static readonly string ThemeContrastTestPath = Path.Combine(ProjectRoot, "tests", "Easydict.UIAutomation.Tests", "Tests", "ThemeContrastTests.cs");

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
