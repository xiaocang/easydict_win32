using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FluentAssertions;
using Xunit;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class HoverLookupLayoutTests : IDisposable
{
    private readonly PerMonitorDpiScope _dpi = new();
    private readonly string? _previousSettings = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
    private readonly AppLauncher _launcher = new();
    private readonly nint _main;
    private nint _popupHandle;

    public HoverLookupLayoutTests()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Easydict.HoverLayout.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
            {
                UILanguage = "en-US", AppTheme = "Light",
                EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
                EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false,
                EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false,
                HoverWordLookupEnabled = false, MouseSelectionTranslate = false,
            }));
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
            _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            _main = _launcher.GetMainWindow().Properties.NativeWindowHandle.Value;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    [Fact]
    public void LongSourceWithShortResult_WrapsAndGrowsWithoutClipping()
    {
        var popup = Show(1);
        var shortHeight = Bounds().Height;
        var lineHeight = Find(popup, "HoverLookupWord").BoundingRectangle.Height;

        popup = Show(2);
        Bounds().Height.Should().BeGreaterThan(shortHeight);
        ((double)Find(popup, "HoverLookupWord").BoundingRectangle.Height).Should().BeGreaterThan(lineHeight * 1.5);
        AssertContained(popup, "HoverLookupWord");
        AssertContained(popup, "HoverLookupBody");
        AssertContained(popup, "HoverLookupService");
    }

    [Fact]
    public void TwelveLineResult_GrowsBeyondOldEightLineLimit_ThenShrinks()
    {
        var popup = Show(1);
        var compact = Bounds();
        var lineHeight = Find(popup, "HoverLookupBody").BoundingRectangle.Height;

        popup = Show(3);
        Find(popup, "HoverLookupBody").Name.Should().Contain("Definition 12");
        Find(popup, "HoverLookupBody").BoundingRectangle.Height.Should().BeGreaterThan(lineHeight * 10);
        Bounds().Height.Should().BeGreaterThan(compact.Height + lineHeight * 10);
        AssertContained(popup, "HoverLookupBody");
        AssertContained(popup, "HoverLookupService");

        popup = Show(1);
        ((double)Bounds().Height).Should().BeApproximately(compact.Height, 2);
        AssertContained(popup, "HoverLookupService");
    }

    [Fact]
    public void OversizedResult_TruncatesWithoutScrolling_ThenShrinks()
    {
        var popup = Show(1);
        var compactHeight = Bounds().Height;
        popup = Show(4);
        var area = WorkArea();
        var bounds = Bounds();
        bounds.Top.Should().BeGreaterThanOrEqualTo(area.Top);
        bounds.Bottom.Should().BeLessThanOrEqualTo(area.Bottom);
        ((double)bounds.Height).Should().BeApproximately(area.Height, 2);

        popup.FindAllDescendants().Where(element => element.Patterns.Scroll.IsSupported)
            .Should().BeEmpty("the hover popup must not expose any scrolling surface");
        Find(popup, "HoverLookupService").IsOffscreen.Should().BeTrue("overflow below the result is clipped");
        Find(popup, "HoverLookupBody").Name.Should().Contain("Definition 200");
        AssertContained(popup, "HoverLookupWord");

        // Exercise the production dismissal guard with the pointer inside and outside the card.
        bounds = Bounds();
        Mouse.Position = new Point((int)bounds.Left + 20, (int)bounds.Top + 20);
        Retry.WhileFalse(() => bounds.Contains(Mouse.Position), TimeSpan.FromSeconds(3)).Result.Should().BeTrue(
            $"the pointer must enter the popup before testing scroll dismissal (bounds={bounds}, pointer={Mouse.Position})");
        Send(204, 0).Should().Be((nuint)1);
        Retry.WhileFalse(() => !IsWindowVisible(_popupHandle), TimeSpan.FromSeconds(3)).Result.Should().BeTrue(
            "scrolling dismisses the popup instead of moving its content");

        popup = Show(1);
        ((double)Bounds().Height).Should().BeApproximately(compactHeight, 2);
        AssertContained(popup, "HoverLookupWord");
        AssertContained(popup, "HoverLookupBody");
        AssertContained(popup, "HoverLookupService");

        Mouse.Position = new Point(area.Left + 1, area.Top + 1);
        Retry.WhileFalse(() => !Bounds().Contains(Mouse.Position), TimeSpan.FromSeconds(3)).Result.Should().BeTrue(
            "the pointer must leave the popup before testing outside scroll dismissal");
        Send(204, 0).Should().Be((nuint)1);
        Retry.WhileFalse(() => !IsWindowVisible(_popupHandle), TimeSpan.FromSeconds(3)).Result.Should().BeTrue();
        IsWindowVisible(_popupHandle).Should().BeFalse();
    }

    [Fact]
    public void LoadingAndResultTransitions_SettleWithoutRetainingPreviousHeight()
    {
        Show(5);
        var loadingHeight = Bounds().Height;
        for (var i = 0; i < 3; i++)
        {
            Show(3);
            Bounds().Height.Should().BeGreaterThan(loadingHeight * 2);
            Show(5);
            ((double)Bounds().Height).Should().BeApproximately(loadingHeight, 2);
        }
    }

    private AutomationElement Show(int scenario)
    {
        Retry.WhileFalse(() => (_popupHandle = (nint)Send(203, scenario)) != 0,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)).Result.Should().BeTrue(
                "layout tests require an initialized EasydictUiTestBuild=true app");
        var popup = _launcher.Automation.FromHandle(_popupHandle);
        Find(popup, "HoverLookupWord");
        Rectangle previous = default;
        var stableSamples = 0;
        Retry.WhileFalse(() =>
        {
            var current = Bounds();
            stableSamples = current == previous ? stableSamples + 1 : 0;
            previous = current;
            return current.Height > 0 && stableSamples >= 5;
        }, TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100)).Result.Should().BeTrue("popup layout must settle");
        return popup;
    }

    private static AutomationElement Find(AutomationElement popup, string id) =>
        Retry.WhileNull(() => popup.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(5)).Result
        ?? throw new InvalidOperationException($"Missing popup element: {id}");

    private void AssertContained(AutomationElement popup, string id)
    {
        var element = Find(popup, id);
        Retry.WhileFalse(() =>
        {
            var outer = Bounds();
            var inner = element.BoundingRectangle;
            return !element.IsOffscreen && inner.Width > 0 && inner.Height > 0
                && inner.Top >= outer.Top && inner.Bottom <= outer.Bottom
                && inner.Left >= outer.Left && inner.Right <= outer.Right;
        }, TimeSpan.FromSeconds(3)).Result.Should().BeTrue(
            $"{id} must fit completely inside the popup (window={Bounds()}, element={element.BoundingRectangle}, offscreen={element.IsOffscreen}; " +
            $"{Find(popup, "HoverLookupBody").Properties.HelpText.Value})");
    }

    private Rectangle Bounds()
    {
        GetWindowRect(_popupHandle, out var rect).Should().BeTrue();
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private Rect WorkArea()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(_popupHandle, 2), ref info).Should().BeTrue();
        return info.Work;
    }

    private nuint Send(int message, int scenario)
    {
        SendMessageTimeout(_main, (uint)(0x8000 + message), scenario, 0, 2, 5000, out var result)
            .Should().NotBe(0, "the test app must respond");
        return result;
    }

    public void Dispose()
    {
        _launcher.Dispose();
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", _previousSettings);
        _dpi.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
