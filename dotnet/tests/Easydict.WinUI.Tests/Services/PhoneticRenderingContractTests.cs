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
/// </summary>
[Trait("Category", "WinUI")]
public class PhoneticRenderingContractTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static readonly string ServiceResultItemPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");

    private static readonly string MinimalServiceResultItemPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml.cs");

    private static readonly string MinimalServiceResultItemXamlPath = Path.Combine(
        ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml");

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
