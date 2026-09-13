using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the pure hover lookup decision rules (service selection, language guess).
/// </summary>
public class HoverLookupRulesTests
{
    private static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase)
    {
        "google", "bing", "youdao", "google_web", "openai", "mdx::oxford", "deepl",
    };

    private static bool IsRegistered(string id) => Registered.Contains(id);

    [Fact]
    public void SelectServiceId_ExplicitUsableService_Wins()
    {
        var result = HoverLookupRules.SelectServiceId("deepl", ["google", "youdao"], _ => true, IsRegistered);

        result.Should().Be("deepl");
    }

    [Fact]
    public void SelectServiceId_ExplicitButUnusableOrUnknown_FallsBackToAuto()
    {
        HoverLookupRules.SelectServiceId("deepl", ["google", "youdao"], id => id != "deepl", IsRegistered)
            .Should().Be("youdao");
        HoverLookupRules.SelectServiceId("nonexistent", ["google"], _ => true, IsRegistered)
            .Should().Be("google");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void SelectServiceId_Auto_PrefersYoudao_ThenGoogleWeb(string? configured)
    {
        HoverLookupRules.SelectServiceId(configured, ["google", "google_web", "youdao"], _ => true, IsRegistered)
            .Should().Be("youdao");
        HoverLookupRules.SelectServiceId(configured, ["google", "google_web"], _ => true, IsRegistered)
            .Should().Be("google_web");
    }

    [Fact]
    public void SelectServiceId_Auto_ThenEnabledMdxDictionary_ThenFirstUsable()
    {
        HoverLookupRules.SelectServiceId("", ["openai", "mdx::oxford", "google"], _ => true, IsRegistered)
            .Should().Be("mdx::oxford");
        HoverLookupRules.SelectServiceId("", ["openai", "google"], _ => true, IsRegistered)
            .Should().Be("openai");
    }

    [Fact]
    public void SelectServiceId_SkipsUnusableAndUnregisteredEntries()
    {
        HoverLookupRules.SelectServiceId("", ["youdao", "openai", "google"], id => id != "youdao" && id != "openai", IsRegistered)
            .Should().Be("google");
        HoverLookupRules.SelectServiceId("", ["ghost", "google"], _ => true, IsRegistered)
            .Should().Be("google");
    }

    [Fact]
    public void SelectServiceId_NothingUsable_ReturnsNull()
    {
        HoverLookupRules.SelectServiceId("", ["youdao", "google"], _ => false, IsRegistered).Should().BeNull();
        HoverLookupRules.SelectServiceId("", [], _ => true, IsRegistered).Should().BeNull();
    }

    [Fact]
    public void SelectServiceId_IsCaseInsensitiveForPreferredServices()
    {
        HoverLookupRules.SelectServiceId("", ["Google", "YouDao"], _ => true, IsRegistered)
            .Should().Be("YouDao");
    }

    [Theory]
    [InlineData("hello", Language.English)]
    [InlineData("naïve", Language.English)]
    [InlineData("", Language.English)]
    [InlineData("你好", Language.SimplifiedChinese)]
    [InlineData("こんにちは", Language.Japanese)]
    [InlineData("漢字とかな", Language.Japanese)]
    [InlineData("안녕하세요", Language.Korean)]
    public void GuessSourceLanguage_UsesScript(string word, Language expected)
    {
        HoverLookupRules.GuessSourceLanguage(word).Should().Be(expected);
    }
}
