using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HotkeySuggestionProviderTests
{
    [Fact]
    public void EmptyText_SuggestsModifiersAndFunctionKeys()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("");

        suggestions.Should().Contain(["Ctrl+", "Alt+", "Shift+", "Win+"]);
        suggestions.Should().Contain("F1");
        // Bare letters are not valid hotkeys, so they must not be offered.
        suggestions.Should().NotContain("A");
    }

    [Fact]
    public void PartialModifier_CompletesModifierWithTrailingPlus()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("Ct");

        suggestions.Should().ContainSingle().Which.Should().Be("Ctrl+");
    }

    [Fact]
    public void AfterModifier_SuggestsRemainingModifiersAndKeys()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("Ctrl+");

        suggestions.Should().Contain(["Ctrl+Alt+", "Ctrl+Shift+", "Ctrl+Win+"]);
        suggestions.Should().NotContain("Ctrl+Ctrl+");
        suggestions.Should().Contain("Ctrl+A");
    }

    [Fact]
    public void PartialKeyAfterModifiers_CompletesKey()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("Ctrl+Alt+Sp");

        suggestions.Should().ContainSingle().Which.Should().Be("Ctrl+Alt+Space");
    }

    [Fact]
    public void PartialTokenMatchingModifierAndKeys_SuggestsBoth()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("Ctrl+S");

        suggestions.Should().Contain("Ctrl+Shift+");
        suggestions.Should().Contain("Ctrl+S");
        suggestions.Should().Contain("Ctrl+Space");
    }

    [Fact]
    public void CompletedCombination_YieldsNoSuggestions()
    {
        // "T" is a completed key token; nothing can follow it.
        HotkeySuggestionProvider.GetSuggestions("Ctrl+Alt+T+").Should().BeEmpty();
    }

    [Fact]
    public void ModifierAliases_AreRecognizedAsAlreadyUsed()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("Control+");

        suggestions.Should().NotContain(s => s.Contains("Ctrl"));
        suggestions.Should().Contain("Control+Alt+");
    }

    [Fact]
    public void CaseInsensitiveMatching()
    {
        var suggestions = HotkeySuggestionProvider.GetSuggestions("ctrl+alt+sp");

        suggestions.Should().ContainSingle().Which.Should().Be("ctrl+alt+Space");
    }

    [Fact]
    public void SuggestionCount_IsCapped()
    {
        HotkeySuggestionProvider.GetSuggestions("Ctrl+").Should().HaveCountLessThanOrEqualTo(12);
    }

    [Fact]
    public void SuggestedKeyCompletions_AreValidCombinations()
    {
        foreach (var suggestion in HotkeySuggestionProvider.GetSuggestions("Ctrl+Alt+"))
        {
            if (!suggestion.EndsWith('+'))
            {
                HotkeyParser.IsValidCombination(suggestion).Should().BeTrue(
                    $"suggestion '{suggestion}' should be accepted by HotkeyParser");
            }
        }
    }
}

public class HotkeyParserIsValidCombinationTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T")]
    [InlineData("Win+Space")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("F8")]                  // bare function keys are allowed
    [InlineData("ctrl+alt+m")]          // case-insensitive
    public void ValidCombinations_AreAccepted(string hotkey)
    {
        HotkeyParser.IsValidCombination(hotkey).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                    // empty
    [InlineData(null)]
    [InlineData("T")]                   // bare letter would hijack typing
    [InlineData("Space")]               // bare named key
    [InlineData("Ctrl+Alt")]            // modifiers only, no key
    [InlineData("Ctrl+T+M")]            // two keys
    [InlineData("Ctrl+Altt+T")]         // typo in modifier
    public void InvalidCombinations_AreRejected(string? hotkey)
    {
        HotkeyParser.IsValidCombination(hotkey).Should().BeFalse();
    }
}
