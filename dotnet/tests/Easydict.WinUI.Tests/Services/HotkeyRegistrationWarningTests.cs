using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HotkeyRegistrationWarningTests
{
    [Fact]
    public void ShouldShowHotkeyRegistrationWarnings_IsFalseInDebugOrWinUiTestBuild()
    {
        EasydictConditions.ShouldShowHotkeyRegistrationWarnings.Should().BeFalse();
    }
}
