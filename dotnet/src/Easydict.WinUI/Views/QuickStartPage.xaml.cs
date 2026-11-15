using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Easydict.WinUI.Views;

public sealed partial class QuickStartPage : Page
{
    private readonly IOnboardingPracticeRunner _practiceRunner;
    private readonly OnboardingStateService _state = OnboardingStateService.Instance;
    private CancellationTokenSource? _practiceCancellation;
    private string _selection = string.Empty;
    private bool _practiceCompleted;
    private bool _invitationShownHere;
    private bool _mainWindowIsActive;
    private bool _eventsAttached;

    public QuickStartPage() : this(new MiniOnboardingPracticeRunner()) { }

    internal QuickStartPage(IOnboardingPracticeRunner practiceRunner)
    {
        _practiceRunner = practiceRunner;
        InitializeComponent();
        ApplyStrings();
        SelectionDemoOption.IsChecked = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnPageSizeChanged;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Give the scroller a real measure constraint. A MaxWidth-only parent can leave its content
        // arranged wider than the clipped viewport after the main window is maximized.
        if (e.NewSize.Width > 0) LayoutRoot.Width = Math.Min(880, e.NewSize.Width);
    }

    private static string T(string key, string fallback)
        => LocalizationService.Instance.GetStringOrDefault(key, fallback);

