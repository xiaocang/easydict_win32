using Easydict.WinUI.Views;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class SettingsPageMdxServiceIdTests
{
    [Fact]
    public void BuildMdxServiceId_ShouldNormalizeDisplayName()
    {
        var id = SettingsPage.BuildMdxServiceId(" Oxford! Advanced@Learner's  ", @"C:\\dicts\\oald.mdx");

        id.Should().StartWith("mdx::oxford-advanced-learner-s-");
    }

    [Fact]
    public void BuildMdxServiceId_ShouldFallbackToDictionaryWhenNameEmpty()
    {
        var id = SettingsPage.BuildMdxServiceId("   ", @"C:\\dicts\\a.mdx");

        id.Should().StartWith("mdx::dictionary-");
    }
}
