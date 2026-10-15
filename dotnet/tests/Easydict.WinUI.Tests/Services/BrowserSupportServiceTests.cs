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
        // Reads real HKCU state, which other tests or a local install may change,
        // so only assert that the check completes without throwing.
        var act = () => BrowserSupportService.IsChromeSupportInstalled;
        act.Should().NotThrow();
    }

    [Fact]
    public void IsFirefoxSupportInstalled_ReturnsBool()
    {
        var act = () => BrowserSupportService.IsFirefoxSupportInstalled;
        act.Should().NotThrow();
    }
}
