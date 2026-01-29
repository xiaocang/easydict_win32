using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for TargetLanguageSelector.
/// Verifies that manual language selection persists until explicitly reset,
/// and that auto-selection is used only when appropriate.
/// </summary>
[Trait("Category", "WinUI")]
public class TargetLanguageSelectorTests : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LanguageDetectionService _detectionService;
    private readonly TargetLanguageSelector _selector;

    public TargetLanguageSelectorTests()
    {
        _settings = SettingsService.Instance;
        _detectionService = new LanguageDetectionService(_settings);
        _selector = new TargetLanguageSelector(_settings);
    }

    public void Dispose()
    {
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
        var result = _selector.ResolveTargetLanguage(Language.English, _detectionService);

        // Should return a non-null auto-selected language
        result.Should().NotBeNull();
        result.Should().NotBe(Language.English); // Should not translate to same language
    }

    // --- Manual selection persistence ---

    [Fact]
    public void MarkManualSelection_SetsFlag()
    {
        _selector.MarkManualSelection();

        _selector.IsManualSelection.Should().BeTrue();
    }

    [Fact]
    public void ResolveTargetLanguage_AfterManualSelection_ReturnsNull()
    {
        _selector.MarkManualSelection();

        var result = _selector.ResolveTargetLanguage(Language.English, _detectionService);

        // null means "use the current combo box value"
        result.Should().BeNull();
    }

    [Fact]
    public void ManualSelection_PersistsAcrossMultipleQueries()
    {
        // Simulate: user selects target language, then translates multiple texts
        _selector.MarkManualSelection();

        // Query 1
        var result1 = _selector.ResolveTargetLanguage(Language.English, _detectionService);
        result1.Should().BeNull("manual selection should persist");

        // Query 2 - different detected source
        var result2 = _selector.ResolveTargetLanguage(Language.SimplifiedChinese, _detectionService);
        result2.Should().BeNull("manual selection should persist across different texts");

        // Query 3 - yet another language
        var result3 = _selector.ResolveTargetLanguage(Language.Japanese, _detectionService);
        result3.Should().BeNull("manual selection should persist regardless of source language");

        _selector.IsManualSelection.Should().BeTrue("flag should never be reset by queries");
    }

    [Fact]
    public void ManualSelection_PersistsAfterSwap()
    {
        // Simulate: user clicks swap button (which calls MarkManualSelection)
        _selector.MarkManualSelection();

        // Subsequent queries should still use manual selection
        var result = _selector.ResolveTargetLanguage(Language.English, _detectionService);
        result.Should().BeNull("swap is a manual selection that should persist");
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

        var result = _selector.ResolveTargetLanguage(Language.English, _detectionService);

        // Should be back to auto-selection
        result.Should().NotBeNull("after reset, auto-selection should be active again");
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
    public void ResolveTargetLanguage_WhenAutoSelectDisabled_ReturnsNull()
    {
        // Save original and set auto-select to disabled
        var original = _settings.AutoSelectTargetLanguage;
        try
        {
            _settings.AutoSelectTargetLanguage = false;

            var result = _selector.ResolveTargetLanguage(Language.English, _detectionService);

            result.Should().BeNull("auto-select is disabled, should use current combo box value");
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

            // Even without manual selection, should return null when auto-select is off
            var result = _selector.ResolveTargetLanguage(Language.English, _detectionService);
            result.Should().BeNull();

            // With manual selection too
            _selector.MarkManualSelection();
            var result2 = _selector.ResolveTargetLanguage(Language.English, _detectionService);
            result2.Should().BeNull();
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
        var act = () => _selector.ResolveTargetLanguage(Language.English, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("detectionService");
    }

    // --- Full workflow simulation ---

    [Fact]
    public void FullWorkflow_InitAutoSelect_ManualOverride_PersistsUntilReset()
    {
        // Step 1: Initial state - auto-select is active
        var r1 = _selector.ResolveTargetLanguage(Language.English, _detectionService);
        r1.Should().NotBeNull("initially, auto-select should be active");

        // Step 2: User manually selects a language
        _selector.MarkManualSelection();

        // Step 3: Multiple queries - manual selection persists
        _selector.ResolveTargetLanguage(Language.English, _detectionService)
            .Should().BeNull("manual selection persists");
        _selector.ResolveTargetLanguage(Language.SimplifiedChinese, _detectionService)
            .Should().BeNull("manual selection persists across different texts");
        _selector.ResolveTargetLanguage(Language.Japanese, _detectionService)
            .Should().BeNull("manual selection persists across different texts");

        // Step 4: Window closes and reopens (reset)
        _selector.Reset();

        // Step 5: Back to auto-select
        var r5 = _selector.ResolveTargetLanguage(Language.English, _detectionService);
        r5.Should().NotBeNull("after reset, auto-select should be active again");
    }
}
