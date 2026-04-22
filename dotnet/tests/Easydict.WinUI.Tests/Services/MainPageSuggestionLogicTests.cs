using Easydict.WinUI.Views;
using FluentAssertions;
using Windows.System;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class MainPageSuggestionLogicTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string MainPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml");
    private static readonly string MainPageCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml.cs");

    [Fact]
    public void TryGetActiveSuggestionToken_ReturnsCurrentWordAtCaretEnd()
    {
        var success = MainPage.TryGetActiveSuggestionToken("this is app", "this is app".Length, out var token);

        success.Should().BeTrue();
        token.QueryText.Should().Be("app");
        token.StartIndex.Should().Be(8);
        token.Length.Should().Be(3);
    }

    [Fact]
    public void TryGetActiveSuggestionToken_StripsOuterPunctuation()
    {
        var success = MainPage.TryGetActiveSuggestionToken("(app)", "(app)".Length, out var token);

        success.Should().BeTrue();
        token.QueryText.Should().Be("app");
        token.StartIndex.Should().Be(1);
        token.Length.Should().Be(3);
    }

    [Fact]
    public void TryGetActiveSuggestionToken_RejectsTokenWithInternalDelimiter()
    {
        var success = MainPage.TryGetActiveSuggestionToken("app/test", "app/test".Length, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryGetActiveSuggestionToken_RejectsWildcardPatternToken()
    {
        var success = MainPage.TryGetActiveSuggestionToken("tea*", "tea*".Length, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void ReplaceSuggestionToken_ReplacesOnlyCurrentWord_AndReturnsCaretIndex()
    {
        MainPage.TryGetActiveSuggestionToken("this is app now", 11, out var token).Should().BeTrue();

        var replaced = MainPage.ReplaceSuggestionToken("this is app now", token, "apple", out var caretIndex);

        replaced.Should().Be("this is apple now");
        caretIndex.Should().Be(13);
    }

    [Fact]
    public void TryGetWildcardSuggestionToken_ExtractsWildcardWordAtCaret()
    {
        MainPage.TryGetWildcardSuggestionToken("  tea*  ", 6, out var token).Should().BeTrue();
        token.QueryText.Should().Be("tea*");
        token.StartIndex.Should().Be(2);
        token.Length.Should().Be(4);

        // Caret inside the wildcard word in a multi-word input — now supported
        MainPage.TryGetWildcardSuggestionToken("find tea*", "find tea*".Length, out var wildcardAfterFind).Should().BeTrue();
        wildcardAfterFind.QueryText.Should().Be("tea*");
        wildcardAfterFind.StartIndex.Should().Be(5);
        wildcardAfterFind.Length.Should().Be(4);

        MainPage.TryGetWildcardSuggestionToken("hello te*m", "hello te*m".Length, out var wildcardAtEnd).Should().BeTrue();
        wildcardAtEnd.QueryText.Should().Be("te*m");
        wildcardAtEnd.StartIndex.Should().Be(6);
        wildcardAtEnd.Length.Should().Be(4);

        MainPage.TryGetWildcardSuggestionToken("te*m hello", 4, out var wildcardBeforeSpace).Should().BeTrue();
        wildcardBeforeSpace.QueryText.Should().Be("te*m");
        wildcardBeforeSpace.StartIndex.Should().Be(0);
        wildcardBeforeSpace.Length.Should().Be(4);
    }

    [Fact]
    public void TryGetWildcardSuggestionToken_RejectsWordsWithoutWildcards()
    {
        // Caret inside a plain word in a multi-word input
        MainPage.TryGetWildcardSuggestionToken("hello te*m", 4, out _).Should().BeFalse();
        MainPage.TryGetWildcardSuggestionToken("te*m hello", "te*m hello".Length, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetWildcardSuggestionToken_RejectsPureWildcardTokens()
    {
        // Token with no literal characters — matches entire dictionary, intentionally rejected
        MainPage.TryGetWildcardSuggestionToken("hello *", "hello *".Length, out _).Should().BeFalse();
        MainPage.TryGetWildcardSuggestionToken("**", 2, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetWildcardSuggestionToken_RejectsTokenWithInternalDelimiter()
    {
        // Slash is neither whitespace nor a word/wildcard char — token extraction fails.
        MainPage.TryGetWildcardSuggestionToken("tea*/tray", 4, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(VirtualKey.Tab, true, false, true, false, false, (int)MainPage.SuggestionNavigationCommand.EnterNavigation)]
    [InlineData(VirtualKey.Down, true, false, true, false, false, (int)MainPage.SuggestionNavigationCommand.EnterNavigation)]
    [InlineData(VirtualKey.Down, true, true, true, true, false, (int)MainPage.SuggestionNavigationCommand.MoveNext)]
    [InlineData(VirtualKey.Up, true, true, true, true, false, (int)MainPage.SuggestionNavigationCommand.MovePrevious)]
    [InlineData(VirtualKey.Enter, true, true, true, true, false, (int)MainPage.SuggestionNavigationCommand.ApplySelection)]
    [InlineData(VirtualKey.Tab, true, true, true, true, true, (int)MainPage.SuggestionNavigationCommand.ExitNavigation)]
    [InlineData(VirtualKey.Escape, true, false, true, false, false, (int)MainPage.SuggestionNavigationCommand.HidePopup)]
    [InlineData(VirtualKey.Tab, false, false, true, false, false, (int)MainPage.SuggestionNavigationCommand.None)]
    public void ResolveSuggestionNavigationCommand_ReturnsExpectedAction(
        VirtualKey key,
        bool popupOpen,
        bool navigationActive,
        bool hasSuggestions,
        bool hasSelectedSuggestion,
        bool isShiftDown,
        int expected)
    {
        var command = MainPage.ResolveSuggestionNavigationCommand(
            key,
            popupOpen,
            navigationActive,
            hasSuggestions,
            hasSelectedSuggestion,
            isShiftDown);

        ((int)command).Should().Be(expected);
    }

    [Fact]
    public void GetLocalDictionarySuggestionsToggleState_DisablesWhenNoImportedDictionaryExists()
    {
        var disabledState = SettingsPage.GetLocalDictionarySuggestionsToggleState(0);
        var enabledState = SettingsPage.GetLocalDictionarySuggestionsToggleState(1);

        disabledState.IsEnabled.Should().BeFalse();
        disabledState.HintText.Should().Contain("Import a custom MDX dictionary");
        enabledState.IsEnabled.Should().BeTrue();
        enabledState.HintText.Should().BeEmpty();
    }

    [Fact]
    public void MainPageSuggestionList_DisablesImplicitFocusInXaml()
    {
        var xaml = File.ReadAllText(MainPageXamlPath);

        xaml.Should().Contain("AllowFocusOnInteraction=\"False\"",
            "the suggestion popup should not steal keyboard interaction when it opens");
        xaml.Should().Contain("IsTabStop=\"False\"",
            "the suggestion list should stay out of the normal tab order until MainPage opts into navigation mode");
    }

    [Fact]
    public void MainPageSuggestionPopup_UsesCodeBehindFocusGuards()
    {
        var source = File.ReadAllText(MainPageCodeBehindPath);

        source.Should().Contain("SuggestionPopup.Opened += OnSuggestionPopupOpened;");
        source.Should().Contain("SuggestionListView.GettingFocus += OnSuggestionListViewGettingFocus;");
        source.Should().Contain("if (!IsInputTextBoxFocused())");
        source.Should().Contain("QueueRestoreInputFocusFromSuggestionPopup();");
        source.Should().Contain("args.Cancel = true;");
    }

    [Fact]
    public void ApplySuggestionAsync_DoesNotTriggerQueryImmediately()
    {
        var source = File.ReadAllText(MainPageCodeBehindPath);
        var applySuggestionStart = source.IndexOf("private Task ApplySuggestionAsync(SuggestionItem suggestion)", StringComparison.Ordinal);

        applySuggestionStart.Should().BeGreaterThanOrEqualTo(0);

        var showSuggestionsStart = source.IndexOf("private void ShowSuggestions", applySuggestionStart, StringComparison.Ordinal);
        showSuggestionsStart.Should().BeGreaterThan(applySuggestionStart);

        var applySuggestionBlock = source[applySuggestionStart..showSuggestionsStart];
        applySuggestionBlock.Should().NotContain("StartQueryTrackedAsync",
            "accepting a suggestion should only commit text; querying should wait for the next Enter");
        applySuggestionBlock.Should().Contain("QueueRestoreInputFocusFromSuggestionPopup();");
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
