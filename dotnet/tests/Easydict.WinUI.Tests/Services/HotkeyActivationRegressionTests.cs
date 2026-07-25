using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Regression checks for issue #188: a global hotkey re-showed the settings page the
/// main window had been left on, instead of the translation UI, and silently dropped
/// the selected text.
///
/// The activation path lives in <c>App.xaml.cs</c> and depends on a live XAML Frame,
/// so — as with <see cref="KanbanTodoUxRegressionTests"/> — these validate stable
/// source-level contracts rather than driving the UI.
/// </summary>
[Trait("Category", "WinUI")]
public class HotkeyActivationRegressionTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string AppPath =
        Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "App.xaml.cs");
    private static readonly string SettingsPageCodePath =
        Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");

    [Fact]
    public void App_TranslationActivations_ReturnFrameToMainPage()
    {
        var appCode = File.ReadAllText(AppPath);

        appCode.Should().Contain("private MainPage? EnsureMainPageForQuery()",
            "translation-intent activations need a single place that leaves the settings page first");
        appCode.Should().Contain("if (frame.Content is SettingsPage settingsPage)",
            "the helper must recognize a frame parked on the settings page");
        appCode.Should().Contain("settingsPage.TryReturnToMainPage();",
            "leaving the settings page must go through the guarded entry point, not a raw Navigate");
    }

    [Fact]
    public void App_ShowWindowHotkey_LeavesSettingsPageBeforeRaisingWindow()
    {
        var appCode = File.ReadAllText(AppPath);

        var showHotkeyIndex = appCode.IndexOf("private void OnShowWindowHotkey()", StringComparison.Ordinal);
        showHotkeyIndex.Should().BeGreaterOrEqualTo(0, "the show-window hotkey handler should exist");

        var handlerEnd = appCode.IndexOf("private async void OnTranslateSelectionHotkey()", showHotkeyIndex, StringComparison.Ordinal);
        handlerEnd.Should().BeGreaterThan(showHotkeyIndex, "the translate-selection handler should follow the show-window handler");

        var handler = appCode[showHotkeyIndex..handlerEnd];
        handler.Should().Contain("EnsureMainPageForQuery();",
            "the show-window hotkey focuses the query input, so it must not surface the settings page");
        handler.Should().Contain("FocusMainWindowInputForTyping();",
            "the show-window hotkey should still request input focus after raising the window");
    }

    [Fact]
    public void App_SelectionAndClipboardQueries_ShareGuardedDelivery()
    {
        var appCode = File.ReadAllText(AppPath);

        appCode.Should().Contain("private void ShowQueryInMainWindow(string? text)",
            "selection and clipboard translation should share one delivery path");

        var selectionHotkey = ExtractSection(appCode,
            "private async void OnTranslateSelectionHotkey()",
            "private void ShowQueryInMainWindow(string? text)");
        var trayClipboard = ExtractSection(appCode,
            "private async void OnTrayTranslateClipboard()",
            "private async void OnTrayOcrTranslate()");

        foreach (var trigger in new[] { selectionHotkey, trayClipboard })
        {
            trigger.Should().Contain("_window?.DispatcherQueue.TryEnqueue(() => ShowQueryInMainWindow(text));",
                "hotkey and tray query triggers should both route through the shared delivery path");
            trigger.Should().NotContain("frame.Content is MainPage mainPage",
                "a query must no longer be discarded when the frame happens to show another page");
        }

        var delivery = ExtractSection(appCode,
            "private void ShowQueryInMainWindow(string? text)",
            "private async void OnShowMiniWindowHotkey()");
        delivery.Should().Contain("MiniWindowService.Instance.ShowWithText(text);",
            "a query that cannot reach MainPage should fall back to the mini window instead of being dropped");
    }

    [Fact]
    public void SettingsPage_TryReturnToMainPage_IsGuardedByUnsavedChanges()
    {
        var settingsCode = File.ReadAllText(SettingsPageCodePath);

        settingsCode.Should().Contain("internal bool TryReturnToMainPage()",
            "App needs a guarded entry point to leave the settings page on behalf of a hotkey");
        settingsCode.Should().Contain("private bool NavigateBackToMainPage()",
            "the back button and the hotkey path should share one navigation implementation");

        var tryReturnIndex = settingsCode.IndexOf("internal bool TryReturnToMainPage()", StringComparison.Ordinal);
        var unsavedGuardIndex = settingsCode.IndexOf("if (_hasUnsavedChanges)", tryReturnIndex, StringComparison.Ordinal);
        var navigateIndex = settingsCode.IndexOf("return NavigateBackToMainPage();", tryReturnIndex, StringComparison.Ordinal);

        unsavedGuardIndex.Should().BeGreaterThan(tryReturnIndex,
            "a background trigger must never discard settings the user is still typing");
        navigateIndex.Should().BeGreaterThan(unsavedGuardIndex,
            "the unsaved-changes guard must run before the navigation");
    }

    /// <summary>
    /// Returns the source text between two markers so an assertion applies to a single
    /// method instead of the whole file.
    /// </summary>
    private static string ExtractSection(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterOrEqualTo(0, $"'{startMarker}' should exist in App.xaml.cs");

        var end = content.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"'{endMarker}' should follow '{startMarker}' in App.xaml.cs");

        return content[start..end];
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
