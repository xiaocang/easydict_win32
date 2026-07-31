using Easydict.TranslationService;
using Easydict.WinUI.Views;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class SettingsPageAgentCliErrorTests
{
    [Fact]
    public void GetAgentCliErrorText_UpdateRequired_UsesLocalizedFormatter()
    {
        var exception = new TranslationException("English update message")
        {
            RecoveryAction = "install-latest-codex",
        };

        var result = SettingsPage.GetAgentCliErrorText(exception, _ => "请更新 Codex CLI");

        result.Should().Be("请更新 Codex CLI");
    }
}
