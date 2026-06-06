using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Behavioral tests for <see cref="WinSpaceHotkeyHook.ProcessKey"/>, the pure
/// key-processing logic that captures Win+Space without installing a real hook.
/// </summary>
[Trait("Category", "WinUI")]
public class WinSpaceHotkeyHookTests
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint VK_SPACE = 0x20;
    private const uint VK_A = 0x41;

    private sealed class Counters
    {
        public int Fires;
        public int Masks;
    }

    private static (WinSpaceHotkeyHook hook, Counters counters) CreateHook(bool withHandler = true)
    {
        var counters = new Counters();
        var hook = new WinSpaceHotkeyHook(maskAction: () => counters.Masks++);
        if (withHandler)
        {
            hook.SetHandler(() => counters.Fires++);
        }
        return (hook, counters);
    }

    [Fact]
    public void SpaceDown_WithWinDown_FiresHandler_Masks_AndSuppresses()
    {
        var (hook, counters) = CreateHook();

        var suppressed = hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true);

        suppressed.Should().BeTrue();
        counters.Fires.Should().Be(1);
        counters.Masks.Should().Be(1, "the Win key must be disguised to keep the Start menu closed");
    }

    [Fact]
    public void SpaceDown_WithoutWin_PassesThrough_AndDoesNotFire()
    {
        var (hook, counters) = CreateHook();

        var suppressed = hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: false);

        suppressed.Should().BeFalse();
        counters.Fires.Should().Be(0);
        counters.Masks.Should().Be(0);
    }

    [Fact]
    public void SpaceUp_AfterConsumedDown_IsSuppressed_WithoutFiring()
    {
        var (hook, counters) = CreateHook();
        hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true); // consume

        var suppressed = hook.ProcessKey(WM_KEYUP, VK_SPACE, isWinDown: true);

        suppressed.Should().BeTrue("the trailing key-up must be swallowed so no stray space is typed");
        counters.Fires.Should().Be(1, "key-up must not re-fire the handler");
    }

    [Fact]
    public void AutoRepeat_FiresHandlerOnlyOnce_PerPress()
    {
        var (hook, counters) = CreateHook();

        hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true); // initial press
        hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true); // auto-repeat
        hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true); // auto-repeat

        counters.Fires.Should().Be(1, "auto-repeat key-downs while held must not re-fire");
        counters.Masks.Should().Be(1);
    }

    [Fact]
    public void SecondPress_AfterRelease_FiresAgain()
    {
        var (hook, counters) = CreateHook();

        hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true);
        hook.ProcessKey(WM_KEYUP, VK_SPACE, isWinDown: true);
        hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true);

        counters.Fires.Should().Be(2);
        counters.Masks.Should().Be(2);
    }

    [Fact]
    public void NonSpaceKey_IsIgnored()
    {
        var (hook, counters) = CreateHook();

        var suppressed = hook.ProcessKey(WM_KEYDOWN, VK_A, isWinDown: true);

        suppressed.Should().BeFalse();
        counters.Fires.Should().Be(0);
    }

    [Fact]
    public void SysKeyDown_WithWin_IsAlsoHandled()
    {
        var (hook, counters) = CreateHook();

        var suppressed = hook.ProcessKey(WM_SYSKEYDOWN, VK_SPACE, isWinDown: true);

        suppressed.Should().BeTrue();
        counters.Fires.Should().Be(1);
    }

    [Fact]
    public void NoHandler_StillSuppresses_AndMasks()
    {
        // A configured-but-handlerless hook (defensive) should still swallow Win+Space.
        var (hook, counters) = CreateHook(withHandler: false);

        var suppressed = hook.ProcessKey(WM_KEYDOWN, VK_SPACE, isWinDown: true);

        suppressed.Should().BeTrue();
        counters.Masks.Should().Be(1);
    }
}
