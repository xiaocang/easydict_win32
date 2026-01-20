using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Easydict.TranslationService.Models;

/// <summary>
/// Represents a translation result from a single service with UI display state.
/// Mirrors macOS QueryResult's isShowing/manualShow pattern for collapse behavior.
/// </summary>
public sealed class ServiceQueryResult : INotifyPropertyChanged
{
    private TranslationResult? _result;
    private TranslationException? _error;
    private bool _isLoading;
    private bool _isExpanded = true;
    private bool _manuallyToggled;

    /// <summary>
    /// Service identifier (e.g., "google", "deepl").
    /// </summary>
    public string ServiceId { get; init; } = "";

    /// <summary>
    /// Display name for the service (e.g., "Google Translate").
    /// </summary>
    public string ServiceDisplayName { get; init; } = "";

    /// <summary>
    /// Path to the service icon asset.
    /// </summary>
    public string ServiceIconPath => $"ms-appx:///Assets/ServiceIcons/{ServiceId}.png";

    /// <summary>
    /// The translation result if successful.
    /// </summary>
    public TranslationResult? Result
    {
        get => _result;
        set => SetField(ref _result, value);
    }

    /// <summary>
    /// The error if translation failed.
    /// </summary>
    public TranslationException? Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    /// <summary>
    /// Whether the service is currently translating.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    /// <summary>
    /// Whether the result content is expanded (visible).
    /// Maps to macOS isShowing property.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ArrowGlyph));
                OnPropertyChanged(nameof(ContentVisibility));
            }
        }
    }

    /// <summary>
    /// Whether the user has manually toggled the expand state.
    /// Used to prevent auto-collapse of error results that user explicitly expanded.
    /// Maps to macOS manualShow property.
    /// </summary>
    public bool ManuallyToggled
    {
        get => _manuallyToggled;
        set => SetField(ref _manuallyToggled, value);
    }

    // Computed properties for UI binding

    /// <summary>
    /// Whether there is a result or error to display.
    /// </summary>
    public bool HasResult => Result != null || Error != null;

    /// <summary>
    /// Whether the result is an error.
    /// </summary>
    public bool HasError => Error != null;

    /// <summary>
    /// Whether the error is a warning type that should auto-collapse.
    /// </summary>
    public bool IsWarningError => Error?.ErrorCode is
        TranslationErrorCode.UnsupportedLanguage or
        TranslationErrorCode.InvalidResponse;

    /// <summary>
    /// The translated text to display (or error message).
    /// </summary>
    public string DisplayText => Result?.TranslatedText ?? Error?.Message ?? "";

    /// <summary>
    /// Arrow glyph based on expand state: ChevronDown when expanded, ChevronRight when collapsed.
    /// </summary>
    public string ArrowGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    /// <summary>
    /// Content visibility based on expand state.
    /// </summary>
    public bool ContentVisibility => IsExpanded && HasResult;

    /// <summary>
    /// Status text for the result (timing info or error).
    /// </summary>
    public string StatusText
    {
        get
        {
            if (IsLoading) return "Translating...";
            if (Error != null) return "Error";
            if (Result != null)
            {
                return Result.FromCache ? "cached" : $"{Result.TimingMs}ms";
            }
            return "";
        }
    }

    /// <summary>
    /// Toggle the expanded state and mark as manually toggled.
    /// </summary>
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded)
        {
            ManuallyToggled = true;
        }
    }

    /// <summary>
    /// Apply auto-collapse logic for error results.
    /// Called after translation completes.
    /// </summary>
    public void ApplyAutoCollapseLogic()
    {
        // Auto-collapse warning errors unless user manually expanded
        if (HasError && IsWarningError && !ManuallyToggled)
        {
            IsExpanded = false;
        }
    }

    /// <summary>
    /// Reset the result state for a new query.
    /// </summary>
    public void Reset()
    {
        Result = null;
        Error = null;
        IsLoading = false;
        IsExpanded = true;
        ManuallyToggled = false;
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ContentVisibility));
    }

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
