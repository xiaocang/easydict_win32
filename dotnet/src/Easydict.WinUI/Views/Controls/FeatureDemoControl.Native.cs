using System.Runtime.InteropServices;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.ScreenCapture;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Views.Controls;

public sealed partial class FeatureDemoControl
{
    private readonly SemaphoreSlim _nativePresentationLock = new(1, 1);
    private CancellationTokenSource? _nativeLifetime;
    private OnboardingWindowPreviewService? _previewWindows;
    private ScreenCaptureDemoSession? _capturePreview;
    private Task _captureCleanup = Task.CompletedTask;
    private int _presentationVersion;
    private bool _miniPreviewShowing;
    private bool _isPresentingNative;

    private string GetStageHint()
    {
        var prefix = DemoKind == QuickStartDemoKind.SelectionTranslation ? "Selection" : "Screenshot";
        string[] fallback = DemoKind == QuickStartDemoKind.SelectionTranslation
            ? ["Play the example to see what happens when you select text.", "Select the text you want to translate.",
               "Click the Easydict icon beside the selection.", "The translation appears in a separate Mini window.",
               "Keep reading, or choose Try it yourself for a real translation."]
            : ["Open screenshot translation from the button at the top of Mini. Playback briefly opens the full-screen overlay; no image is saved or sent.",
               "The screenshot tool covers the desktop so you can choose a region.",
               "Drag around the text. Space pauses, Right advances, and Esc stops the preview.",
               "After confirming the region, the recognized text is placed in Mini.", "The translation appears in the same Mini window."];
        return GetText($"OnboardingDemo{prefix}Step{CurrentStep}", fallback[CurrentStep]);
    }

