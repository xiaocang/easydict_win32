using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Source-level contract tests for task #9 (model-list refresh UX). They pin the shape of the flow after the
/// rework: fetching a model list fills the editor's own dropdown and never opens a "pick a model" dialog, and the
/// Magpie connect flow uses that same dropdown instead of a second picker.
///
/// These are source contracts, not runtime behaviour - the runtime proof lives in the UI automation tests
/// (`AiProfileModelRefreshTests`), which drive the real app.
/// </summary>
[Trait("Category", "WinUI")]
public class AiProfileModelRefreshCharacterizationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static readonly string SettingsPageCodeBehindPath =
        Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");

    private static string ReadAll(string path) => File.ReadAllText(Path.GetFullPath(path));

    /// <summary>
    /// Returns the source between two markers, failing loudly when a marker disappears: an empty or truncated
    /// region would silently turn the assertions into no-ops.
    /// </summary>
    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected to find '{startMarker}'");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"expected to find '{endMarker}' after '{startMarker}'");

        return source[start..end];
    }

    private static string RefreshModelsHandler() => ExtractRegion(
        ReadAll(SettingsPageCodeBehindPath),
        "private async void OnRefreshAiProfileModelsClick(",
        "private void UpdateAiProfileListLabelsAndDirtyState()");

    private static string ConnectMagpieHandler() => ExtractRegion(
        ReadAll(SettingsPageCodeBehindPath),
        "private async void OnConnectMagpieClick(",
        "private static string? GetMagpieBaseAddress(");

    /// <summary>
    /// Refreshing must fill the editor's own model dropdown and leave the choice there. Opening a separate
    /// "Select a model" dialog was the duplicated step reported in the issue.
    /// </summary>
    [Fact]
    public void AiProfileRefresh_FillsTheModelDropdownWithoutADialog()
    {
        var handler = RefreshModelsHandler();

        handler.Should().Contain("ReplaceEditableComboOptions(",
            "the fetched list is written straight into the editor's dropdown");
        handler.Should().NotContain("AiProfileModelPickerTitle",
            "no model-picker dialog may be opened from the refresh path");
        handler.Should().NotContain("new ContentDialog",
            "refresh reports through the inline status text instead of a dialog");
    }

    /// <summary>
    /// A saved model that the gateway no longer offers must stay visible and be called out, not silently
    /// cleared (Owner/ZenMaster default for a reversible, non-destructive behaviour).
    /// </summary>
    [Fact]
    public void AiProfileRefresh_KeepsASavedModelThatIsNoLongerListed()
    {
        var handler = RefreshModelsHandler();

        handler.Should().Contain("AiProfileMagpieModelRemoved",
            "the user is told in place that the saved model is no longer offered");
        handler.Should().NotContain("Configuration.Remove(\"model\")",
            "refresh must not delete the saved model");
    }

    /// <summary>
    /// Connecting Magpie fills the same dropdown; there is no second, independent model picker any more.
    /// </summary>
    [Fact]
    public void MagpieConnect_UsesTheSameDropdownInsteadOfItsOwnPicker()
    {
        var code = ReadAll(SettingsPageCodeBehindPath);

        ConnectMagpieHandler().Should().Contain("ReplaceEditableComboOptions(",
            "connect fills the editor's dropdown like refresh does");
        code.Should().NotContain("ShowMagpieModelPickerAsync",
            "the second picker implementation was removed");
        code.Should().NotContain("AiProfileModelPickerTitle",
            "no picker dialog title remains in the code-behind");
    }

    /// <summary>
    /// The model field is rendered as an editable combo box for providers that support model listing, which is
    /// why it can host a fetched list directly.
    /// </summary>
    [Fact]
    public void ModelField_IsRenderedAsAnEditableComboBox()
    {
        ReadAll(SettingsPageCodeBehindPath)
            .Should().Contain("IsEditable = field.Id == \"model\"",
                "the model field is an editable combo box and can host a fetched list");
    }

    /// <summary>
    /// The standalone Ollama section is hidden by design (Ollama is an AI profile now), so any claim that its
    /// inline refresh represents the shipped UI would be wrong. This pins that it stays hidden.
    /// </summary>
    [Fact]
    public void LegacyOllamaSection_StaysHiddenInFavourOfTheProfile()
    {
        ReadAll(SettingsPageCodeBehindPath)
            .Should().Contain("oldProviderTags",
                "the legacy provider sections are collapsed by the AI profile editor initialiser");
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "src", "Easydict.WinUI")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Could not locate the WinUI project root from the test output directory.");
    }
}
