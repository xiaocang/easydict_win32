using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Source-level contracts for how phonetic badges reach the screen.
///
/// The rendering itself needs a WinUI dispatcher, so these validate stable source
/// contracts instead of driving the control, matching the approach used by the other
/// regression suites in this project. The selection logic they delegate to is covered
/// directly by the tests in Easydict.TranslationService.Tests.
///
/// Scope: these prove which size and selection the renderers read from source. They do not
/// prove what a pixel looks like on screen — that still needs a manual visual check.
/// </summary>
[Trait("Category", "WinUI")]
public class PhoneticRenderingContractTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static readonly string ServiceResultItemPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");

    private static readonly string ServiceResultItemXamlPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml");

    private static readonly string MinimalServiceResultItemPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml.cs");

    private static readonly string MinimalServiceResultItemXamlPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml");

    private static readonly string HoverLookupWindowPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "HoverLookupWindow.xaml.cs");

    private static readonly string AppPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "App.xaml.cs");

    private static readonly string SettingsPageXamlPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml");

    private static readonly string SettingsPageCodeBehindPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");

    [Fact]
    public void ServiceResultItem_DelegatesPhoneticSelectionToSharedHelper()
    {
        var content = File.ReadAllText(ServiceResultItemPath);

        content.Should().Contain("PhoneticDisplayHelper.GetDisplayPhonetics(result)",
            "the badge panel must render exactly what the shared helper selects, so the "
            + "display rule stays testable outside the UI");
        content.Should().Contain("PhoneticDisplayHelper.GetDisplayPhonetics(_serviceResult.Result)",
            "cross-service deduplication must report the same phonetics the panel renders");
    }

    [Fact]
    public void ServiceResultItem_DoesNotGatePhoneticsOnTargetLanguage()
    {
        // Regression for https://github.com/xiaocang/easydict_win32/issues/218: phonetics
        // were hidden whenever the target language was not English, which silently killed
        // the feature for en→zh — the most common dictionary lookup there is.
        var content = File.ReadAllText(ServiceResultItemPath);

        content.Should().NotContain("result.TargetLanguage != TranslationLanguage.English",
            "phonetics belong to the word being looked up, not to a translation direction");
    }

    [Fact]
    public void ServiceResultItem_SpeaksTheEnglishSideForUsUkBadges()
    {
        // A US/UK badge on an en→zh result describes the query, not the translation;
        // reading TranslatedText aloud would pronounce Chinese with an English voice.
        var content = File.ReadAllText(ServiceResultItemPath);

        content.Should().Contain("PhoneticDisplayHelper.GetEnglishPhoneticSubject(result)",
            "the speaker button must read the English side the badge describes");
    }

    [Fact]
    public void MinimalTheme_RendersNoPhoneticBadges()
    {
        // Deliberate and permanent product decision: the minimal theme trades dictionary
        // chrome for a bare result. This is not a gap to be filled later — if a future
        // change adds phonetics to the minimal renderer, this test should fail and the
        // decision should be revisited explicitly rather than drifting.
        var xaml = File.ReadAllText(MinimalServiceResultItemXamlPath);
        var content = File.ReadAllText(MinimalServiceResultItemPath);

        xaml.Should().NotContain("Phonetic",
            "the minimal theme intentionally has no phonetic panel");

        content.Should().Contain("GetDisplayedPhoneticKeys() => Array.Empty<string>()",
            "the minimal renderer must report no phonetics so the other renderer keeps them all");
        content.Should().NotContain("PhoneticDisplayHelper",
            "the minimal theme intentionally does not select phonetics for display");
    }

    /// <summary>
    /// Issue #249: the badge used to hardcode <c>FontSize = 10</c> in three places, which is
    /// what users reported as unreadable. Every part of the badge must now take the size the
    /// appearance settings compute.
    /// </summary>
    [Fact]
    public void ServiceResultItem_BadgesUseTheConfiguredPhoneticFontSize()
    {
        var content = File.ReadAllText(ServiceResultItemPath);
        var badgeFactory = ExtractRegion(
            content,
            "private Border CreatePhoneticBadge(",
            "private static string? GetAccentDisplayLabel(");

        badgeFactory.Should().NotContain("FontSize = 10",
            "the hardcoded 10 DIP phonetic size is the defect reported in issue #249");
        badgeFactory.Should().Contain("FontSize = phoneticFontSize",
            "the accent label, the phonetic text and the speaker icon must all follow the setting");
        badgeFactory.Should().Contain("var phoneticFontSize = _phoneticFontSize;");
        content.Should().Contain("private double _phoneticFontSize = AppearanceService.PhoneticFontSize;",
            "badges rendered before ApplyAppearance runs must already use the configured size");
    }

    /// <summary>
    /// Issue #249 regression: at the largest phonetic size a US+UK pair no longer fits the
    /// default 320-DIP result column, and the result scroll viewer has no horizontal scroll.
    /// The badge row must wrap, and each badge must let its transcription wrap instead of
    /// pushing the speaker button out of view.
    /// </summary>
    [Fact]
    public void ServiceResultItem_PhoneticBadgesWrapInNarrowWindows()
    {
        var xaml = File.ReadAllText(ServiceResultItemXamlPath);
        var badgeRow = ExtractRegion(xaml, "<!-- Phonetic Transcription Badges", "</controls:WrapPanel>");

        badgeRow.Should().Contain("<controls:WrapPanel",
            "a horizontal StackPanel clips instead of wrapping when the badges do not fit");
        badgeRow.Should().Contain("x:Name=\"PhoneticPanel\"",
            "the wrapping row must stay the element the code-behind populates");

        var content = File.ReadAllText(ServiceResultItemPath);
        var badgeFactory = ExtractRegion(
            content,
            "private Border CreatePhoneticBadge(",
            "private static string? GetAccentDisplayLabel(");

        badgeFactory.Should().Contain("TextWrapping = TextWrapping.Wrap",
            "long transcriptions must wrap inside the badge rather than overflow it");
    }

    /// <summary>
    /// The phonetic panel caches its render. If the font size is not part of that cache key,
    /// moving the settings slider leaves the already-rendered badges at the old size until the
    /// next query, which reads as "the setting does nothing".
    /// </summary>
    [Fact]
    public void ServiceResultItem_RebuildsPhoneticsWhenTheFontSizeChanges()
    {
        var content = File.ReadAllText(ServiceResultItemPath);

        content.Should().Contain("_phoneticsRenderedFontSize",
            "the phonetic render cache must remember which size it rendered at");
        content.Should().Contain("Math.Abs(_phoneticsRenderedFontSize - _phoneticFontSize) < 0.001",
            "a size change must invalidate the cached phonetic render");
        content.Should().Contain("ApplyPhoneticAppearance(settings.PhoneticFontSize)",
            "ApplyAppearance is the path a settings change takes into an existing result item");
    }

    /// <summary>
    /// The hover card renders phonetics in its own TextBlock, outside ServiceResultItem, so it
    /// needs the shared size applied explicitly (issue #249 asks for hover to be covered too).
    /// </summary>
    [Fact]
    public void HoverLookup_UsesTheConfiguredPhoneticFontSize()
    {
        var content = File.ReadAllText(HoverLookupWindowPath);

        content.Should().Contain("PhoneticText.FontSize = AppearanceService.PhoneticFontSize",
            "the hover card must read the same phonetic size as the dictionary results");
        content.Should().Contain("public void ApplyAppearance()");
    }

    [Fact]
    public void ApplyAppearance_FanOutReachesTheHoverPopup()
    {
        var content = File.ReadAllText(AppPath);

        content.Should().Contain("_hoverWordLookupService?.ApplyAppearance()",
            "without the fan-out, changing the setting never reaches an open hover card");
    }

    [Fact]
    public void SettingsPage_ExposesThePhoneticFontSizeSlider()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var code = File.ReadAllText(SettingsPageCodeBehindPath);

        xaml.Should().Contain("x:Name=\"PhoneticFontScaleSlider\"");
        xaml.Should().Contain("Minimum=\"1.0\"", "phonetics must not be adjustable back down to the unreadable size");
        code.Should().Contain("PhoneticFontScaleSlider.Value = Math.Clamp(",
            "loading clamps a value that predates the slider's range");
        code.Should().Contain("_settings.PhoneticFontScale = PhoneticFontScaleSlider.Value;");
        code.Should().Contain("loc.GetString(\"PhoneticFontSizeDescription\")");
    }

    /// <summary>
    /// Returns the source between two markers, failing loudly when a marker disappears: an empty
    /// or truncated region would silently turn the assertions below into no-ops.
    /// </summary>
    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected to find '{startMarker}'");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"expected to find '{endMarker}' after '{startMarker}'");

        return source[start..end];
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
