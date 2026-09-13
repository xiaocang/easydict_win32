using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the raw mouse-move / keyboard events MouseHookService exposes to hover word lookup.
/// Drives the public Process* methods directly, without installing any Win32 hooks.
/// </summary>
[Trait("Category", "WinUI")]
public class MouseHookServiceEventTests
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint VK_CONTROL = 0x11;

    [Fact]
    public void ProcessMouseMessage_MouseMove_RaisesOnMouseMoveWithPoint()
    {
        using var hook = new MouseHookService();
        var received = new List<MouseHookService.POINT>();
        hook.OnMouseMove += pt => received.Add(pt);

        hook.ProcessMouseMessage(WM_MOUSEMOVE, new MouseHookService.POINT { x = 12, y = 34 });

        received.Should().ContainSingle();
        received[0].x.Should().Be(12);
        received[0].y.Should().Be(34);
    }

    [Fact]
    public void ProcessKeyboardMessage_KeyDownWithVk_RaisesOnKeyDownAndKeyboardEvent()
    {
        using var hook = new MouseHookService();
        var keyDownCount = 0;
        var events = new List<MouseHookService.KeyboardHookEvent>();
        hook.OnKeyDown += () => keyDownCount++;
        hook.OnKeyboardEvent += e => events.Add(e);

        hook.ProcessKeyboardMessage(WM_KEYDOWN, VK_CONTROL);
        hook.ProcessKeyboardMessage(WM_SYSKEYDOWN, 0x12);

        keyDownCount.Should().Be(2);
        events.Should().HaveCount(2);
        events[0].Should().Be(new MouseHookService.KeyboardHookEvent(true, VK_CONTROL));
        events[1].Should().Be(new MouseHookService.KeyboardHookEvent(true, 0x12));
    }

    [Fact]
    public void ProcessKeyboardMessage_KeyUp_RaisesKeyboardEventOnly()
    {
        using var hook = new MouseHookService();
        var keyDownCount = 0;
        var events = new List<MouseHookService.KeyboardHookEvent>();
        hook.OnKeyDown += () => keyDownCount++;
        hook.OnKeyboardEvent += e => events.Add(e);

        hook.ProcessKeyboardMessage(WM_KEYUP, VK_CONTROL);
        hook.ProcessKeyboardMessage(WM_SYSKEYUP, 0x12);

        keyDownCount.Should().Be(0);
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => !e.IsKeyDown);
        events[0].VkCode.Should().Be(VK_CONTROL);
    }

    [Fact]
    public void ProcessKeyboardMessage_LegacyOverload_StillRaisesOnKeyDown()
    {
        using var hook = new MouseHookService();
        var keyDownCount = 0;
        var events = new List<MouseHookService.KeyboardHookEvent>();
        hook.OnKeyDown += () => keyDownCount++;
        hook.OnKeyboardEvent += e => events.Add(e);

        hook.ProcessKeyboardMessage(WM_KEYDOWN);

        keyDownCount.Should().Be(1);
        events.Should().ContainSingle().Which.Should().Be(new MouseHookService.KeyboardHookEvent(true, 0));
    }

    [Fact]
    public void IsInstalled_IsFalse_WithoutInstall()
    {
        using var hook = new MouseHookService();
        hook.IsInstalled.Should().BeFalse();
    }

    [Fact]
    public void AddOwnedWindowHandle_IgnoresZeroAndDuplicates()
    {
        using var hook = new MouseHookService();

        // Must not throw; zero handles are ignored and duplicates collapse.
        hook.AddOwnedWindowHandle(IntPtr.Zero);
        hook.AddOwnedWindowHandle(new IntPtr(0x1234));
        hook.AddOwnedWindowHandle(new IntPtr(0x1234));
        hook.SetPopButtonWindowHandle(new IntPtr(0x5678));
    }
}
