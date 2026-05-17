using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;

namespace Easydict.WinUI.Services;

internal sealed record PhiSilicaModelPreparationSnapshot(
    string ResourceKey,
    bool IsPreparing,
    WindowsAIReadyState? ReadyState = null,
    DeliveryOptimizationEstimate? DownloadEstimate = null,
    double? SdkProgressPercent = null)
{
    public double? ProgressPercent => DownloadEstimate?.ProgressPercent ?? SdkProgressPercent;
}

internal static class PhiSilicaModelPreparationProgressFormatter
{
    public static double? MergeProgressPercent(double? previousPercent, double? nextPercent)
    {
        if (nextPercent is not { } next
            || double.IsNaN(next)
            || double.IsInfinity(next))
        {
            return previousPercent;
        }

        var clampedNext = Math.Clamp(next, 0d, 100d);
        return previousPercent is { } previous
            ? Math.Max(Math.Clamp(previous, 0d, 100d), clampedNext)
            : clampedNext;
    }

    public static string FormatText(PhiSilicaModelPreparationSnapshot snapshot)
    {
        var loc = LocalizationService.Instance;
        var text = loc.GetString(snapshot.ResourceKey);
        if (string.IsNullOrWhiteSpace(text) || text == snapshot.ResourceKey)
        {
            text = "Preparing local AI model...";
        }

        if (snapshot.DownloadEstimate is not { } estimate)
        {
            return text;
        }

        var estimateTemplate = loc.GetString(PhiSilicaResources.ProgressKeys.DeliveryOptimizationEstimate);
        if (string.IsNullOrWhiteSpace(estimateTemplate)
            || estimateTemplate == PhiSilicaResources.ProgressKeys.DeliveryOptimizationEstimate)
        {
            estimateTemplate = "Current package: {0}% ({1} of {2}), about {3} remaining. More Windows-managed components may follow.";
        }

        var eta = estimate.EstimatedTimeRemaining is { } remaining
            ? FormatDuration(remaining)
            : loc.GetString(PhiSilicaResources.ProgressKeys.TimeUnknown);
        if (string.IsNullOrWhiteSpace(eta) || eta == PhiSilicaResources.ProgressKeys.TimeUnknown)
        {
            eta = "unknown time";
        }

        var detail = string.Format(
            CultureInfo.CurrentCulture,
            estimateTemplate,
            Math.Clamp((int)Math.Round(estimate.ProgressPercent), 0, 100),
            FormatBytes(estimate.BytesDownloaded),
            FormatBytes(estimate.TotalBytes),
            eta);

        return $"{text} {detail}";
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024d;
        const double MiB = KiB * 1024d;
        const double GiB = MiB * 1024d;

        if (bytes >= GiB)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / GiB);
        }

        if (bytes >= MiB)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / MiB);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", bytes / KiB);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0} min", Math.Ceiling(duration.TotalMinutes));
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0} sec", Math.Max(1, Math.Ceiling(duration.TotalSeconds)));
    }
}

/// <summary>
/// Shares the single Windows-managed Phi Silica preparation operation across
/// pages and translation triggers. The underlying Windows download/preparation
/// must not be tied to a page-level cancellation token: navigation can cancel a
/// caller's wait, but Windows should keep resuming the existing model job.
/// </summary>
internal sealed class PhiSilicaModelPreparationCoordinator
{
    public static PhiSilicaModelPreparationCoordinator Instance { get; } = new();

    private readonly object _gate = new();
    private Task<WindowsAIReadyState>? _activePreparationTask;
    private PhiSilicaModelPreparationSnapshot _currentSnapshot =
        new(PhiSilicaResources.ProgressKeys.Checking, IsPreparing: false);

    private static readonly TimeSpan DeliveryOptimizationPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeliveryOptimizationQueryTimeout = TimeSpan.FromSeconds(4);
    private const long MinimumCandidateFileSizeBytes = 100L * 1024L * 1024L;

    private PhiSilicaModelPreparationCoordinator()
    {
    }

    public event EventHandler<PhiSilicaModelPreparationSnapshot>? ProgressChanged;

    public bool IsPreparing
    {
        get
        {
            lock (_gate)
            {
                return _activePreparationTask is { IsCompleted: false };
            }
        }
    }

    public PhiSilicaModelPreparationSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _currentSnapshot;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot for displaying a transient preparation status text.
    /// If a preparation is in flight, the active snapshot's progress fields
    /// are preserved so the UI doesn't lose the percent it was already showing.
    /// </summary>
    public PhiSilicaModelPreparationSnapshot CreatePreparingSnapshot(string resourceKey)
    {
        var snapshot = CurrentSnapshot;
        return snapshot.IsPreparing
            ? snapshot with { ResourceKey = resourceKey }
            : new PhiSilicaModelPreparationSnapshot(resourceKey, IsPreparing: true);
    }

    public async Task<WindowsAIReadyState> EnsureReadyAsync(
        Action<string>? reportProgress,
        CancellationToken waitCancellationToken)
    {
        Task<WindowsAIReadyState> task;
        var reusedExisting = false;

        lock (_gate)
        {
            if (_activePreparationTask is { IsCompleted: false })
            {
                task = _activePreparationTask;
                reusedExisting = true;
            }
            else
            {
                _activePreparationTask = RunPreparationAsync();
                task = _activePreparationTask;
            }
        }

        if (reusedExisting)
        {
            Report(PhiSilicaResources.ProgressKeys.ReusingExisting);
            reportProgress?.Invoke(PhiSilicaResources.ProgressKeys.ReusingExisting);
        }

        var snapshot = CurrentSnapshot;
        if (snapshot.IsPreparing)
        {
            reportProgress?.Invoke(snapshot.ResourceKey);
        }

        return await task.WaitAsync(waitCancellationToken);
    }

    private async Task<WindowsAIReadyState> RunPreparationAsync()
    {
        WindowsAIReadyState finalState = WindowsAIReadyState.NotReady;

        try
        {
            Report(PhiSilicaResources.ProgressKeys.Checking);
            PhiSilicaBackendHealthMonitor.Shared.Reset();
            await Task.Yield();

            finalState = PhiSilicaAvailability.GetReadyState();
            if (finalState == WindowsAIReadyState.Ready)
            {
                await EnsurePhiSilicaBackendHealthyAsync();
                return finalState;
            }

            var existingEstimate = await TryGetDeliveryOptimizationEstimateAsync(CancellationToken.None);
            if (existingEstimate is not null)
            {
                Report(CreateDeliveryOptimizationSnapshot(
                    PhiSilicaResources.ProgressKeys.ReusingExisting,
                    existingEstimate));
            }

            Report(PhiSilicaResources.ProgressKeys.Requesting);
            await Task.Yield();

            Report(PhiSilicaResources.ProgressKeys.Waiting);
            var sdkProgress = new Progress<double>(ReportSdkProgress);
            var ensureReadyTask = PhiSilicaAvailability.Client.EnsureReadyAsync(
                CancellationToken.None,
                sdkProgress);
            using var pollCts = new CancellationTokenSource();
            var pollTask = PollDeliveryOptimizationProgressAsync(ensureReadyTask, pollCts.Token);

            try
            {
                finalState = await ensureReadyTask;
            }
            finally
            {
                pollCts.Cancel();
                try { await pollTask; } catch (OperationCanceledException) { }
            }

            Report(PhiSilicaResources.ProgressKeys.Finalizing);
            if (finalState == WindowsAIReadyState.Ready)
            {
                await EnsurePhiSilicaBackendHealthyAsync();
            }

            return finalState;
        }
        finally
        {
            Complete(finalState);
        }
    }

    private void Report(string resourceKey)
    {
        Report(new PhiSilicaModelPreparationSnapshot(resourceKey, IsPreparing: true));
    }

    private void Report(PhiSilicaModelPreparationSnapshot snapshot)
    {
        lock (_gate)
        {
            snapshot = PreserveProgress(snapshot, _currentSnapshot);
            if (_currentSnapshot == snapshot)
            {
                return;
            }
            _currentSnapshot = snapshot;
        }

        ProgressChanged?.Invoke(this, snapshot);
    }

