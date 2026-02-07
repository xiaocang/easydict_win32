using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for TargetLanguageSelector.
/// Verifies that manual language selection persists until explicitly reset,
/// that auto-selection is used only when appropriate, and that same-language
/// translation is always prevented via first↔second language reversal.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class TargetLanguageSelectorTests : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LanguageDetectionService _detectionService;
    private readonly TargetLanguageSelector _selector;
    private readonly string _originalFirstLanguage;
    private readonly string _originalSecondLanguage;

    public TargetLanguageSelectorTests()
    {
        _settings = SettingsService.Instance;
        _originalFirstLanguage = _settings.FirstLanguage;
        _originalSecondLanguage = _settings.SecondLanguage;
        _settings.FirstLanguage = "zh";
        _settings.SecondLanguage = "en";
        _detectionService = new LanguageDetectionService(_settings);
        _selector = new TargetLanguageSelector(_settings);
    }

    public void Dispose()
    {
        _settings.FirstLanguage = _originalFirstLanguage;
        _settings.SecondLanguage = _originalSecondLanguage;
        _detectionService.Dispose();
    }

    // --- Construction ---

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

    // --- Initial state ---

    [Fact]
    public void IsManualSelection_InitiallyFalse()
    {
        _selector.IsManualSelection.Should().BeFalse();
    }

    [Fact]
    public void ResolveTargetLanguage_Initially_ReturnsAutoSelected()
    {
        // With default settings (AutoSelectTargetLanguage=true), should auto-select
        var result = _selector.ResolveTargetLanguage(
            Language.English, Language.English, _detectionService);

        // Should not translate to same language
        result.Should().NotBe(Language.English);
    }

    // --- Manual selection persistence ---

    [Fact]
    public void MarkManualSelection_SetsFlag()
    {
        _selector.MarkManualSelection();

        _selector.IsManualSelection.Should().BeTrue();
    }

    [Fact]
    public void ResolveTargetLanguage_AfterManualSelection_UsesCurrentTarget()
    {
        _selector.MarkManualSelection();

        // Manual mode should use the provided currentTarget
        var result = _selector.ResolveTargetLanguage(
            Language.English, Language.Japanese, _detectionService);

        result.Should().Be(Language.Japanese);
    }

    [Fact]
    public void ManualSelection_PersistsAcrossMultipleQueries()
    {
        // Simulate: user selects target language, then translates multiple texts
        _selector.MarkManualSelection();

        // Query 1: manual target is Japanese
        var result1 = _selector.ResolveTargetLanguage(
            Language.English, Language.Japanese, _detectionService);
        result1.Should().Be(Language.Japanese, "manual selection should persist");

        // Query 2: different detected source, still manual target
        var result2 = _selector.ResolveTargetLanguage(
            Language.SimplifiedChinese, Language.Japanese, _detectionService);
        result2.Should().Be(Language.Japanese, "manual selection should persist across different texts");

        _selector.IsManualSelection.Should().BeTrue("flag should never be reset by queries");
    }

    [Fact]
    public void ManualSelection_PersistsAfterSwap()
    {
        // Simulate: user clicks swap button (which calls MarkManualSelection)
        _selector.MarkManualSelection();

        // Subsequent queries should still use manual selection
        var result = _selector.ResolveTargetLanguage(
            Language.English, Language.Korean, _detectionService);
        result.Should().Be(Language.Korean, "swap is a manual selection that should persist");
        _selector.IsManualSelection.Should().BeTrue();
    }

    // --- Reset behavior ---

    [Fact]
    public void Reset_ClearsManualSelection()
    {
        _selector.MarkManualSelection();
        _selector.IsManualSelection.Should().BeTrue();

        _selector.Reset();

        _selector.IsManualSelection.Should().BeFalse();
    }

    [Fact]
    public void ResolveTargetLanguage_AfterReset_ReturnsAutoSelected()
    {
        // Manual selection, then reset (simulates window close/reopen)
        _selector.MarkManualSelection();
        _selector.Reset();

        var result = _selector.ResolveTargetLanguage(
            Language.English, Language.English, _detectionService);

        // Should be back to auto-selection, not same as source
        result.Should().NotBe(Language.English, "after reset, auto-selection should be active again");
    }

    [Fact]
    public void Reset_CanBeCalledMultipleTimes()
    {
        var act = () =>
        {
            _selector.Reset();
            _selector.Reset();
            _selector.Reset();
        };

        act.Should().NotThrow();
        _selector.IsManualSelection.Should().BeFalse();
    }

    // --- Auto-select disabled ---

    [Fact]
    public void ResolveTargetLanguage_WhenAutoSelectDisabled_UsesCurrentTarget()
    {
        var original = _settings.AutoSelectTargetLanguage;
        try
        {
            _settings.AutoSelectTargetLanguage = false;

            var result = _selector.ResolveTargetLanguage(
                Language.English, Language.Japanese, _detectionService);

            result.Should().Be(Language.Japanese, "auto-select is disabled, should use current target");
        }
        finally
        {
            _settings.AutoSelectTargetLanguage = original;
        }
    }

    [Fact]
    public void ResolveTargetLanguage_WhenAutoSelectDisabled_ManualFlagIrrelevant()
    {
        var original = _settings.AutoSelectTargetLanguage;
        try
        {
            _settings.AutoSelectTargetLanguage = false;

            // Without manual selection
            var result = _selector.ResolveTargetLanguage(
                Language.English, Language.Korean, _detectionService);
            result.Should().Be(Language.Korean);

            // With manual selection too
            _selector.MarkManualSelection();
            var result2 = _selector.ResolveTargetLanguage(
                Language.English, Language.Korean, _detectionService);
            result2.Should().Be(Language.Korean);
        }
        finally
        {
            _settings.AutoSelectTargetLanguage = original;
        }
    }

    // --- Null argument validation ---

    [Fact]
    public void ResolveTargetLanguage_WithNullDetectionService_ThrowsArgumentNullException()
    {
        var act = () => _selector.ResolveTargetLanguage(
            Language.English, Language.Japanese, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("detectionService");
    }

    // --- Same-language reversal ---

    [Fact]
    public void ResolveTargetLanguage_ManualMode_SameAsSource_ReversesToSecondLanguage()
    {
        // Default: FirstLanguage=zh, SecondLanguage=en
        // Source is Chinese (first lang), manual target is also Chinese -> should reverse to English (second)
        _selector.MarkManualSelection();

        var result = _selector.ResolveTargetLanguage(
            Language.SimplifiedChinese, Language.SimplifiedChinese, _detectionService);

        result.Should().Be(Language.English, "source == first language, should reverse to second language");
    }

    [Fact]
    public void ResolveTargetLanguage_ManualMode_SameAsSource_ReversesToFirstLanguage()
    {
        // Source is English (second lang), manual target is also English -> should reverse to Chinese (first)
        _selector.MarkManualSelection();

        var result = _selector.ResolveTargetLanguage(
            Language.English, Language.English, _detectionService);

        result.Should().Be(Language.SimplifiedChinese, "source == second language, should reverse to first language");
    }

    [Fact]
    public void ResolveTargetLanguage_ManualMode_SameAsSource_NeitherFirstNorSecond_FallsBackToFirst()
    {
        // Source is Japanese (neither first nor second), manual target is also Japanese
        // -> should fall back to first language (Chinese)
        _selector.MarkManualSelection();

        var result = _selector.ResolveTargetLanguage(
            Language.Japanese, Language.Japanese, _detectionService);

        result.Should().Be(Language.SimplifiedChinese, "source is neither first nor second, should fall back to first language");
    }

    [Fact]
    public void ResolveTargetLanguage_ManualMode_DifferentFromSource_NoReversal()
    {
        // Source is English, manual target is Japanese -> different, no reversal needed
        _selector.MarkManualSelection();

        var result = _selector.ResolveTargetLanguage(
            Language.English, Language.Japanese, _detectionService);

        result.Should().Be(Language.Japanese, "source != target, no reversal needed");
    }

    [Fact]
    public void ResolveTargetLanguage_AutoSelectDisabled_SameAsSource_StillReverses()
    {
        // Even with auto-select disabled, same-language should be reversed
        var original = _settings.AutoSelectTargetLanguage;
        try
        {
            _settings.AutoSelectTargetLanguage = false;

            var result = _selector.ResolveTargetLanguage(
                Language.English, Language.English, _detectionService);

            result.Should().NotBe(Language.English, "same-language reversal should apply even when auto-select is disabled");
        }
        finally
        {
            _settings.AutoSelectTargetLanguage = original;
        }
    }

    [Fact]
    public void ResolveTargetLanguage_AutoMode_SourceIsAuto_NoReversal()
    {
        // When source is Auto (not yet detected), skip reversal logic
        var result = _selector.ResolveTargetLanguage(
            Language.Auto, Language.English, _detectionService);

        // Should just return auto-selected target without reversal
        // (Auto source cannot meaningfully be compared)
        result.Should().NotBe(Language.Auto);
    }

    // --- Full workflow simulation ---

    [Fact]
    public void FullWorkflow_InitAutoSelect_ManualOverride_PersistsUntilReset()
    {
        // Step 1: Initial state - auto-select is active
        var r1 = _selector.ResolveTargetLanguage(
            Language.English, Language.English, _detectionService);
        r1.Should().NotBe(Language.English, "initially, auto-select should be active and avoid same-language");

        // Step 2: User manually selects a language
        _selector.MarkManualSelection();

        // Step 3: Multiple queries - manual selection persists
        _selector.ResolveTargetLanguage(Language.English, Language.Japanese, _detectionService)
            .Should().Be(Language.Japanese, "manual selection persists");
        _selector.ResolveTargetLanguage(Language.SimplifiedChinese, Language.Japanese, _detectionService)
            .Should().Be(Language.Japanese, "manual selection persists across different texts");

        // Step 4: Window closes and reopens (reset)
        _selector.Reset();

        // Step 5: Back to auto-select
        var r5 = _selector.ResolveTargetLanguage(
            Language.English, Language.English, _detectionService);
        r5.Should().NotBe(Language.English, "after reset, auto-select should be active again");
    }

    [Fact]
    public void FullWorkflow_ManualSelection_SameLanguage_AutoReverses()
    {
        // User manually selects Chinese as target, then types Chinese text
        _selector.MarkManualSelection();

        // Source is Chinese, target is Chinese -> should auto-reverse to English
        var result = _selector.ResolveTargetLanguage(
            Language.SimplifiedChinese, Language.SimplifiedChinese, _detectionService);
        result.Should().Be(Language.English, "same-language should auto-reverse even in manual mode");

        // User then types English text with same manual Chinese target -> no reversal needed
        var result2 = _selector.ResolveTargetLanguage(
            Language.English, Language.SimplifiedChinese, _detectionService);
        result2.Should().Be(Language.SimplifiedChinese, "different languages, no reversal");
    }
}
