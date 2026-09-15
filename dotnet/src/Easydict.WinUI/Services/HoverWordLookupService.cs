using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

/// <summary>
/// Runtime options for hover word lookup (a snapshot of the related settings).
/// </summary>
public sealed record HoverLookupOptions(bool Enabled, HoverLookupModifier Modifier, bool UseOcrFallback, string ServiceId,
    int DelayMs = SettingsService.DefaultHoverWordLookupDelayMs)
{
    public static HoverLookupOptions Disabled { get; } = new(false, HoverLookupModifierExtensions.Default, true, string.Empty);

    public static HoverLookupOptions FromSettings(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HoverLookupOptions(
            settings.HoverWordLookupEnabled,
            HoverLookupModifierExtensions.Parse(settings.HoverWordLookupModifier),
            settings.HoverWordLookupUseOcrFallback,
            settings.HoverWordLookupServiceId ?? string.Empty,
            settings.HoverWordLookupDelayMs);
    }
}

/// <summary>
/// Orchestrates hover word lookup (悬浮取词): while the trigger key is held and the pointer
/// rests on a word, finds the word under the pointer (UIA, then OCR), tries translation services
/// in priority order and shows the first useful result in a small non-activating popup.
///
/// Threading: hook events and the dwell timer run on the UI thread and only do O(1) work;
/// word extraction and translation run on the thread pool; results come back through the
/// dispatcher and are checked against a generation counter so stale lookups never show.
/// </summary>
public sealed partial class HoverWordLookupService : IDisposable
{
    /// <summary>Dwell timer interval while the trigger condition holds.</summary>
    public const int TickIntervalMs = 50;

    /// <summary>Timeout for word extraction; translation has a separate timeout per service.</summary>
    public const int LookupTimeoutMs = 6000;

    /// <summary>Per-request translation timeout.</summary>
    public const int TranslationTimeoutMs = 5000;

    /// <summary>Margin (DIPs) around word + popup within which the pointer may move without dismissing.</summary>
    public const int SafeZoneMarginDips = 12;

    /// <summary>After a lookup found nothing, the pointer must move this far before trying again.</summary>
    public const int MissRetryDistancePx = 24;

    /// <summary>Moving this far away from the dwell point cancels a lookup that has not shown anything yet.</summary>
    public const int PendingLeaveDistancePx = 16;

    private const int ProcessNameCacheLimit = 64;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(MouseHookService.POINT pt);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MouseHookService _mouseHook;
    private readonly WordUnderCursorService _wordService;
    private readonly HoverDwellDetector _detector = new();
    private readonly HoverLookupKeyHold _keyHold = new();
    private readonly ConcurrentDictionary<uint, string?> _processNames = new();

    private DispatcherQueueTimer? _timer;
    private HoverLookupWindow? _window;
    private HoverLookupOptions _options = HoverLookupOptions.Disabled;
    private bool _blockedUntilModifierRelease;
    private bool _isDisposed;

    // Owned by StartLookup()/RunLookupAsync(): StartLookup creates it and RunLookupAsync disposes it
    // in its finally block. Other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _lookupCts;
    private int _generation;

    // Popup state — only touched on the UI thread.
    private string? _currentWord;
    private OcrRect _currentWordRect;
    private OcrRect _safeZone;
    private bool _hasActiveWord;
    private bool _hasLastMiss;
    private MouseHookService.POINT _lastMissPoint;
    private bool _hasPendingLookup;
    private MouseHookService.POINT _pendingLookupPoint;