    private static PhiSilicaModelPreparationSnapshot PreserveProgress(
        PhiSilicaModelPreparationSnapshot snapshot,
        PhiSilicaModelPreparationSnapshot currentSnapshot)
    {
        if (!snapshot.IsPreparing)
        {
            return snapshot;
        }

        var previousPercent = currentSnapshot.IsPreparing
            ? currentSnapshot.ProgressPercent
            : null;
        var mergedPercent = PhiSilicaModelPreparationProgressFormatter.MergeProgressPercent(
            previousPercent,
            snapshot.ProgressPercent);

        if (mergedPercent is null || mergedPercent == snapshot.ProgressPercent)
        {
            return snapshot;
        }

        return snapshot with
        {
            DownloadEstimate = null,
            SdkProgressPercent = mergedPercent,
        };
    }

    private void ReportSdkProgress(double progressPercent)
    {
        if (double.IsNaN(progressPercent) || double.IsInfinity(progressPercent))
        {
            return;
        }

        // Round to whole-percent steps so high-frequency SDK callbacks don't
        // wake observers (and the WinUI dispatcher) for sub-pixel changes.
        var rounded = Math.Round(Math.Clamp(progressPercent, 0d, 100d), MidpointRounding.AwayFromZero);

        Report(new PhiSilicaModelPreparationSnapshot(
            PhiSilicaResources.ProgressKeys.Waiting,
            IsPreparing: true,
            SdkProgressPercent: rounded));
    }

    private async Task EnsurePhiSilicaBackendHealthyAsync()
    {
        await PhiSilicaBackendHealthMonitor.Shared.EnsureHealthyAsync(
            PhiSilicaAvailability.Client,
            snapshot =>
            {
                var resourceKey = snapshot.State switch
                {
                    PhiSilicaBackendHealthState.CreatingSession =>
                        PhiSilicaResources.ProgressKeys.CreatingSession,
                    PhiSilicaBackendHealthState.WarmingUp =>
                        PhiSilicaResources.ProgressKeys.WarmingUp,
                    PhiSilicaBackendHealthState.Healthy =>
                        PhiSilicaResources.ProgressKeys.Finalizing,
                    _ => PhiSilicaResources.ProgressKeys.Finalizing,
                };
                Report(resourceKey);
            },
            CancellationToken.None);
    }

    private async Task PollDeliveryOptimizationProgressAsync(
        Task<WindowsAIReadyState> preparationTask,
        CancellationToken cancellationToken)
    {
        while (!preparationTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(DeliveryOptimizationPollInterval, cancellationToken);

            var estimate = await TryGetDeliveryOptimizationEstimateAsync(cancellationToken);
            if (estimate is null)
            {
                continue;
            }

            Report(CreateDeliveryOptimizationSnapshot(
                PhiSilicaResources.ProgressKeys.Waiting,
                estimate));
        }
    }

    private static PhiSilicaModelPreparationSnapshot CreateDeliveryOptimizationSnapshot(
        string resourceKey,
        DeliveryOptimizationEstimate estimate)
    {
        return new PhiSilicaModelPreparationSnapshot(
            resourceKey,
            IsPreparing: true,
            DownloadEstimate: estimate);
    }

    private static async Task<DeliveryOptimizationEstimate?> TryGetDeliveryOptimizationEstimateAsync(
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DeliveryOptimizationQueryTimeout);

