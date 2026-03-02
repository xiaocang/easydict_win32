using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Easydict.WinUI.Models;

/// <summary>
/// Represents a file item in the Long Document Translation queue.
/// Implements INotifyPropertyChanged for UI binding.
/// </summary>
public class LongDocFileItem : INotifyPropertyChanged
{
    private LongDocItemStatus _status;
    private int _progressPercentage;
    private string _progressDetail = string.Empty;
    private string? _outputPath;
    private DateTime _completedTime;
    private string? _errorMessage;

    /// <summary>
    /// The full path to the source file.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// The file name (without path).
    /// </summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>
    /// The current status of the translation.
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
                OnPropertyChanged(nameof(IsInProgress));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsCancelled));
            }
        }
    }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage
    {
        get => _progressPercentage;
        set
        {
            if (_progressPercentage != value)
            {
                _progressPercentage = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Detailed progress message (e.g., "Translating page 5 of 20").
    /// </summary>
    public string ProgressDetail
    {
        get => _progressDetail;
        set
        {
            if (_progressDetail != value)
            {
                _progressDetail = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// The output file path after translation.
    /// </summary>
    public string? OutputPath
    {
        get => _outputPath;
        set
        {
            if (_outputPath != value)
            {
                _outputPath = value;
                OnPropertyChanged();
            }
        }
    }

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
            }
        }
    }

    /// <summary>
    /// Error message if translation failed.
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether translation is currently in progress.
    /// </summary>
    public bool IsInProgress => Status == LongDocItemStatus.InProgress;

    /// <summary>
    /// Whether translation completed successfully.
    /// </summary>
    public bool IsCompleted => Status == LongDocItemStatus.Completed;

    /// <summary>
    /// Whether translation failed.
    /// </summary>
    public bool IsFailed => Status == LongDocItemStatus.Failed;

    /// <summary>
    /// Whether translation was cancelled.
    /// </summary>
    public bool IsCancelled => Status == LongDocItemStatus.Cancelled;

    /// <summary>
    /// Whether the remove button should be enabled (disabled during translation).
    /// </summary>
    public bool CanRemove => !IsInProgress;

    /// <summary>
    /// Relative time string since completion (e.g., "2 minutes ago").
    /// </summary>
    public string TimeAgo
    {
        get
        {
            if (CompletedTime == DateTime.MinValue)
                return string.Empty;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Updates progress with throttling support (called from UI layer).
    /// </summary>
    public void UpdateProgress(int percentage, string detail)
    {
        ProgressPercentage = percentage;
        ProgressDetail = detail;
    }

    /// <summary>
    /// Marks the item as completed.
    /// </summary>
    public void MarkCompleted(string outputPath)
    {
        Status = LongDocItemStatus.Completed;
        OutputPath = outputPath;
        CompletedTime = DateTime.Now;
        ProgressPercentage = 100;
        ProgressDetail = "Completed";
    }

    /// <summary>
    /// Marks the item as failed.
    /// </summary>
    public void MarkFailed(string errorMessage)
    {
        Status = LongDocItemStatus.Failed;
        ErrorMessage = errorMessage;
        CompletedTime = DateTime.Now;
    }

    /// <summary>
    /// Marks the item as cancelled.
    /// </summary>
    public void MarkCancelled()
    {
        Status = LongDocItemStatus.Cancelled;
        CompletedTime = DateTime.Now;
    }

    /// <summary>
    /// Marks the item as in progress.
    /// </summary>
    public void MarkInProgress()
    {
        Status = LongDocItemStatus.InProgress;
        ProgressPercentage = 0;
        ProgressDetail = "Starting...";
        ErrorMessage = null;
    }

    /// <summary>
    /// Resets the item to pending state.
    /// </summary>
    public void Reset()
    {
        Status = LongDocItemStatus.Pending;
        ProgressPercentage = 0;
        ProgressDetail = string.Empty;
        OutputPath = null;
        CompletedTime = DateTime.MinValue;
        ErrorMessage = null;
    }
}

/// <summary>
/// Status of a long document translation item.
/// </summary>
public enum LongDocItemStatus
{
    /// <summary>
    /// File is queued but not yet started.
    /// </summary>
    Pending,

    /// <summary>
    /// Translation is currently in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// Translation completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Translation failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Translation was cancelled by user.
    /// </summary>
    Cancelled
}
