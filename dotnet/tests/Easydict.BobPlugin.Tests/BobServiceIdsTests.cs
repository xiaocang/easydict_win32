using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests;

public class BobServiceIdsTests
{
    [Fact]
    public void Build_NamespacesThePlugin()
        => BobServiceIds.Build("com.example.x", "default").Should().Be("bob:com.example.x:default");

    [Fact]
    public void TryParse_RoundTripsBuild()
    {
        var id = BobServiceIds.Build("com.example.x", "a1b2c3");

        BobServiceIds.TryParse(id, out var identifier, out var instance).Should().BeTrue();
        identifier.Should().Be("com.example.x");
        instance.Should().Be("a1b2c3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("google")]
    [InlineData("mdx::abc")]
    [InlineData("bob:")]
    [InlineData("bob:onlyidentifier")]
    [InlineData("bob:trailing:")]
    public void TryParse_RejectsAnythingElse(string? serviceId)
        => BobServiceIds.TryParse(serviceId, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("bob:com.example.x:default", true)]
    [InlineData("google", false)]
    [InlineData(null, false)]
    public void IsPluginServiceId_MatchesThePrefix(string? serviceId, bool expected)
        => BobServiceIds.IsPluginServiceId(serviceId).Should().Be(expected);
}
