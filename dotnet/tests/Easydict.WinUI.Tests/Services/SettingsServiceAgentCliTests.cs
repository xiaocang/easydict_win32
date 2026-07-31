using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class SettingsServiceAgentCliTests
{
    [Theory]
    [InlineData("luna", "gpt-5.6-luna")]
    [InlineData("terra", "gpt-5.6-terra")]
    [InlineData("sol", "gpt-5.6-sol")]
    [InlineData("gpt-5.6-luna", "gpt-5.6-luna")]
    public void NormalizeCodexModel_MigratesLegacyAliases(string model, string expected)
    {
        SettingsService.NormalizeCodexModel(model).Should().Be(expected);
    }
}
