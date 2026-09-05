using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class WindowShowLatencyTests : IDisposable
{
    private readonly AppLauncher _launcher = new();
    private readonly ITestOutputHelper _output;

    public WindowShowLatencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SlowCapture_ShowsWithoutFocus_ThenAppliesSelection(bool fixedWindow, bool mainVisible)
    {
        Launch(delayMs: 3000, mainVisible);
        using var source = CreateTextSource("issue 202 selection");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SelectSourceText(source);
            var sourceHwnd = GetForegroundWindow();
            var timer = Stopwatch.StartNew();
            SendShowHotkey(fixedWindow);
            var hwnd = WaitForSecondaryWindow();
            var visibleMs = timer.ElapsedMilliseconds;
            visibleMs.Should().BeLessThan(2500, "the window must be visible before the simulated capture completes");
            GetForegroundWindow().Should().Be(sourceHwnd, "showing must preserve the source selection");
            Thread.Sleep(600); // Past Mini's normal 500 ms auto-close grace period.
            IsWindowVisible(hwnd).Should().BeTrue();
            GetForegroundWindow().Should().Be(sourceHwnd);
            WaitUntil(() => GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(8)).Should().BeTrue();
            var input = FindInput(hwnd);
            WaitUntil(() => input.Text == source.TextContent, TimeSpan.FromSeconds(5));
            input.Text.Should().Be(source.TextContent);
            _output.WriteLine($"{(fixedWindow ? "Fixed" : "Mini")} mainVisible={mainVisible} attempt={attempt}: visible={visibleMs}ms, ready={timer.ElapsedMilliseconds}ms (includes 3000ms test delay)");
            SendShowHotkey(fixedWindow);
            WaitUntil(() => !IsWindowVisible(hwnd), TimeSpan.FromSeconds(3)).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualInputDuringCapture_WinsOverLateSelection(bool fixedWindow)
    {
        Launch(delayMs: 3000, mainVisible: false);
        using var source = CreateTextSource("must not replace manual input");
        SelectSourceText(source);
        SendShowHotkey(fixedWindow);
        var hwnd = WaitForSecondaryWindow();
        var input = FindInput(hwnd);
        input.Click();
        // ValuePattern drives the same TextChanged path without depending on the user's IME.
        input.Text = "manual input wins";
        Thread.Sleep(3500);
        input.Text.Should().Be("manual input wins");
        IsWindowVisible(hwnd).Should().BeTrue();
        GetForegroundWindow().Should().Be(hwnd);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedHotkeyDuringCapture_HidesWithoutLateReopening(bool fixedWindow)
    {
        Launch(delayMs: 3000, mainVisible: false);
        using var source = CreateTextSource("pending selection");
        source.BringToForeground();
        SendShowHotkey(fixedWindow);
        var hwnd = WaitForSecondaryWindow();
        SendShowHotkey(fixedWindow);
        WaitUntil(() => !IsWindowVisible(hwnd), TimeSpan.FromSeconds(1)).Should().BeTrue();
        Thread.Sleep(3500);
        IsWindowVisible(hwnd).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseOpening_RecordsColdAndRepeatedVisibility(bool fixedWindow)
    {
        Launch(delayMs: 0, mainVisible: false);
        using var source = CreateTextSource("latency measurement");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            source.BringToForeground();
            var timer = Stopwatch.StartNew();
            SendShowHotkey(fixedWindow);
            var hwnd = WaitForSecondaryWindow();
            var visibleMs = timer.ElapsedMilliseconds;
            WaitUntil(() => GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(8)).Should().BeTrue();
            _output.WriteLine($"{(fixedWindow ? "Fixed" : "Mini")} attempt={attempt}: visible={visibleMs}ms, activated={timer.ElapsedMilliseconds}ms");
            SendShowHotkey(fixedWindow);
            WaitUntil(() => !IsWindowVisible(hwnd), TimeSpan.FromSeconds(3)).Should().BeTrue();
        }
    }

    private static NotepadTestTarget CreateTextSource(string text)
        => new(text, Environment.GetEnvironmentVariable("EASYDICT_UIA_TEXT_TARGET_EXE"));

    private static void SelectSourceText(NotepadTestTarget source)
    {
        source.BringToForeground();
        var editor = source.GetWindow().FindFirstDescendant(cf =>
            cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)
                .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Document)));
        editor.Should().NotBeNull();
        editor!.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(100);
        if (editor.Patterns.Text.IsSupported)
            string.Concat(editor.Patterns.Text.Pattern.GetSelection().Select(range => range.GetText(-1)))
                .TrimEnd('\r', '\n').Should().Be(source.TextContent, "the source must have the intended selection before invoking Easydict");
    }

    private nint _mainHwnd;

    private void Launch(int delayMs, bool mainVisible)
    {
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        _mainHwnd = _launcher.GetMainWindow().Properties.NativeWindowHandle.Value;
        SendMessageTimeout(_mainHwnd, 0x8000 + 202, delayMs, 0, 2, 5000, out var result)
            .Should().NotBe(0, "the app must respond to the test control message");
        result.Should().Be((nuint)1, "latency tests require an EasydictUiTestBuild=true app");
        if (!mainVisible) ShowWindow(_mainHwnd, 0);
    }

    private nint WaitForSecondaryWindow()
    {
        nint result = 0;
        WaitUntil(() =>
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == _launcher.Application.ProcessId && hwnd != _mainHwnd && IsWindowVisible(hwnd))
                {
                    result = hwnd;
                    return false;
                }
                return true;
            }, 0);
            return result != 0;
        }, TimeSpan.FromSeconds(5)).Should().BeTrue("a secondary window must become visible");
        return result;
    }

    private TextBox FindInput(nint hwnd)
        => _launcher.Automation.FromHandle(hwnd).FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox")).AsTextBox();

    private void SendShowHotkey(bool fixedWindow)
        // Exercise the real WM_HOTKEY handler without sending the shortcut to another
        // installed Easydict instance that may own the same global key combination.
        => PostMessage(_mainHwnd, 0x0312, fixedWindow ? 4 : 3, 0).Should().BeTrue();

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(
        nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