        try
        {
            var jobs = await QueryDeliveryOptimizationJobsAsync(timeoutCts.Token);
            return SelectDeliveryOptimizationCandidate(jobs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PhiSilicaPreparation] Delivery Optimization progress query failed: {ex.Message}");
            return null;
        }
    }

    private static DeliveryOptimizationEstimate? SelectDeliveryOptimizationCandidate(
        IReadOnlyList<DeliveryOptimizationJobSnapshot> jobs)
    {
        var candidate = jobs
            .Select(job => (job, kind: ParseDeliveryOptimizationStatus(job.StatusName)))
            .Where(t => t.job.FileSize >= MinimumCandidateFileSizeBytes
                && t.job.TotalBytesDownloaded > 0
                && t.kind != DeliveryOptimizationStatusKind.Other)
            .OrderBy(t => (int)t.kind)
            .ThenByDescending(t => t.job.TotalBytesDownloaded)
            .Select(t => t.job)
            .FirstOrDefault();

        if (candidate is null || candidate.FileSize <= 0)
        {
            return null;
        }

        var percent = candidate.TotalBytesDownloaded * 100d / candidate.FileSize;
        TimeSpan? eta = null;
        if (candidate.DownloadDurationSeconds > 5
            && candidate.TotalBytesDownloaded < candidate.FileSize)
        {
            var bytesPerSecond = candidate.TotalBytesDownloaded / candidate.DownloadDurationSeconds;
            if (bytesPerSecond > 0)
            {
                eta = TimeSpan.FromSeconds((candidate.FileSize - candidate.TotalBytesDownloaded) / bytesPerSecond);
            }
        }

        return new DeliveryOptimizationEstimate(
            Math.Clamp(percent, 0d, 100d),
            candidate.TotalBytesDownloaded,
            candidate.FileSize,
            eta);
    }

    private static DeliveryOptimizationStatusKind ParseDeliveryOptimizationStatus(string statusName)
    {
        if (string.Equals(statusName, "Downloading", StringComparison.OrdinalIgnoreCase))
        {
            return DeliveryOptimizationStatusKind.Downloading;
        }

        if (string.Equals(statusName, "Transferring", StringComparison.OrdinalIgnoreCase))
        {
            return DeliveryOptimizationStatusKind.Transferring;
        }

        if (string.Equals(statusName, "Caching", StringComparison.OrdinalIgnoreCase))
        {
            return DeliveryOptimizationStatusKind.Caching;
        }

        return DeliveryOptimizationStatusKind.Other;
    }

    private static async Task<IReadOnlyList<DeliveryOptimizationJobSnapshot>> QueryDeliveryOptimizationJobsAsync(
        CancellationToken cancellationToken)
    {
        const string command = """
            $ErrorActionPreference = 'SilentlyContinue';
            @(Get-DeliveryOptimizationStatus | Select-Object `
                @{Name='FileId';Expression={$_.FileId}}, `
                @{Name='StatusName';Expression={$_.Status.ToString()}}, `
                @{Name='FileSize';Expression={[Int64]$_.FileSize}}, `
                @{Name='TotalBytesDownloaded';Expression={[Int64]$_.TotalBytesDownloaded}}, `
                @{Name='DownloadDurationSeconds';Expression={[Double]$_.DownloadDuration.TotalSeconds}}) |
                ConvertTo-Json -Compress -Depth 3
            """;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = false,
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);

        string output;
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            output = await outputTask;
            _ = await errorTask;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return ParseDeliveryOptimizationJobs(output);
    }

    private static IReadOnlyList<DeliveryOptimizationJobSnapshot> ParseDeliveryOptimizationJobs(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return document.RootElement
                .EnumerateArray()
                .Select(ParseDeliveryOptimizationJob)
                .Where(job => job is not null)
                .Cast<DeliveryOptimizationJobSnapshot>()
                .ToArray();
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var job = ParseDeliveryOptimizationJob(document.RootElement);
            return job is null ? [] : [job];
        }

        return [];
    }

    private static DeliveryOptimizationJobSnapshot? ParseDeliveryOptimizationJob(JsonElement element)
    {
        var statusName = GetString(element, "StatusName");
        var fileSize = GetInt64(element, "FileSize");
        var totalBytesDownloaded = GetInt64(element, "TotalBytesDownloaded");
        var durationSeconds = GetDouble(element, "DownloadDurationSeconds");

        if (string.IsNullOrWhiteSpace(statusName)
            || fileSize <= 0
            || totalBytesDownloaded <= 0)
        {
            return null;
        }

        return new DeliveryOptimizationJobSnapshot(
            statusName,
            fileSize,
            totalBytesDownloaded,
            durationSeconds);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0,
        };
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0,
        };
    }

    private void Complete(WindowsAIReadyState finalState)
    {
        PhiSilicaModelPreparationSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new PhiSilicaModelPreparationSnapshot(
                PhiSilicaAvailability.GetStatusResourceKey(finalState),
                IsPreparing: false,
                finalState);
            _currentSnapshot = snapshot;
        }

        ProgressChanged?.Invoke(this, snapshot);
    }
}

internal enum DeliveryOptimizationStatusKind
{
    Downloading = 0,
    Transferring = 1,
    Caching = 2,
    Other = 3,
}

internal sealed record DeliveryOptimizationJobSnapshot(
    string StatusName,
    long FileSize,
    long TotalBytesDownloaded,
    double DownloadDurationSeconds);

internal sealed record DeliveryOptimizationEstimate(
    double ProgressPercent,
    long BytesDownloaded,
    long TotalBytes,
    TimeSpan? EstimatedTimeRemaining);
