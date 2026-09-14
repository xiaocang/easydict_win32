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
    public void SelectServiceIds_ExplicitUsableService_ComesFirstWithoutDuplicates()
    {
        var result = HoverLookupRules.SelectServiceIds("deepl", ["google", "deepl", "youdao", "GOOGLE"], _ => true, IsRegistered);

        result.Should().Equal("deepl", "youdao", "google");
    }

    [Fact]
    public void SelectServiceIds_ExplicitButUnusableOrUnknown_FallsBackToAuto()
    {
        HoverLookupRules.SelectServiceIds("deepl", ["google", "youdao"], id => id != "deepl", IsRegistered)
            .Should().Equal("youdao", "google");
        HoverLookupRules.SelectServiceIds("nonexistent", ["google"], _ => true, IsRegistered)
            .Should().Equal("google");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void SelectServiceIds_Auto_PrefersYoudao_ThenGoogleWeb(string? configured)
    {
        HoverLookupRules.SelectServiceIds(configured, ["google", "google_web", "youdao"], _ => true, IsRegistered)
            .Should().Equal("youdao", "google_web", "google");
        HoverLookupRules.SelectServiceIds(configured, ["google", "google_web"], _ => true, IsRegistered)
            .Should().Equal("google_web", "google");
    }

    [Fact]
    public void SelectServiceIds_Auto_ThenEnabledMdxDictionary_ThenFirstUsable()
    {
        HoverLookupRules.SelectServiceIds("", ["openai", "mdx::oxford", "google"], _ => true, IsRegistered)
            .Should().Equal("mdx::oxford", "openai", "google");
        HoverLookupRules.SelectServiceIds("", ["openai", "google"], _ => true, IsRegistered)
            .Should().Equal("openai", "google");
    }

    [Fact]
    public void SelectServiceIds_SkipsUnusableAndUnregisteredEntries()
    {
        HoverLookupRules.SelectServiceIds("", ["youdao", "openai", "google"], id => id != "youdao" && id != "openai", IsRegistered)
            .Should().Equal("google");
        HoverLookupRules.SelectServiceIds("", ["ghost", "google"], _ => true, IsRegistered)
            .Should().Equal("google");
    }

    [Fact]
    public void SelectServiceIds_NothingUsable_ReturnsEmpty()
    {
        HoverLookupRules.SelectServiceIds("", ["youdao", "google"], _ => false, IsRegistered).Should().BeEmpty();
        HoverLookupRules.SelectServiceIds("", [], _ => true, IsRegistered).Should().BeEmpty();
    }

    [Fact]
    public void SelectServiceIds_IsCaseInsensitiveForPreferredServices()
    {
        HoverLookupRules.SelectServiceIds("", ["Google", "YouDao"], _ => true, IsRegistered)
            .Should().Equal("YouDao", "Google");
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

    [Theory]
    [InlineData("Haus", Language.German, Language.English, Language.German)]
    [InlineData("maison", Language.French, Language.English, Language.French)]
    [InlineData("Haus", Language.SimplifiedChinese, Language.German, Language.German)]
    [InlineData("hello", Language.Russian, Language.English, Language.English)]
    [InlineData("привет", Language.Russian, Language.English, Language.Russian)]
    [InlineData("привіт", Language.Ukrainian, Language.English, Language.Ukrainian)]
    [InlineData("привет", Language.English, Language.Russian, Language.Russian)]
    [InlineData("مرحبا", Language.Arabic, Language.English, Language.Arabic)]
    [InlineData("سلام", Language.Persian, Language.English, Language.Persian)]
    [InlineData("سلام", Language.English, Language.Urdu, Language.Urdu)]
    [InlineData("漢字", Language.Japanese, Language.English, Language.Japanese)]
    [InlineData("繁體", Language.TraditionalChinese, Language.English, Language.TraditionalChinese)]
    [InlineData("こんにちは", Language.German, Language.English, Language.Japanese)]
    [InlineData("안녕하세요", Language.German, Language.English, Language.Korean)]
    [InlineData("hello", Language.Auto, Language.English, Language.English)]
    [InlineData("привет", Language.SimplifiedChinese, Language.English, Language.Russian)]
    [InlineData("مرحبا", Language.SimplifiedChinese, Language.English, Language.Arabic)]
    public void GuessSourceLanguage_PrefersCompatibleConfiguredLanguage(
        string word, Language first, Language second, Language expected)
    {
        HoverLookupRules.GuessSourceLanguage(word, first, second).Should().Be(expected);
    }
}
