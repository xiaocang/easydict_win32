using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the unsaved changes confirmation dialog on the settings page.
/// Verifies localization resources, code-behind implementation, and consistency
/// across all supported languages.
/// </summary>
[Trait("Category", "Configuration")]
public class UnsavedChangesDialogTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string StringsPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Strings");
    private static readonly string SettingsPagePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");

    private static readonly string[] SupportedLanguages =
        { "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE" };

    /// <summary>
    /// Resource keys required for the unsaved changes dialog.
    /// </summary>
    private static readonly string[] RequiredDialogKeys =
        { "UnsavedChangesTitle", "UnsavedChangesMessage", "DontSave", "Cancel" };

    #region Resource Key Existence Tests

    [Theory]
    [InlineData("UnsavedChangesTitle")]
    [InlineData("UnsavedChangesMessage")]
    [InlineData("DontSave")]
    [InlineData("Cancel")]
    public void AllLanguages_HaveUnsavedChangesDialogKey(string key)
    {
        foreach (var lang in SupportedLanguages)
        {
            var reswPath = Path.Combine(StringsPath, lang, "Resources.resw");
            var doc = XDocument.Load(reswPath);
            var element = doc.Descendants("data")
                .FirstOrDefault(e => e.Attribute("name")?.Value == key);

            element.Should().NotBeNull(
                $"{lang}/Resources.resw should contain key '{key}'");
            element!.Element("value")?.Value.Should().NotBeNullOrWhiteSpace(
                $"{lang}/Resources.resw should have a non-empty value for '{key}'");
        }
    }

    [Fact]
    public void AllLanguages_HaveAllRequiredDialogKeys()
    {
        foreach (var lang in SupportedLanguages)
        {
            var reswPath = Path.Combine(StringsPath, lang, "Resources.resw");
            var doc = XDocument.Load(reswPath);
            var existingKeys = doc.Descendants("data")
                .Select(e => e.Attribute("name")?.Value)
                .Where(v => v != null)
                .ToHashSet();

            foreach (var key in RequiredDialogKeys)
            {
                existingKeys.Should().Contain(key,
                    $"{lang}/Resources.resw is missing required dialog key '{key}'");
            }
        }
    }

    #endregion

    #region Resource Value Verification Tests

    [Theory]
    [InlineData("en-US", "UnsavedChangesTitle", "Unsaved Changes")]
    [InlineData("en-US", "DontSave", "Don't Save")]
    [InlineData("en-US", "Cancel", "Cancel")]
    [InlineData("zh-CN", "UnsavedChangesTitle", "未保存的更改")]
    [InlineData("zh-CN", "DontSave", "不保存")]
    [InlineData("zh-CN", "Cancel", "取消")]
    [InlineData("ja-JP", "UnsavedChangesTitle", "未保存の変更")]
    [InlineData("ja-JP", "Cancel", "キャンセル")]
    public void ResourceFile_HasCorrectValue(string language, string key, string expectedValue)
    {
        var reswPath = Path.Combine(StringsPath, language, "Resources.resw");
        var doc = XDocument.Load(reswPath);
        var element = doc.Descendants("data")
            .FirstOrDefault(e => e.Attribute("name")?.Value == key);

        element.Should().NotBeNull(
            $"{language}/Resources.resw should contain key '{key}'");
        element!.Element("value")?.Value.Should().Be(expectedValue,
            $"{language}/Resources.resw should have correct value for '{key}'");
    }

    [Fact]
    public void DialogKeys_HaveUniqueValuesPerLanguage()
    {
        // Each language should have distinct translations (not all identical to en-US)
        var englishValues = GetDialogKeyValues("en-US");

        var nonEnglishLanguages = SupportedLanguages.Where(l => l != "en-US");
        foreach (var lang in nonEnglishLanguages)
        {
            var langValues = GetDialogKeyValues(lang);

            // At least one key should differ from English (i.e., it's actually translated)
            var allSameAsEnglish = RequiredDialogKeys
                .All(key => englishValues[key] == langValues[key]);

            allSameAsEnglish.Should().BeFalse(
                $"{lang} dialog strings should not all be identical to en-US (untranslated)");
        }
    }

    #endregion

    #region Code-Behind Implementation Tests

    [Fact]
    public void SettingsPage_HasUnsavedChangesFlag()
    {
        var content = File.ReadAllText(SettingsPagePath);

        content.Should().Contain("_hasUnsavedChanges",
            "SettingsPage should track unsaved changes state");
    }

    [Fact]
    public void SettingsPage_SetsUnsavedFlagOnChange()
    {
        var content = File.ReadAllText(SettingsPagePath);

        content.Should().Contain("_hasUnsavedChanges = true",
            "SettingsPage should set unsaved flag when settings change");
    }

    [Fact]
    public void SettingsPage_ResetsUnsavedFlagOnSave()
    {
        var content = File.ReadAllText(SettingsPagePath);

        content.Should().Contain("_hasUnsavedChanges = false",
            "SettingsPage should reset unsaved flag after saving");
    }

    [Fact]
    public void SettingsPage_HasSaveSettingsAsyncMethod()
    {
        var content = File.ReadAllText(SettingsPagePath);

        content.Should().Contain("Task<bool> SaveSettingsAsync()",
            "SettingsPage should have reusable SaveSettingsAsync method");
    }

    [Fact]
    public void SettingsPage_BackClickChecksUnsavedChanges()
    {
        var content = File.ReadAllText(SettingsPagePath);

        content.Should().Contain("OnBackClick",
            "SettingsPage should have OnBackClick handler");
        content.Should().Contain("if (_hasUnsavedChanges)",
            "OnBackClick should check for unsaved changes before navigating");
    }

    [Fact]
    public void SettingsPage_ShowsDialogWithThreeOptions()
    {
        var content = File.ReadAllText(SettingsPagePath);

        // The dialog should have Save (Primary), Don't Save (Secondary), Cancel (Close) buttons
        content.Should().Contain("PrimaryButtonText",
            "Dialog should have a primary button (Save)");
        content.Should().Contain("SecondaryButtonText",
            "Dialog should have a secondary button (Don't Save)");
        content.Should().Contain("CloseButtonText",
            "Dialog should have a close button (Cancel)");
    }

    [Fact]
    public void SettingsPage_UsesLocalizationForDialogStrings()
    {
        var content = File.ReadAllText(SettingsPagePath);

        content.Should().Contain("GetString(\"UnsavedChangesTitle\")",
            "Dialog title should use localization");
        content.Should().Contain("GetString(\"UnsavedChangesMessage\")",
            "Dialog message should use localization");
        content.Should().Contain("GetString(\"DontSave\")",
            "Don't Save button should use localization");
        content.Should().Contain("GetString(\"Cancel\")",
            "Cancel button should use localization");
    }

    [Fact]
    public void SettingsPage_HandlesAllDialogResults()
    {
        var content = File.ReadAllText(SettingsPagePath);

        // Primary = Save and go back
        content.Should().Contain("ContentDialogResult.Primary",
            "Should handle Primary result (Save)");
        // Secondary = Discard and go back
        content.Should().Contain("ContentDialogResult.Secondary",
            "Should handle Secondary result (Don't Save)");
    }

    [Fact]
    public void SettingsPage_SaveCallsGoBackOnSuccess()
    {
        var content = File.ReadAllText(SettingsPagePath);

        // When save is chosen from the dialog, it should call SaveSettingsAsync and navigate back
        content.Should().Contain("await SaveSettingsAsync()",
            "Save option should await SaveSettingsAsync");
        content.Should().Contain("Frame.GoBack()",
            "Should navigate back after save or discard");
    }

    [Fact]
    public void SettingsPage_OnSaveClickCallsSaveSettingsAsync()
    {
        var content = File.ReadAllText(SettingsPagePath);

        // The Save button handler should also use the refactored method
        content.Should().Contain("OnSaveClick",
            "Should have OnSaveClick handler");

        // Both OnSaveClick and OnBackClick should use SaveSettingsAsync
        var saveSettingsAsyncCount = content.Split("SaveSettingsAsync()").Length - 1;
        saveSettingsAsyncCount.Should().BeGreaterThanOrEqualTo(2,
            "SaveSettingsAsync should be called from both OnSaveClick and OnBackClick");
    }

    #endregion

    #region SaveSettings Button Integration Tests

    [Fact]
    public void SettingsPage_SaveButtonAlsoUsesLocalization()
    {
        var content = File.ReadAllText(SettingsPagePath);

        // The existing save button text key "SaveSettings" should be used in the dialog too
        content.Should().Contain("GetString(\"SaveSettings\")",
            "Dialog Primary button should reuse the SaveSettings localization key");
    }

    [Fact]
    public void AllLanguages_HaveSaveSettingsKey()
    {
        // The SaveSettings key is reused in the unsaved changes dialog as the Primary button text
        foreach (var lang in SupportedLanguages)
        {
            var reswPath = Path.Combine(StringsPath, lang, "Resources.resw");
            var doc = XDocument.Load(reswPath);
            var element = doc.Descendants("data")
                .FirstOrDefault(e => e.Attribute("name")?.Value == "SaveSettings");

            element.Should().NotBeNull(
                $"{lang}/Resources.resw should contain 'SaveSettings' key (used in dialog)");
        }
    }

    #endregion

    #region Helper Methods

    private Dictionary<string, string> GetDialogKeyValues(string language)
    {
        var reswPath = Path.Combine(StringsPath, language, "Resources.resw");
        var doc = XDocument.Load(reswPath);
        var values = new Dictionary<string, string>();

        foreach (var key in RequiredDialogKeys)
        {
            var element = doc.Descendants("data")
                .FirstOrDefault(e => e.Attribute("name")?.Value == key);
            values[key] = element?.Element("value")?.Value ?? "";
        }

        return values;
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

    #endregion
}
