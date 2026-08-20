using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HotkeyShowRequestTrackerTests
{
    [Fact]
    public void Begin_InvalidatesPreviousRequest()
    {
        var generation = 0;

        var firstRequest = HotkeyShowRequestTracker.Begin(ref generation);
        var secondRequest = HotkeyShowRequestTracker.Begin(ref generation);

        HotkeyShowRequestTracker.IsCurrent(ref generation, firstRequest).Should().BeFalse();
        HotkeyShowRequestTracker.IsCurrent(ref generation, secondRequest).Should().BeTrue();
    }
}
