using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for BrowserSupportService — status detection helpers.
/// Most functionality (registry, process launch, file deploy) requires Windows
/// and is covered by integration / UI automation tests.
/// </summary>
public class BrowserSupportServiceTests
{
    [Fact]
    public void IsChromeSupportInstalled_ReturnsBool()
    {
        // Should not throw — just returns false when registry key is absent
        var result = BrowserSupportService.IsChromeSupportInstalled;
        result.Should().BeFalse();
    }

    [Fact]
    public void IsFirefoxSupportInstalled_ReturnsBool()
    {
        var result = BrowserSupportService.IsFirefoxSupportInstalled;
        result.Should().BeFalse();
    }
}
