using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Easydict.WinUI.Models;

/// <summary>
/// View-model for one row in the "Expose these services to local API" checkbox list
/// on the Local API settings tab.
/// </summary>
public sealed class ExposedServiceItem : INotifyPropertyChanged
{
    private bool _isExposed;

    public required string ServiceId { get; init; }
    public required string DisplayName { get; init; }
    public bool SupportsStreaming { get; init; }

    public bool IsExposed
    {
        get => _isExposed;
        set
        {
            if (_isExposed == value) return;
            _isExposed = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
