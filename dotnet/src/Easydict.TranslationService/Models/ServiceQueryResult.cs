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
    private bool _isStreaming;
    private string _streamingText = "";
    private bool _isExpanded = true;
    private bool _manuallyToggled;
    private bool _enabledQuery = true;
    private bool _hasQueried;

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
        set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowPendingQueryHint));
            }
        }
    }

    /// <summary>
    /// Whether the service is currently streaming a response.
    /// </summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetField(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ContentVisibility));
            }
        }
    }

    /// <summary>
    /// Accumulated streaming text (updated during streaming).
    /// </summary>
    public string StreamingText
    {
        get => _streamingText;
        set
        {
            if (SetField(ref _streamingText, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ContentVisibility));
            }
        }
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

    /// <summary>
    /// Whether this service should auto-query when translation is triggered.
    /// When true (default), service queries automatically and shows expanded.
    /// When false, service starts collapsed and only queries when user clicks to expand.
    /// Maps to macOS enabledQuery property.
    /// </summary>
    public bool EnabledQuery
    {
        get => _enabledQuery;
        set => SetField(ref _enabledQuery, value);
    }

    /// <summary>
    /// Whether this service has been queried (either auto or on-demand).
    /// Used to track if a manual-query service needs to fetch results when expanded.
    /// </summary>
    public bool HasQueried
    {
        get => _hasQueried;
        private set
        {
            if (SetField(ref _hasQueried, value))
            {
                OnPropertyChanged(nameof(ShowPendingQueryHint));
            }
        }
    }

    /// <summary>
    /// Mark this service as having been queried.
    /// </summary>
    public void MarkQueried() => HasQueried = true;

    /// <summary>
    /// Clear the queried state so the service can be retried.
    /// Used when a manual query is cancelled before producing a result.
    /// </summary>
    public void ClearQueried() => HasQueried = false;

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
    /// Whether to show the "Click to query" hint.
    /// Shown when service is manual-query (!EnabledQuery), not yet queried, and not loading.
    /// </summary>
    public bool ShowPendingQueryHint => !EnabledQuery && !HasQueried && !IsLoading;

    /// <summary>
    /// The translated text to display (or error message).
    /// During streaming, shows accumulated streaming text.
    /// </summary>
    public string DisplayText
    {
        get
        {
            // During streaming, show accumulated text
            if (IsStreaming && !string.IsNullOrEmpty(StreamingText))
                return StreamingText;

            return Result?.TranslatedText ?? Error?.Message ?? "";
        }
    }

    /// <summary>
    /// Arrow glyph based on expand state: ChevronDown when expanded, ChevronRight when collapsed.
    /// </summary>
    public string ArrowGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    /// <summary>
    /// Content visibility based on expand state.
    /// Also visible during streaming to show incremental results.
    /// </summary>
    public bool ContentVisibility => IsExpanded && (HasResult || (IsStreaming && !string.IsNullOrEmpty(StreamingText)));

    /// <summary>
    /// Status text for the result (timing info or error).
    /// </summary>
    public string StatusText
    {
        get
        {
            if (IsStreaming) return "Streaming...";
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
        IsStreaming = false;
        StreamingText = "";
        IsExpanded = EnabledQuery; // Auto-query services expand, manual-query services collapse
        ManuallyToggled = false;
        HasQueried = false;
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ContentVisibility));
        OnPropertyChanged(nameof(ShowPendingQueryHint));
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
