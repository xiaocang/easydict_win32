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
    private bool _enabledQuery = true;
    private bool _isAvailable = true;
    private bool _isReorderModeEnabled;

    /// <summary>
    /// The service identifier (e.g., "google", "deepl").
    /// </summary>
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>
    /// The display name shown in the UI (e.g., "Google Translate", "DeepL").
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Whether this service requires configuration but is not yet configured.
    /// When true, the service name is displayed in italic.
    /// </summary>
    public bool IsUnconfigured { get; init; }

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

    /// <summary>
    /// Whether this service should auto-query (true) or query on demand (false).
    /// When true, service queries automatically when translation triggers.
    /// When false, service starts collapsed and only queries when user clicks to expand.
    /// </summary>
    public bool EnabledQuery
    {
        get => _enabledQuery;
        set
        {
            if (_enabledQuery != value)
            {
                _enabledQuery = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether this service is available in the current region.
    /// When false, the service is grayed out and cannot be checked.
    /// International-only services are unavailable when EnableInternationalServices is off.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable != value)
            {
                _isAvailable = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether the settings page is currently in explicit reorder mode.
    /// When false, the per-row move controls stay hidden to keep the list tidy.
    /// </summary>
    public bool IsReorderModeEnabled
    {
        get => _isReorderModeEnabled;
        set
        {
            if (_isReorderModeEnabled != value)
            {
                _isReorderModeEnabled = value;
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
