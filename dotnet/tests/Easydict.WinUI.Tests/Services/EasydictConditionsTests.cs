using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class EasydictConditionsTests
{
    [Fact]
    public void CanRateApp_MatchesCurrentBuildPolicy()
    {
#if DEBUG
        EasydictConditions.CanRateApp.Should().BeTrue();
#else
        EasydictConditions.CanRateApp.Should().Be(EasydictConditions.IsStoreBuild);
#endif
    }
}
