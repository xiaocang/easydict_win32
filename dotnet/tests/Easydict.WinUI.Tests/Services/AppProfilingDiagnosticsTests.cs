using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Static regression checks for DEBUG-only profiling overrides that should not drift silently.
/// </summary>
[Trait("Category", "Configuration")]
public class AppProfilingDiagnosticsTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string AppPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "App.xaml.cs");

    [Fact]
    public void App_UsesDebugFlagToDisableMouseSelectionTranslateDuringProfiling()
    {
        var content = File.ReadAllText(AppPath);
        content.Should().Contain("EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE",
            "manual memory profiling should be able to disable mouse selection translate noise at runtime");
        content.Should().Contain("Mouse selection translate disabled by EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE",
            "startup logs should make the profiling override explicit");
    }

    [Fact]
    public void App_ApplyMouseSelectionTranslateRespectsProfilingOverride()
    {
        var content = File.ReadAllText(AppPath);
        content.Should().Contain("public static void ApplyMouseSelectionTranslate(bool enabled)",
            "the runtime apply path should remain the single toggle point for mouse selection translate");
        content.Should().Contain("ApplyMouseSelectionTranslate ignored due to EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE",
            "runtime toggles should stay suppressed while the profiling override is enabled");
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
