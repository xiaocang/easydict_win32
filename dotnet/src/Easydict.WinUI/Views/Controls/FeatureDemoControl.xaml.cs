using System.Globalization;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// Plays a local-only demonstration of selection or screenshot translation.
/// It never calls a translation or OCR service and never touches the clipboard.
/// </summary>
public sealed partial class FeatureDemoControl : UserControl
{
    private const int LastStep = 4;
    private static readonly TimeSpan DefaultPlaybackInterval = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _playbackTimer = new();
    private readonly KeyEventHandler _rootKeyDownHandler;
    private readonly PointerEventHandler _samplePointerPressedHandler;
    private readonly UISettings? _uiSettings;
    private Storyboard? _stepTransition;
    private bool _handlersAttached;
    private bool _isPlaying;
    private bool _isCompleted;
    private bool _playWhenLoaded;

    public static readonly DependencyProperty DemoKindProperty = DependencyProperty.Register(
        nameof(DemoKind),
        typeof(QuickStartDemoKind),
        typeof(FeatureDemoControl),
        new PropertyMetadata(QuickStartDemoKind.SelectionTranslation, OnDemoKindChanged));

    public FeatureDemoControl()
    {
        InitializeComponent();

        try
        {
            _uiSettings = new UISettings();
        }
        catch
        {
            // Reduced motion is the safe fallback when system animation settings are unavailable.
            _uiSettings = null;
        }

        _playbackTimer.Interval = DefaultPlaybackInterval;
        _rootKeyDownHandler = OnRootKeyDown;
        _samplePointerPressedHandler = OnSamplePointerPressed;
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
        SetLocalizedText();
        UpdateVisualState(animate: false);
        UpdatePlaybackControls();
    }

    /// <summary>The offline demonstration shown by this control.</summary>
    public QuickStartDemoKind DemoKind
    {
        get => (QuickStartDemoKind)GetValue(DemoKindProperty);
        set => SetValue(DemoKindProperty, value);
    }

    /// <summary>Zero-based step currently visible in the demonstration.</summary>
    public int CurrentStep { get; private set; }

    /// <summary>Whether timed playback is active.</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>Whether the final demonstration step has been reached.</summary>
    public bool IsCompleted => _isCompleted;

    /// <summary>
    /// Raised when the offline demonstration reaches its final step. This does not signal
    /// that the user completed a real translation.
    /// </summary>
    public event EventHandler? DemoCompleted;

#if WINUI_TEST
    internal bool? AnimationsEnabledForTest { get; set; }
    internal bool NativeWindowsEnabledForTest { get; set; } = true;
#endif

    /// <summary>Starts or resumes the four-transition demonstration.</summary>
    public void Play()
    {
        if (_isCompleted)
        {
            Replay();
            return;
        }

        if (_isPlaying)
        {
            return;
        }

        _isPlaying = true;
        _playWhenLoaded = !IsLoaded;
        if (IsLoaded && _handlersAttached && !_isPresentingNative)
        {
            _playbackTimer.Start();
        }

        UpdatePlaybackControls();
    }

    /// <summary>Pauses timed playback without changing the current step.</summary>
    public void Pause()
    {
        _playWhenLoaded = false;
        _isPlaying = false;
        _playbackTimer.Stop();
        UpdatePlaybackControls();
    }

    /// <summary>Advances one step. The final step raises <see cref="DemoCompleted"/> once.</summary>
    public void NextStep()
    {
        AdvanceToNextStep();
    }

    /// <summary>Returns to the first step and starts the demonstration again.</summary>
    public void Replay()
    {
        ResetToFirstStep(animate: false);
        Play();
    }

    /// <summary>Stops playback and returns to the first step.</summary>
    public void Stop()
    {
        ResetToFirstStep(animate: false);
    }

