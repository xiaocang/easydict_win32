using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// Lightweight scenario used by the PR memory gate.
/// The default path exercises launch, idle, main-window text selection, close,
/// and post-close idle without invoking real translation services.
/// </summary>
[Trait("Category", "UIAutomation")]
[Trait("Category", "MemoryGate")]
[Collection("UIAutomation")]
public sealed class MemoryGateTests : IDisposable
{
    private const string GateInputText = "Memory gate mock selection text";

    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public MemoryGateTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void PrMemoryGate_LightweightWindowAndSelectionScenario()
    {
        var initialIdle = ResolveDelaySeconds("EASYDICT_MEMORY_GATE_INITIAL_IDLE_SECONDS", 30);
        var postCloseIdle = ResolveDelaySeconds("EASYDICT_MEMORY_GATE_POST_CLOSE_IDLE_SECONDS", 15);
        var runRealTranslation = ResolveFlag("EASYDICT_MEMORY_GATE_RUN_TRANSLATION");

        var window = _launcher.GetMainWindow(TimeSpan.FromSeconds(60));
        window.Should().NotBeNull("main window must be available for the memory gate scenario");

        _output.WriteLine($"[MemoryGate] Initial idle: {initialIdle}s");
        Thread.Sleep(TimeSpan.FromSeconds(initialIdle));

        window.SetForeground();
        Thread.Sleep(500);

        var inputBox = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(15));
        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        inputBox!.Click();
        Thread.Sleep(250);
        inputBox.Text = GateInputText;
        Thread.Sleep(500);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(300);

        if (runRealTranslation)
        {
            _output.WriteLine("[MemoryGate] EASYDICT_MEMORY_GATE_RUN_TRANSLATION enabled; pressing Enter.");
            Keyboard.Type(VirtualKeyShort.ENTER);
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }

        _output.WriteLine("[MemoryGate] Closing main window");
        window.Close();

        _output.WriteLine($"[MemoryGate] Post-close idle: {postCloseIdle}s");
        WriteMarker("EASYDICT_MEMORY_GATE_CLOSED_MARKER_PATH");
        WaitForReleaseOrIdle(postCloseIdle);
    }

    private static int ResolveDelaySeconds(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(value, out var seconds))
        {
            return defaultValue;
        }

        return Math.Clamp(seconds, 0, 300);
    }

    private static bool ResolveFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMarker(string envName)
    {
        var path = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void WaitForReleaseOrIdle(int seconds)
    {
        var timeout = TimeSpan.FromSeconds(seconds);
        var releasePath = Environment.GetEnvironmentVariable("EASYDICT_MEMORY_GATE_RELEASE_MARKER_PATH");
        if (string.IsNullOrWhiteSpace(releasePath))
        {
            Thread.Sleep(timeout);
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(releasePath))
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
