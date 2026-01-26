using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Easydict.WinUI.Models;

/// <summary>
/// Represents a translation service item for checkbox selection in settings.
/// Implements INotifyPropertyChanged for UI binding.
/// </summary>
public class ServiceCheckItem : INotifyPropertyChanged
{
    private bool _isChecked;

    /// <summary>
    /// The service identifier (e.g., "google", "deepl").
    /// </summary>
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>
    /// The display name shown in the UI (e.g., "Google Translate", "DeepL").
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Whether this service is enabled/checked.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