    private static void OnDemoKindChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FeatureDemoControl control)
        {
            return;
        }

        control.ResetToFirstStep(animate: false);
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (!_handlersAttached)
        {
            _playbackTimer.Tick += OnPlaybackTimerTick;
            // Intercept before the readonly sample can move its caret and invalidate the selection.
            RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, _rootKeyDownHandler, handledEventsToo: true);
            SelectionSampleText.AddHandler(UIElement.PointerPressedEvent, _samplePointerPressedHandler, handledEventsToo: true);
            PlayPauseButton.Click += OnPlayPauseClicked;
            NextStepButton.Click += OnNextStepClicked;
            ReplayButton.Click += OnReplayClicked;
            StopButton.Click += OnStopClicked;
            _handlersAttached = true;
        }

        _stepTransition = Resources.TryGetValue("DemoStepTransition", out var transition)
            ? transition as Storyboard
            : null;
        UpdateVisualState(animate: false);
        if (_playWhenLoaded && _isPlaying)
        {
            _playWhenLoaded = false;
            if (!_isPresentingNative) _playbackTimer.Start();
        }
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        Stop();
        _stepTransition?.Stop();
        DemoStage.Opacity = 1;

        if (!_handlersAttached)
        {
            return;
        }

        _playbackTimer.Tick -= OnPlaybackTimerTick;
        RootGrid.RemoveHandler(UIElement.PreviewKeyDownEvent, _rootKeyDownHandler);
        SelectionSampleText.RemoveHandler(UIElement.PointerPressedEvent, _samplePointerPressedHandler);
        PlayPauseButton.Click -= OnPlayPauseClicked;
        NextStepButton.Click -= OnNextStepClicked;
        ReplayButton.Click -= OnReplayClicked;
        StopButton.Click -= OnStopClicked;
        _handlersAttached = false;
    }

    private void OnPlaybackTimerTick(object? sender, object e)
    {
        AdvanceToNextStep();
    }

    private void AdvanceToNextStep()
    {
        if (_isCompleted || CurrentStep >= LastStep)
        {
            Pause();
            return;
        }

        CurrentStep++;
        if (CurrentStep == LastStep)
        {
            _isCompleted = true;
            Pause();
        }

        UpdateVisualState(animate: true);
        UpdatePlaybackControls();
        if (_isCompleted)
        {
            DemoCompleted?.Invoke(this, EventArgs.Empty);
        }
        else if (_isPlaying && !_isPresentingNative && IsLoaded && _handlersAttached)
        {
            _playbackTimer.Stop();
            _playbackTimer.Start();
        }
    }

    private void ResetToFirstStep(bool animate)
    {
        ResetNativeScene();
        _playbackTimer.Stop();
        _playWhenLoaded = false;
        _isPlaying = false;
        _isCompleted = false;
        CurrentStep = 0;
        UpdateVisualState(animate);
        UpdatePlaybackControls();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    private void OnNextStepClicked(object sender, RoutedEventArgs e) => NextStep();

    private void OnReplayClicked(object sender, RoutedEventArgs e) => Replay();

    private void OnStopClicked(object sender, RoutedEventArgs e) => Stop();

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A handled-events listener lets Right work even when a button owns focus. Space stays
        // with a focused button so its native Click cannot also toggle global playback.
        if (e.Key == VirtualKey.Space && IsSpaceOwnedByButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                if (_isPlaying) Pause(); else Play();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                NextStep();
                e.Handled = true;
                break;
            case VirtualKey.Home:
                Replay();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Stop();
                e.Handled = true;
                break;
        }
    }

    private bool IsSpaceOwnedByButton(DependencyObject? originalSource)
    {
        if (IsWithinButton(originalSource))
        {
            return true;
        }

        var focusedElement = RootGrid.XamlRoot is { } xamlRoot
            ? FocusManager.GetFocusedElement(xamlRoot) as DependencyObject
            : null;
        return IsWithinButton(focusedElement);
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void SetLocalizedText()
    {
        var play = GetText("OnboardingDemoPlay", "Play demo");
        var next = GetText("OnboardingDemoNextStep", "Next step");
        var replay = GetText("OnboardingDemoReplay", "Replay");
        var stop = GetText("OnboardingDemoStop", "Stop demo");

        PlayPauseText.Text = play;
        NextStepText.Text = next;
        ReplayText.Text = replay;
        StopText.Text = stop;
        SelectionSampleText.Text = GetText("OnboardingDemoSelectionSampleText", "A good idea can change everything.");
        ScreenshotSampleText.Text = GetText("OnboardingDemoScreenshotSampleText", "Small steps make a big difference.");
        OfflineNoticeText.Text = GetText("OnboardingDemoOfflineNotice", "The actual Easydict windows show fixed offline examples. No translation service is called.");

        AutomationProperties.SetName(PlayPauseButton, play);
        AutomationProperties.SetName(NextStepButton, next);
        AutomationProperties.SetName(ReplayButton, replay);
        AutomationProperties.SetName(StopButton, stop);
    }

    private void UpdatePlaybackControls()
    {
        var playPauseText = _isPlaying
            ? GetText("OnboardingDemoPause", "Pause demo")
            : GetText("OnboardingDemoPlay", "Play demo");
        PlayPauseText.Text = playPauseText;
        PlayPauseIcon.Glyph = _isPlaying ? "\uE769" : "\uE768";
        AutomationProperties.SetName(PlayPauseButton, playPauseText);
        NextStepButton.IsEnabled = !_isCompleted && CurrentStep < LastStep;
        StopButton.IsEnabled = _isPlaying || CurrentStep > 0 || _isCompleted;
        ReplayButton.IsEnabled = true;

        var stepFormat = GetText("OnboardingDemoStepCounter", "Step {0} of {1}");
        try
        {
            StepCounterText.Text = string.Format(
                CultureInfo.CurrentCulture,
                stepFormat,
                CurrentStep + 1,
                LastStep + 1);
        }
        catch (FormatException)
        {
            StepCounterText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "Step {0} of {1}",
                CurrentStep + 1,
                LastStep + 1);
        }

        StepProgress.Value = CurrentStep;
    }

    private void UpdateVisualState(bool animate)
    {
        var selectionDemo = DemoKind == QuickStartDemoKind.SelectionTranslation;
        SelectionDemoPanel.Visibility = selectionDemo ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotDemoPanel.Visibility = selectionDemo ? Visibility.Collapsed : Visibility.Visible;

        if (selectionDemo && CurrentStep is >= 1 and <= 3)
        {
            if (IsLoaded && CurrentStep == 1) SelectionSampleText.Focus(FocusState.Programmatic);
            SelectionSampleText.SelectAll();
        }
        else
        {
            SelectionSampleText.Select(0, 0);
        }
        StageHintText.Text = GetStageHint();
        RequestNativePresentation();

        if (animate && selectionDemo && IsLoaded && AnimationsEnabled)
        {
            try
            {
                _stepTransition ??= Resources.TryGetValue("DemoStepTransition", out var transition)
                    ? transition as Storyboard
                    : null;
                if (_stepTransition is not null)
                {
                    _stepTransition.Stop();
                    DemoStage.Opacity = 0.76;
                    _stepTransition.Begin();
                }
            }
            catch
            {
                DemoStage.Opacity = 1;
            }
        }
        else
        {
            _stepTransition?.Stop();
            DemoStage.Opacity = 1;
        }
    }

    private bool AnimationsEnabled
    {
        get
        {
#if WINUI_TEST
            if (AnimationsEnabledForTest is { } enabled)
            {
                return enabled;
            }
#endif
            try
            {
                return _uiSettings?.AnimationsEnabled ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string GetText(string key, string fallback)
    {
        try
        {
            return LocalizationService.Instance.GetStringOrDefault(key, fallback);
        }
        catch
        {
            return fallback;
        }
    }
}