    private void ApplyStrings()
    {
        TitleText.Text = T("OnboardingTitle", "Get to know Easydict");
        SubtitleText.Text = T("OnboardingSubtitle", "See how it works, then try a real translation.");
        SelectionDemoOption.Content = T("OnboardingSelectionTab", "Translate selected text");
        OcrDemoOption.Content = T("OnboardingOcrTab", "Translate a screenshot");
        TryItButton.Content = T("OnboardingTryIt", "Try it yourself");
        PracticeTitleText.Text = T("OnboardingPracticeTitle", "Your turn");
        PracticeInstructionText.Text = T("OnboardingPracticeInstruction", "Select a few words below, then choose Translate selection. Your enabled translation services will show the real result in the Mini window.");
        PracticeText.Text = T("OnboardingPracticeSample", "A small step can open a whole new world. Select a phrase and discover what it means.");
        TranslateSelectionButton.Content = T("OnboardingTranslateSelection", "Translate selection");
        RetryButton.Content = T("OnboardingRetry", "Try again");
        ServicesButton.Content = T("OnboardingOpenServices", "Translation service settings");
        BackButton.Content = T("OnboardingBack", "Back to the demos");
        CompleteTitleText.Text = T("OnboardingCompleteTitle", "You got your first translation");
        CompleteMessageText.Text = T("OnboardingCompleteMessage", "You can keep reading and use Easydict whenever you need it.");
        SupportMessageText.Text = T("OnboardingSupportMessage", "Finding Easydict useful? You can give the project a Star on GitHub to support its development.");
        SupportButton.Content = T("OnboardingGitHubSupport", "Support Easydict on GitHub");
        TutorialSupportLink.Content = T("OnboardingGitHubSupport", "Support Easydict on GitHub");
        NotNowButton.Content = T("OnboardingNotNow", "Not now");
        StartUsingButton.Content = T("OnboardingStartUsing", "Start using Easydict");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _state.MarkShown();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_eventsAttached && App.MainWindow is { } window)
        {
            window.Activated += OnMainWindowActivated;
            _eventsAttached = true;
        }
        _mainWindowIsActive = true;
        TryShowSupportInvitation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPractice();
        DemoPlayer.Stop();
        if (_eventsAttached && App.MainWindow is { } window)
            window.Activated -= OnMainWindowActivated;
        _eventsAttached = false;
    }

    private void OnMainWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        _mainWindowIsActive = e.WindowActivationState != WindowActivationState.Deactivated;
        if (_mainWindowIsActive) TryShowSupportInvitation();
    }

    private void OnSelectionDemoChecked(object sender, RoutedEventArgs e)
    {
        if (DemoPlayer is not null) DemoPlayer.DemoKind = QuickStartDemoKind.SelectionTranslation;
    }

    private void OnOcrDemoChecked(object sender, RoutedEventArgs e)
    {
        if (DemoPlayer is not null) DemoPlayer.DemoKind = QuickStartDemoKind.ScreenshotTranslation;
    }

    private void OnTryItClick(object sender, RoutedEventArgs e)
    {
        DemoPlayer.Stop();
        DemoPanel.Visibility = Visibility.Collapsed;
        PracticePanel.Visibility = Visibility.Visible;
        PracticeText.Focus(FocusState.Programmatic);
    }

    private void OnPracticeSelectionChanged(object sender, RoutedEventArgs e)
    {
        // The global capture service intentionally excludes Easydict's own process. This control supplies its own
        // selection, leaving that protection intact and never using a synthetic clipboard selection.
        var selectedText = PracticeText.SelectedText?.Trim() ?? string.Empty;
        if (!string.Equals(_selection, selectedText, StringComparison.Ordinal) && _practiceCancellation is not null)
        {
            CancelPractice();
            SetBusy(false);
            PracticeStatusText.Text = string.Empty;
            RetryButton.Visibility = Visibility.Collapsed;
        }
        _selection = selectedText;
        if (TranslateSelectionButton is not null)
            TranslateSelectionButton.Visibility = string.IsNullOrWhiteSpace(_selection) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnTranslateSelectionClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selection))
        {
            PracticeStatusText.Text = T("OnboardingPracticeSelectText", "Select some text to translate first.");
            return;
        }

        var requestedSelection = _selection;
        CancelPractice();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _practiceCancellation = cancellation;
        var token = cancellation.Token;
        SetBusy(true);
        PracticeStatusText.Text = T("OnboardingTranslating", "Getting your translation in the Mini window…");
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _practiceRunner.TranslateSelectionAsync(requestedSelection, token);
            if (!IsLoaded || !ReferenceEquals(_practiceCancellation, cancellation) || token.IsCancellationRequested
                || !string.Equals(_selection, requestedSelection, StringComparison.Ordinal)) return;
            if (!result.HasTranslation)
            {
                ShowPracticeFailure();
                return;
            }

            _practiceCompleted = true;
            _state.MarkPracticeSucceeded();
            TranslationResultText.Text = result.TranslatedText;
            PracticePanel.Visibility = Visibility.Collapsed;
            CompletedPanel.Visibility = Visibility.Visible;
            TryShowSupportInvitation();
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded && ReferenceEquals(_practiceCancellation, cancellation)) ShowPracticeFailure();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Onboarding] Practice failed: {ex.GetType().Name}");
            if (IsLoaded && ReferenceEquals(_practiceCancellation, cancellation)) ShowPracticeFailure();
        }
        finally
        {
            if (ReferenceEquals(_practiceCancellation, cancellation))
            {
                _practiceCancellation = null;
                if (IsLoaded) SetBusy(false);
            }
            cancellation.Dispose();
        }
    }

    private void ShowPracticeFailure()
    {
        PracticeStatusText.Text = T("OnboardingPracticeFailed", "We couldn't get a translation. Try again or check your translation services.");
        RetryButton.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        PracticeProgress.IsActive = busy;
        PracticeProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TranslateSelectionButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
    }

    private void CancelPractice()
    {
        var old = _practiceCancellation;
        _practiceCancellation = null;
        old?.Cancel();
        // Its async handler owns disposal after callbacks have finished.
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        CancelPractice();
        SetBusy(false);
        PracticePanel.Visibility = Visibility.Collapsed;
        DemoPanel.Visibility = Visibility.Visible;
        TryItButton.Focus(FocusState.Programmatic);
    }

    private void OnServicesClick(object sender, RoutedEventArgs e)
    {
        CancelPractice();
        Frame.Navigate(typeof(SettingsPage), "services");
    }

    private void OnStartUsingClick(object sender, RoutedEventArgs e)
    {
        if (!_practiceCompleted) _state.MarkSkipped();
        CancelPractice();
        Frame.Navigate(typeof(MainPage));
        Frame.BackStack.Clear();
        Frame.ForwardStack.Clear();
    }

    private void TryShowSupportInvitation()
    {
        if (!_practiceCompleted || !_mainWindowIsActive || _invitationShownHere || !_state.SupportInvitationEligible) return;
        SupportInvitation.Visibility = Visibility.Visible;
        TutorialSupportLink.Visibility = Visibility.Collapsed;
        _state.MarkInvitationShown();
        _invitationShownHere = true;
    }

    private async void OnSupportClick(object sender, RoutedEventArgs e)
    {
        if (await GitHubSupportService.OpenAsync())
        {
            SupportInvitation.Visibility = Visibility.Collapsed;
            SupportErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SupportErrorText.Text = T("OnboardingSupportOpenFailed", "Couldn't open your browser. Visit github.com/xiaocang/easydict_win32 to support the project.");
            SupportErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnNotNowClick(object sender, RoutedEventArgs e)
    {
        _state.DismissInvitation();
        SupportInvitation.Visibility = Visibility.Collapsed;
    }
}
