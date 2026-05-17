using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for quick-query language routing.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class TargetLanguageSelectorTests : IDisposable
{
    private readonly SettingsService _settings;
    private readonly TargetLanguageSelector _selector;
    private readonly string _originalFirstLanguage;
    private readonly string _originalSecondLanguage;
    private readonly List<string> _originalSelectedLanguages;

    public TargetLanguageSelectorTests()
    {
        _settings = SettingsService.Instance;
        _originalFirstLanguage = _settings.FirstLanguage;
        _originalSecondLanguage = _settings.SecondLanguage;
        _originalSelectedLanguages = [.. _settings.SelectedLanguages];

        _settings.FirstLanguage = "zh";
        _settings.SecondLanguage = "en";
        _settings.SelectedLanguages = ["zh", "en", "ja"];

        _selector = new TargetLanguageSelector(_settings);
    }

    public void Dispose()
    {
        _settings.FirstLanguage = _originalFirstLanguage;
        _settings.SecondLanguage = _originalSecondLanguage;
        _settings.SelectedLanguages = _originalSelectedLanguages;
    }

    [Fact]
    public void Constructor_WithValidSettings_CreatesInstance()
    {
        var selector = new TargetLanguageSelector(_settings);

        selector.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithNullSettings_ThrowsArgumentNullException()
    {
        var act = () => new TargetLanguageSelector(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("settings");
    }

    [Fact]
    public void IsManualSelection_InitiallyFalse()
    {
        _selector.IsManualSelection.Should().BeFalse();
    }

    [Fact]
    public void MarkManualSelection_SetsFlag()
    {
        _selector.MarkManualSelection();

        _selector.IsManualSelection.Should().BeTrue();
    }

    [Fact]
    public void Reset_ClearsManualSelection()
    {
        _selector.MarkManualSelection();

        _selector.Reset();

        _selector.IsManualSelection.Should().BeFalse();
    }

    [Fact]
    public void ResolveQueryLanguage_WhenTargetAutoAndSourceIsFirst_UsesSecondLanguage()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.SimplifiedChinese,
            Language.Auto,
            Language.SimplifiedChinese,
            grammarCorrectionAvailable: true);

        result.EffectiveMode.Should().Be(QueryMode.Translation);
        result.IsTargetAuto.Should().BeTrue();
        result.EffectiveTargetLanguage.Should().Be(Language.English);
        result.GrammarCorrectionRequested.Should().BeFalse();
    }

    [Fact]
    public void ResolveQueryLanguage_WhenTargetAutoAndSourceIsSecond_UsesFirstLanguage()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.English,
            Language.Auto,
            Language.English,
            grammarCorrectionAvailable: true);

        result.EffectiveMode.Should().Be(QueryMode.Translation);
        result.EffectiveTargetLanguage.Should().Be(Language.SimplifiedChinese);
    }

    [Fact]
    public void ResolveQueryLanguage_WhenBothAutoAndDetectedSourceIsFirst_UsesSecondLanguage()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.Auto,
            Language.Auto,
            Language.SimplifiedChinese,
            grammarCorrectionAvailable: true);

        result.EffectiveMode.Should().Be(QueryMode.Translation);
        result.EffectiveSourceLanguage.Should().Be(Language.SimplifiedChinese);
        result.EffectiveTargetLanguage.Should().Be(Language.English);
    }

    [Fact]
    public void ResolveQueryLanguage_WhenExplicitSameLanguageAndGrammarAvailable_UsesGrammarCorrection()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.English,
            Language.English,
            Language.English,
            grammarCorrectionAvailable: true);

        result.EffectiveMode.Should().Be(QueryMode.GrammarCorrection);
        result.EffectiveSourceLanguage.Should().Be(Language.English);
        result.EffectiveTargetLanguage.Should().Be(Language.English);
        result.GrammarCorrectionRequested.Should().BeTrue();
        result.GrammarCorrectionFallback.Should().BeFalse();
    }

    [Fact]
    public void ResolveQueryLanguage_WhenExplicitSameLanguageAndGrammarUnavailable_FallsBackToTranslation()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.English,
            Language.English,
            Language.English,
            grammarCorrectionAvailable: false);

        result.EffectiveMode.Should().Be(QueryMode.Translation);
        result.EffectiveTargetLanguage.Should().Be(Language.SimplifiedChinese);
        result.GrammarCorrectionRequested.Should().BeTrue();
        result.GrammarCorrectionFallback.Should().BeTrue();
    }

    [Fact]
    public void ResolveQueryLanguage_WhenExplicitDifferentLanguage_UsesTranslation()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.English,
            Language.Japanese,
            Language.English,
            grammarCorrectionAvailable: true);

        result.EffectiveMode.Should().Be(QueryMode.Translation);
        result.EffectiveTargetLanguage.Should().Be(Language.Japanese);
        result.GrammarCorrectionRequested.Should().BeFalse();
        result.GrammarCorrectionFallback.Should().BeFalse();
    }

    [Fact]
    public void ResolveQueryLanguage_WhenSourceDetectionUnknownAndTargetExplicit_DoesNotEnterGrammar()
    {
        var result = _selector.ResolveQueryLanguage(
            Language.Auto,
            Language.English,
            Language.Auto,
            grammarCorrectionAvailable: true);

        result.EffectiveMode.Should().Be(QueryMode.Translation);
        result.EffectiveTargetLanguage.Should().Be(Language.English);
        result.GrammarCorrectionRequested.Should().BeFalse();
    }

    [Fact]
    public void ResolveDifferentTargetLanguage_PrefersFirstSecondLanguages()
    {
        _selector.ResolveDifferentTargetLanguage(Language.SimplifiedChinese)
            .Should().Be(Language.English);

        _selector.ResolveDifferentTargetLanguage(Language.English)
            .Should().Be(Language.SimplifiedChinese);
    }
}