    public HoverWordLookupService(
        DispatcherQueue dispatcherQueue,
        MouseHookService mouseHook,
        WordUnderCursorService wordService)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _mouseHook = mouseHook ?? throw new ArgumentNullException(nameof(mouseHook));
        _wordService = wordService ?? throw new ArgumentNullException(nameof(wordService));
    }

    /// <summary>Current options.</summary>
    public HoverLookupOptions Options => _options;

    /// <summary>Whether the feature is enabled.</summary>
    public bool IsEnabled => _options.Enabled;

    /// <summary>Whether the popup is currently visible.</summary>
    public bool IsPopupVisible => _window?.IsPopupVisible ?? false;

    /// <summary>
    /// Apply new options (UI thread). Disabling dismisses the popup and stops the dwell timer.
    /// </summary>
    public void ApplyOptions(HoverLookupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_isDisposed) return;

        _options = options;
        _detector.Reset();
        _keyHold.SetDown(false, Environment.TickCount64);
        _hasLastMiss = false;
        _blockedUntilModifierRelease = false;

        if (!options.Enabled)
        {
            Dismiss("Disabled");
            StopTimer();
            Debug.WriteLine("[HoverLookup] Disabled");
            return;
        }

        _keyHold.SetDown(options.Modifier != HoverLookupModifier.None && IsModifierPhysicallyHeld(), Environment.TickCount64);
        Debug.WriteLine($"[HoverLookup] Enabled: modifier={options.Modifier}, ocrFallback={options.UseOcrFallback}, service='{options.ServiceId}'");
    }

    /// <summary>
    /// Mouse move from the low-level hook (UI thread). Feeds the dwell detector and dismisses
    /// the popup once the pointer leaves the word/popup safe zone. Must stay cheap.
    /// </summary>
    public void OnMouseMove(MouseHookService.POINT pt)
    {
        if (_isDisposed || !_options.Enabled) return;

        _detector.OnMouseMove(pt, Environment.TickCount64);

        if (_hasActiveWord)
        {
            if (!_safeZone.Contains(pt.x, pt.y))
            {
                Dismiss("PointerLeft");
            }
        }
        else if (_hasPendingLookup &&
                 DistanceSquared(pt, _pendingLookupPoint) > (long)PendingLeaveDistancePx * PendingLeaveDistancePx)
        {
            // Nothing shown yet and the pointer already moved on: drop the in-flight lookup.
            Dismiss("PointerLeftPending");
        }

        // Start the timer even before the key-hold threshold, so a stationary pointer can fire later.
        if (_detector.IsArmed && IsTriggerActive())
        {
            EnsureTimerRunning();
        }
    }

    /// <summary>
    /// Keyboard event from the low-level hook (UI thread): tracks the trigger key and
    /// dismisses the popup on any other key press.
    /// </summary>
    public void OnKeyboardEvent(MouseHookService.KeyboardHookEvent e)
    {
        if (_isDisposed || !_options.Enabled) return;

        var modifier = _options.Modifier;
        if (modifier != HoverLookupModifier.None && modifier.MatchesVirtualKey(e.VkCode))
        {
            if (e.IsKeyDown)
            {
                if (!_keyHold.IsDown)
                {
                    _keyHold.SetDown(true, Environment.TickCount64);
                    _blockedUntilModifierRelease = false;
                    // A resting pointer may fire once the trigger key has been held long enough.
                    _detector.Rearm();
                    EnsureTimerRunning();
                }
            }
            else
            {
                _keyHold.SetDown(false, Environment.TickCount64);
                _blockedUntilModifierRelease = false;
                // The next press over the same word should look it up again.
                _detector.Rearm();
            }

            return;
        }

        if (!e.IsKeyDown || HoverLookupModifierExtensions.IsAnyModifierVirtualKey(e.VkCode))
        {
            return; // Other modifier keys (Shift, Win…) never dismiss.
        }

        // Any other key (Esc, typing, Ctrl+C…): hide the popup, and while the trigger key stays
        // held treat the combination as a shortcut rather than a lookup request.
        DismissForInput("KeyDown");
    }

    /// <summary>
    /// Dismiss because of a pointer/keyboard action (click, scroll, key press). While the trigger
    /// key is held, further lookups are blocked until it is released.
    /// </summary>
    public void DismissForInput(string reason)
    {
        if (_isDisposed) return;
        if (_keyHold.IsDown)
        {
            _blockedUntilModifierRelease = true;
        }

        Dismiss(reason);
    }

    /// <summary>
    /// Hide the popup and cancel any in-flight lookup.
    /// </summary>
    public void Dismiss(string reason)
    {
        Interlocked.Increment(ref _generation);
        CancelPendingLookup();

        var hadWord = _hasActiveWord;
        _hasActiveWord = false;
        _hasPendingLookup = false;
        _currentWord = null;
        _currentWordRect = default;
        _safeZone = default;

        if (_window?.IsPopupVisible == true)
        {
            _dispatcherQueue.TryEnqueue(() => _window?.HidePopup());
        }

        if (hadWord)
        {
            Debug.WriteLine($"[HoverLookup] Dismissed: {reason}");
        }
    }

    /// <summary>
    /// Apply theme to the popup window.
    /// </summary>
    public void ApplyTheme(ElementTheme theme, bool forceResourceRefresh = false)
    {
        _window?.ApplyTheme(theme, forceResourceRefresh);
    }

    // ---------------------------------------------------------------------
    // Dwell timer (UI thread)
    // ---------------------------------------------------------------------

    private bool IsTriggerActive()
    {
        return _options.Modifier == HoverLookupModifier.None
            || (_keyHold.IsDown && !_blockedUntilModifierRelease);
    }

    private bool IsTriggerSatisfied(long nowTicks) => IsTriggerActive() &&
        (_options.Modifier == HoverLookupModifier.None || _keyHold.IsSatisfied(nowTicks));

    private bool IsModifierPhysicallyHeld()
    {
        foreach (var vk in _options.Modifier.GetPollVirtualKeys())
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureTimerRunning()
    {
        if (_timer == null)
        {
            _timer = _dispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
            _timer.IsRepeating = true;
            _timer.Tick += OnTimerTick;
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_isDisposed || !_options.Enabled)
            {
                StopTimer();
                return;
            }

            if (_options.Modifier != HoverLookupModifier.None)
            {
                // Re-validate against the physical key state to recover from missed key-ups.
                var held = IsModifierPhysicallyHeld();
                if (_keyHold.IsDown && !held)
                {
                    _keyHold.SetDown(false, Environment.TickCount64);
                    _blockedUntilModifierRelease = false;
                    _detector.Rearm();
                }

                if (!held)
                {
                    StopTimer();
                    return;
                }
            }
            else if (!_detector.IsArmed)
            {
                StopTimer();
                return;
            }

            var nowTicks = Environment.TickCount64;
            var result = _detector.Tick(nowTicks, IsTriggerSatisfied(nowTicks), _options.DelayMs);
            if (result.Fired)
            {
                StartLookup(result.Point);
            }
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            CrashDiagnostics.LogException("HoverWordLookupService.OnTimerTick", ex, isTerminating: false, isHandled: true);
        }
    }

    // ---------------------------------------------------------------------
    // Lookup pipeline
    // ---------------------------------------------------------------------

    private void StartLookup(MouseHookService.POINT pt)
    {
        if (_isDisposed || !_options.Enabled) return;
        if (ScreenCaptureService.IsCaptureInProgress) return;
        if (_mouseHook.Detector.IsLeftButtonDown) return; // drag in progress

        if (_hasActiveWord && _currentWordRect.Contains(pt.x, pt.y))
        {
            return; // still on the word we already show
        }

        if (_hasLastMiss && DistanceSquared(pt, _lastMissPoint) < (long)MissRetryDistancePx * MissRetryDistancePx)
        {
            return; // nothing was found around here a moment ago
        }

        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return;
        if (GetWindowThreadProcessId(hwnd, out var processId) == 0) return;
        if (processId == (uint)Environment.ProcessId) return; // our own windows (popup, Mini, Main…)

        var cts = new CancellationTokenSource(LookupTimeoutMs);
        var previous = Interlocked.Exchange(ref _lookupCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }

        var generation = Interlocked.Increment(ref _generation);
        var options = _options;
        var excludeRect = _window?.IsPopupVisible == true ? _window.CurrentBounds : (OcrRect?)null;
        _hasPendingLookup = true;
        _pendingLookupPoint = pt;

        _ = Task.Run(() => RunLookupAsync(pt, processId, options, excludeRect, generation, cts));
    }

    private async Task RunLookupAsync(
        MouseHookService.POINT pt,
        uint processId,
        HoverLookupOptions options,
        OcrRect? excludeRect,
        int generation,
        CancellationTokenSource cts)
    {
        var ct = cts.Token;
        WordUnderCursor? word = null;
        try
        {
            var processName = GetProcessName(processId);
            if (SettingsService.Instance.IsMouseSelectionExcluded(processName))
            {
                Debug.WriteLine($"[HoverLookup] Excluded app '{processName}', skipping");
                return;
            }

            var dpiScale = DpiHelper.DpiToScaleFactor(DpiHelper.GetDpiForPoint(pt.x, pt.y));
            word = await _wordService.GetWordAtAsync(pt.x, pt.y, options.UseOcrFallback, excludeRect, dpiScale, ct);
            if (word is null)
            {
                _dispatcherQueue.TryEnqueue(() => RecordMissOnUiThread(pt, generation));
                return;
            }

            ct.ThrowIfCancellationRequested();
            cts.CancelAfter(Timeout.Infinite);
            Debug.WriteLine($"[HoverLookup] Word '{word.Text}' via {word.Source} at {word.ScreenRect}");

            var foundWord = word;
            _dispatcherQueue.TryEnqueue(() => ShowLoadingOnUiThread(foundWord, generation));

            using var handle = TranslationManagerService.Instance.AcquireHandle();
            var manager = handle.Manager;
            var serviceIds = ResolveServiceIds(options.ServiceId, manager);
            if (serviceIds.Count == 0)
            {
                Debug.WriteLine("[HoverLookup] No usable translation service enabled");
                _dispatcherQueue.TryEnqueue(() => ShowMessageOnUiThread(foundWord, "HoverLookupNoResult", generation));
                return;
            }

            var settings = SettingsService.Instance;
            var source = HoverLookupRules.GuessSourceLanguage(word.Text,
                LanguageExtensions.FromCode(settings.FirstLanguage),
                LanguageExtensions.FromCode(settings.SecondLanguage));
            var target = new TargetLanguageSelector(settings).ResolveAutoTargetLanguage(source);
            var request = new TranslationRequest
            {
                Text = word.Text,
                FromLanguage = Language.Auto,
                ToLanguage = target,
                TimeoutMs = TranslationTimeoutMs,
            };

            var result = await HoverLookupFallback.TranslateAsync(
                serviceIds,
                (serviceId, attemptToken) => manager.TranslateAsync(request, attemptToken, serviceId),
                TimeSpan.FromMilliseconds(TranslationTimeoutMs),
                ct);
            ct.ThrowIfCancellationRequested();

            if (result is null)
            {
                _dispatcherQueue.TryEnqueue(() => ShowMessageOnUiThread(foundWord, "HoverLookupNoResult", generation));
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                var content = HoverLookupContentBuilder.Build(
                    foundWord.Text,
                    result,
                    LocalizationService.Instance.GetString("HoverLookupNoResult"));
                ShowContentOnUiThread(foundWord, content, generation);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded, dismissed or timed out.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HoverLookup] Lookup failed: {ex.Message}");
            if (word is { } failedWord)
            {
                _dispatcherQueue.TryEnqueue(() => ShowMessageOnUiThread(failedWord, "HoverLookupError", generation));
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _lookupCts, null, cts);
            cts.Dispose();
        }
    }

    private static IReadOnlyList<string> ResolveServiceIds(string configured, TranslationManager manager)
    {
        var settings = SettingsService.Instance;
        var enabled = settings.MiniWindowEnabledServices
            .Concat(settings.MainWindowEnabledServices)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return HoverLookupRules.SelectServiceIds(
            configured,
            enabled,
            id => IsServiceUsable(manager, settings, id),
            id => manager.Services.ContainsKey(id));
    }

    private static bool IsServiceUsable(TranslationManager manager, SettingsService settings, string serviceId)
    {
        if (!manager.Services.TryGetValue(serviceId, out var service))
        {
            return false;
        }

        if (service.RequiresApiKey && !service.IsConfigured)
        {
            return false;
        }

        if (!settings.EnableInternationalServices && SettingsService.IsInternationalOnlyService(serviceId))
        {
            return false;
        }

        return true;
    }

    private string? GetProcessName(uint processId)
    {
        if (_processNames.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HoverLookup] Failed to resolve process {processId}: {ex.Message}");
        }

        if (_processNames.Count >= ProcessNameCacheLimit)
        {
            _processNames.Clear();
        }

        _processNames[processId] = name;
        return name;
    }

    private static long DistanceSquared(MouseHookService.POINT a, MouseHookService.POINT b)
    {
        long dx = a.x - b.x;
        long dy = a.y - b.y;
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------
    // Popup (UI thread)
    // ---------------------------------------------------------------------

    private bool IsCurrent(int generation) => !_isDisposed && _options.Enabled && generation == _generation;

    private void RecordMissOnUiThread(MouseHookService.POINT pt, int generation)
    {
        if (!IsCurrent(generation)) return;
        _hasPendingLookup = false;
        _hasLastMiss = true;
        _lastMissPoint = pt;
    }

    private void ShowLoadingOnUiThread(WordUnderCursor word, int generation)
    {
        if (!IsCurrent(generation)) return;

        EnsureWindowCreated();
        _currentWord = word.Text;
        _currentWordRect = word.ScreenRect;
        _hasActiveWord = true;
        _hasPendingLookup = false;
        _hasLastMiss = false;
        _window!.ShowLoading(word.Text, word.ScreenRect);
        UpdateSafeZone();
    }

    private void ShowContentOnUiThread(WordUnderCursor word, HoverLookupContent content, int generation)
    {
        if (!IsCurrent(generation)) return;

        EnsureWindowCreated();
        _currentWord = word.Text;
        _currentWordRect = word.ScreenRect;
        _hasActiveWord = true;
        _window!.ShowContent(content, word.ScreenRect);
        UpdateSafeZone();
    }

    private void ShowMessageOnUiThread(WordUnderCursor word, string messageKey, int generation)
    {
        if (!IsCurrent(generation)) return;

        EnsureWindowCreated();
        _currentWord = word.Text;
        _currentWordRect = word.ScreenRect;
        _hasActiveWord = true;
        _window!.ShowMessage(word.Text, LocalizationService.Instance.GetString(messageKey), word.ScreenRect);
        UpdateSafeZone();
    }

    private void UpdateSafeZone()
    {
        var anchorX = (int)Math.Round(_currentWordRect.X);
        var anchorY = (int)Math.Round(_currentWordRect.Y);
        var scale = DpiHelper.DpiToScaleFactor(DpiHelper.GetDpiForPoint(anchorX, anchorY));
        var popup = _window?.IsPopupVisible == true ? _window.CurrentBounds : (OcrRect?)null;
        _safeZone = HoverLookupPlacement.ComputeSafeZone(_currentWordRect, popup, SafeZoneMarginDips * scale);
    }

    private void EnsureWindowCreated()
    {
        if (_window != null) return;

        _window = new HoverLookupWindow();
        _window.OpenInMiniWindowRequested += OnOpenInMiniWindowRequested;
        _window.BoundsChanged += OnPopupBoundsChanged;

        // Clicks on the popup must not dismiss it before its button handler runs.
        _mouseHook.AddOwnedWindowHandle(_window.WindowHandle);

        _window.ApplyTheme(MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme));
        Debug.WriteLine("[HoverLookup] Popup window created");
    }

#if WINUI_TEST
    internal HoverLookupWindow GetWindowForLayoutTest()
    {
        EnsureWindowCreated();
        return _window!;
    }
#endif

    private void OnPopupBoundsChanged()
    {
        if (_hasActiveWord)
        {
            UpdateSafeZone();
        }
    }

    private void OnOpenInMiniWindowRequested()
    {
        var word = _currentWord;
        Dismiss("OpenInMiniWindow");
        if (string.IsNullOrWhiteSpace(word)) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            TextInsertionService.CaptureSourceWindow();
            MiniWindowService.Instance.ShowWithText(word, QuerySourceKind.Hover);
        });
    }

    private void CancelPendingLookup()
    {
        var cts = Interlocked.Exchange(ref _lookupCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        CancelPendingLookup();
        StopTimer();
        if (_timer != null)
        {
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        try
        {
            if (_window != null)
            {
                _window.OpenInMiniWindowRequested -= OnOpenInMiniWindowRequested;
                _window.BoundsChanged -= OnPopupBoundsChanged;
                _window.Close();
            }
        }
        catch
        {
            // Ignore close errors during shutdown
        }
        _window = null;

        Debug.WriteLine("[HoverLookup] Disposed");
    }
}
