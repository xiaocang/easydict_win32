using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Easydict.WinUI.Models;

/// <summary>
/// Represents a completed long document translation in the history.
/// Implements INotifyPropertyChanged for UI binding.
/// </summary>
public class LongDocHistoryItem : INotifyPropertyChanged
{
    private DateTime _completedTime;
    private LongDocItemStatus _status;

    /// <summary>
    /// The full path to the source file.
    /// </summary>
    public string SourceFilePath { get; init; } = string.Empty;

    /// <summary>
    /// The file name (without path).
    /// </summary>
    public string SourceFileName => Path.GetFileName(SourceFilePath);

    /// <summary>
    /// The full path to the output file.
    /// </summary>
    public string OutputFilePath { get; init; } = string.Empty;

    /// <summary>
    /// The output file name.
    /// </summary>
    public string OutputFileName => Path.GetFileName(OutputFilePath);

    /// <summary>
    /// The time when translation completed.
    /// </summary>
    public DateTime CompletedTime
    {
        get => _completedTime;
        set
        {
            if (_completedTime != value)
            {
                _completedTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimeAgo));
                OnPropertyChanged(nameof(DisplayTime));
            }
        }
    }

    /// <summary>
    /// The status of the translation (Completed or Failed).
    /// </summary>
    public LongDocItemStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>
    /// The translation service used (e.g., "Google Translate", "DeepL").
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// The target language.
    /// </summary>
    public string TargetLanguage { get; init; } = string.Empty;

    /// <summary>
    /// Error message if translation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether translation completed successfully.
    /// </summary>
    public bool IsCompleted => Status == LongDocItemStatus.Completed;

    /// <summary>
    /// Whether translation failed.
    /// </summary>
    public bool IsFailed => Status == LongDocItemStatus.Failed;

    /// <summary>
    /// Status text for display.
    /// </summary>
    public string StatusText => IsCompleted ? "Completed" : IsFailed ? "Failed" : "Unknown";

    /// <summary>
    /// Relative time string since completion (e.g., "2 minutes ago").
    /// </summary>
    public string TimeAgo
    {
        get
        {
            var span = DateTime.Now - CompletedTime;
            if (span < TimeSpan.FromSeconds(60))
                return "just now";
            if (span < TimeSpan.FromMinutes(60))
                return $"{(int)span.TotalMinutes} minute{(span.TotalMinutes >= 2 ? "s" : "")} ago";
            if (span < TimeSpan.FromHours(24))
                return $"{(int)span.TotalHours} hour{(span.TotalHours >= 2 ? "s" : "")} ago";
            if (span < TimeSpan.FromDays(7))
                return $"{(int)span.TotalDays} day{(span.TotalDays >= 2 ? "s" : "")} ago";

            return CompletedTime.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>
    /// Display time string (short format).
    /// </summary>
    public string DisplayTime => CompletedTime.ToString("HH:mm");

    /// <summary>
    /// Display date string (for older items).
    /// </summary>
    public string DisplayDate => CompletedTime.ToString("MMM dd");

    /// <summary>
    /// Full display string for this history item.
    /// </summary>
    public string DisplayText => $"{SourceFileName} → {ServiceName} ({TargetLanguage})";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Creates a history item from a completed file item.
    /// </summary>
    public static LongDocHistoryItem FromFileItem(
        LongDocFileItem fileItem,
        string serviceName,
        string targetLanguage)
    {
        return new LongDocHistoryItem
        {
            SourceFilePath = fileItem.FilePath,
            OutputFilePath = fileItem.OutputPath ?? string.Empty,
            CompletedTime = fileItem.CompletedTime,
            Status = fileItem.Status,
            ServiceName = serviceName,
            TargetLanguage = targetLanguage,
            ErrorMessage = fileItem.ErrorMessage
        };
    }
}