    private void OnSamplePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // SelectionChanged also arrives asynchronously for programmatic SelectAll/Focus.
        // Only an actual pointer interaction interrupts the scripted reading example.
        if (CurrentStep > 0) Stop();
    }

    private void ResetNativeScene()
    {
        _presentationVersion++;
        _isPresentingNative = false;
        _nativeLifetime?.Cancel();
        _nativeLifetime?.Dispose();
        _nativeLifetime = null;
        _miniPreviewShowing = false;
        _previewWindows?.Dispose();
        _previewWindows = null;
        var capture = _capturePreview;
        _capturePreview = null;
        if (capture is not null)
        {
            capture.NavigationRequested -= OnCaptureNavigationRequested;
            capture.Cancel();
            _captureCleanup = DisposeCaptureAsync(capture);
        }
    }

    private static async Task DisposeCaptureAsync(ScreenCaptureDemoSession capture)
    {
        try { await capture.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Onboarding capture cleanup: {ex.Message}"); }
    }

    private async void RequestNativePresentation()
    {
        if (!IsLoaded) return;
#if WINUI_TEST
        if (!NativeWindowsEnabledForTest) return;
#endif
        var version = ++_presentationVersion;
        _isPresentingNative = true;
        var step = CurrentStep;
        var kind = DemoKind;
        _nativeLifetime ??= new CancellationTokenSource();
        var token = _nativeLifetime.Token;
        _playbackTimer.Stop();
        await _nativePresentationLock.WaitAsync();
        try
        {
            await _captureCleanup;
            token.ThrowIfCancellationRequested();
            if (version != _presentationVersion || !IsLoaded) return;
            _previewWindows ??= new OnboardingWindowPreviewService();
            if (kind == QuickStartDemoKind.SelectionTranslation)
            {
                if (step == 2)
                {
                    var bounds = GetScreenBounds(SelectionSampleText);
                    var end = SelectionSampleText.GetRectFromCharacterIndex(Math.Max(0, SelectionSampleText.Text.Length - 1), true);
                    var scale = XamlRoot.RasterizationScale;
                    var x = bounds.X + (int)Math.Round(end.Right * scale);
                    var y = bounds.Y + (int)Math.Round((end.Bottom + 8) * scale);
                    _previewWindows.ShowPopButton(x, y, () =>
                    {
                        if (!token.IsCancellationRequested && version == _presentationVersion && CurrentStep == 2) NextStep();
                    });
                }
                else if (step == 3)
                {
                    _previewWindows.ShowMini(SelectionSampleText.Text,
                        GetText("OnboardingDemoSelectionTranslation", "好主意可以改变一切。"));
                    _miniPreviewShowing = true;
                }
                else
                {
                    _previewWindows.HideAll();
                    _miniPreviewShowing = false;
                }
            }
            else if (step is 1 or 2)
            {
                if (_capturePreview is null)
                {
                    var capture = await ScreenCaptureDemoSession.ShowAsync(token);
                    if (token.IsCancellationRequested)
                    {
                        await capture.DisposeAsync();
                        token.ThrowIfCancellationRequested();
                    }
                    _capturePreview = capture;
                    capture.NavigationRequested += OnCaptureNavigationRequested;
                    _ = ObserveCaptureCompletionAsync(capture, token);
                }
                if (step == 2)
                    await _capturePreview.SelectRegionAsync(GetScreenBounds(ScreenshotSampleBorder), token);
            }
            else if (step >= 3)
            {
                if (_capturePreview is { } capture)
                {
                    // Manual navigation may skip the selection frame; confirmation still needs
                    // the known example region, never a rectangle left under the user's cursor.
                    await capture.SelectRegionAsync(GetScreenBounds(ScreenshotSampleBorder), token);
                    var region = await capture.ConfirmAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (region is null) { Stop(); return; }
                    capture.NavigationRequested -= OnCaptureNavigationRequested;
                    await capture.DisposeAsync();
                    _capturePreview = null;
                }
                token.ThrowIfCancellationRequested();
                if (version != _presentationVersion || !IsLoaded) return;
                if (!_miniPreviewShowing)
                {
                    _previewWindows.ShowMini(ScreenshotSampleText.Text,
                        GetText("OnboardingDemoScreenshotTranslation", "微小的进步也能带来很大改变。"));
                    _miniPreviewShowing = true;
                }
            }
            else
            {
                _previewWindows.HideAll();
            }
        }
        catch (OperationCanceledException)
        {
            if (!token.IsCancellationRequested && version == _presentationVersion && IsLoaded) Stop();
        }
        catch (Exception ex)
        {
            if (version == _presentationVersion && IsLoaded)
            {
                Stop();
                StageHintText.Text = GetText("OnboardingDemoUnavailable", "The demo could not open. You can retry or go straight to practice.");
                System.Diagnostics.Debug.WriteLine($"Onboarding native demo: {ex}");
            }
        }
        finally
        {
            _nativePresentationLock.Release();
            if (version == _presentationVersion)
            {
                _isPresentingNative = false;
                if (_isPlaying && IsLoaded) _playbackTimer.Start();
            }
        }
    }

    private async Task ObserveCaptureCompletionAsync(ScreenCaptureDemoSession capture, CancellationToken token)
    {
        RectInt32? region;
        try { region = await capture.Completion.ConfigureAwait(false); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Onboarding capture completion: {ex.Message}");
            region = null;
        }
        if (region is null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(_capturePreview, capture)) Stop();
            });
        }
    }

    private void OnCaptureNavigationRequested(object? sender, ScreenCaptureDemoNavigationKeyEventArgs e)
    {
        if (!ReferenceEquals(sender, _capturePreview) || !IsLoaded) return;
        if (e.Key == ScreenCaptureDemoNavigationKey.Right) NextStep();
        else if (e.Key == ScreenCaptureDemoNavigationKey.Space)
        {
            if (_isPlaying) Pause(); else Play();
        }
    }

    private static RectInt32 GetScreenBounds(FrameworkElement element)
    {
        var window = App.MainWindow ?? throw new InvalidOperationException("The onboarding window is not available.");
        var content = (UIElement)window.Content;
        var logical = element.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point());
        var origin = new NativePoint();
        if (!ClientToScreen(WindowNative.GetWindowHandle(window), ref origin))
            throw new InvalidOperationException("Cannot locate the sample on the desktop.");
        var scale = element.XamlRoot.RasterizationScale;
        return new RectInt32(origin.X + (int)Math.Floor(logical.X * scale), origin.Y + (int)Math.Floor(logical.Y * scale),
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
