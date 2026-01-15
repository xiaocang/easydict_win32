using Windows.ApplicationModel.DataTransfer;

namespace Easydict.WinUI.Services;

/// <summary>
/// Monitors clipboard changes and provides clipboard operations.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private bool _isMonitoring;
    private bool _isDisposed;
    private string _lastClipboardText = string.Empty;

    /// <summary>
    /// Fired when clipboard text changes (only when monitoring is enabled).
    /// </summary>
    public event Action<string>? OnClipboardTextChanged;

    /// <summary>
    /// Gets or sets whether clipboard monitoring is enabled.
    /// </summary>
    public bool IsMonitoringEnabled
    {
        get => _isMonitoring;
        set
        {
            if (_isMonitoring == value) return;
            _isMonitoring = value;

            if (_isMonitoring)
            {
                Clipboard.ContentChanged += OnClipboardContentChanged;
            }
            else
            {
                Clipboard.ContentChanged -= OnClipboardContentChanged;
            }
        }
    }

    private async void OnClipboardContentChanged(object? sender, object e)
    {
        if (!_isMonitoring) return;

        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) && text != _lastClipboardText)
                {
                    _lastClipboardText = text;
                    OnClipboardTextChanged?.Invoke(text);
                }
            }
        }
        catch
        {
            // Ignore clipboard access errors
        }
    }

    /// <summary>
    /// Get current clipboard text.
    /// </summary>
    public static async Task<string?> GetTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                return await content.GetTextAsync();
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    /// <summary>
    /// Set clipboard text.
    /// </summary>
    public static void SetText(string text)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        IsMonitoringEnabled = false;
    }
}

