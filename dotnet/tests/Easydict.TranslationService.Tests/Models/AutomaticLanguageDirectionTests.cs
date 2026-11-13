using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Models;

public class AutomaticLanguageDirectionTests
{
    private static readonly AutomaticLanguageDirection EnglishFirst = new(Language.English, Language.Japanese);

    [Theory]
    [InlineData(Language.English, Language.Japanese)]
    [InlineData(Language.French, Language.English)]
    public void ResolveOutputLanguage_UsesDirectionRuleWhenSourceIsKnown(Language source, Language expected)
    {
        EnglishFirst.ResolveOutputLanguage("ignored", source).Should().Be(expected);
    }

    [Fact]
    public void ResolveOutputLanguage_DetectsScriptWhenSourceIsUnknown()
    {
        EnglishFirst.ResolveOutputLanguage("こんにちは").Should().Be(Language.Japanese);
        EnglishFirst.ResolveOutputLanguage("hello there").Should().Be(Language.English);
    }

    [Fact]
    public void ResolveOutputLanguage_LatinPairFallsBackToFirstLanguage()
    {
        // English and French share a script, so the direction rule's unknown-source branch applies.
        new AutomaticLanguageDirection(Language.English, Language.French)
            .ResolveOutputLanguage("bonjour tout le monde").Should().Be(Language.English);
    }

    [Fact]
    public void ResolveTargetLanguage_ReplacesAutoOnlyForAutomaticDirection()
    {
        var request = new TranslationRequest
        {
            Text = "bonjour", ToLanguage = Language.Auto,
            AutomaticDirection = EnglishFirst
        };
        var result = new TranslationResult
        {
            TranslatedText = "こんにちは", OriginalText = "bonjour",
            ServiceName = "test", DetectedLanguage = Language.French, TargetLanguage = Language.Auto
        };

        result.ResolveTargetLanguage(request).TargetLanguage.Should().Be(Language.English);

        var explicitRequest = new TranslationRequest { Text = "bonjour", ToLanguage = Language.Japanese };
        var explicitResult = result with { TargetLanguage = Language.Japanese };
        explicitResult.ResolveTargetLanguage(explicitRequest).TargetLanguage.Should().Be(Language.Japanese);
    }
}
