using Easydict.TranslationService.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Services;

/// <summary>Owns the warning for one window and prevents stale detection callbacks from replacing it.</summary>
internal sealed class LanguageDetectionWarningPresenter
{
    private readonly InfoBar _bar;
    private readonly Action? _layoutChanged;
    private int _generation;

    public LanguageDetectionWarningPresenter(InfoBar bar, Action? layoutChanged = null)
    {
        _bar = bar;
        _layoutChanged = layoutChanged;
        _bar.Unloaded += (_, _) => Reset();
    }

    public void Reset()
    {
        _generation++;
        _bar.IsOpen = false;
        _bar.Visibility = Visibility.Collapsed;
        _layoutChanged?.Invoke();
    }

    public async Task<Language> DetectAsync(
        LanguageDetectionService service,
        string text,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        Reset();
        var generation = _generation;
        var rateLimited = false;
        var completed = false;
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } deadline)
            attemptCts.CancelAfter(deadline);

        Language detected;
        try
        {
            detected = await Task.Run(() => service.DetectAsync(text, attemptCts.Token, () =>
            {
                rateLimited = true;
                _bar.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!completed && generation == _generation && !cancellationToken.IsCancellationRequested)
                        Show(null);
                });
            }));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Preserve a 429 warning even when Minimal mode's short detection deadline expires.
            detected = Language.Auto;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
                Reset();
            throw;
        }
        finally
        {
            completed = true;
        }

        if (generation == _generation && !cancellationToken.IsCancellationRequested && rateLimited)
            Show(detected);

        return detected;
    }

    private void Show(Language? detected)
    {
        if (!_bar.IsLoaded)
            return;

        var loc = LocalizationService.Instance;
        _bar.Title = loc.GetString("LanguageDetectionRateLimitedTitle");
        _bar.Message = detected switch
        {
            null => loc.GetString("LanguageDetectionRateLimitedPending"),
            Language.Auto => loc.GetString("LanguageDetectionRateLimitedFailed"),
            _ => string.Format(loc.GetString("LanguageDetectionRateLimitedRecovered"), detected.Value.GetDisplayName())
        };
        _bar.IsOpen = true;
        _bar.Visibility = Visibility.Visible;
        _layoutChanged?.Invoke();
    }
}
