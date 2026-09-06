using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class SettingsPresentationComparisonTests
{
    [Fact]
    public void GroupedLanguagesAndMandatoryEnglishDoNotMarkThePageDirty()
    {
        SettingsPresentationComparison.LanguagesEqual(["ja", "zh", "en"], ["zh", "ja"]).Should().BeTrue();
        SettingsPresentationComparison.LanguagesEqual(["zh", "en"], ["zh", "ja", "en"]).Should().BeFalse();
    }

    [Fact]
    public void MissingQueryOverridesMeanAutomaticButManualChangesRemainDirty()
    {
        SettingsPresentationComparison.QueryModesEqual(new Dictionary<string, bool> { ["bing"] = true }, new Dictionary<string, bool>()).Should().BeTrue();
        SettingsPresentationComparison.QueryModesEqual(new Dictionary<string, bool> { ["bing"] = false }, new Dictionary<string, bool>()).Should().BeFalse();
        SettingsPresentationComparison.QueryModesEqual(new Dictionary<string, bool> { ["bing"] = true }, new Dictionary<string, bool> { ["bing"] = false }).Should().BeFalse();
        SettingsPresentationComparison.QueryModesEqual(new Dictionary<string, bool> { ["bing"] = true }, new Dictionary<string, bool> { ["inactive"] = false }).Should().BeTrue();
    }
}
