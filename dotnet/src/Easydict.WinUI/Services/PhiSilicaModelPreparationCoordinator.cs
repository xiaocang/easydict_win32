using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;

namespace Easydict.WinUI.Services;

internal sealed record PhiSilicaModelPreparationSnapshot(
    string ResourceKey,
    bool IsPreparing,
    WindowsAIReadyState? ReadyState = null);

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
        new("PhiSilicaPreparationProgress_Checking", IsPreparing: false);

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
            reportProgress?.Invoke("PhiSilicaPreparationProgress_ReusingExisting");
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
            Report("PhiSilicaPreparationProgress_Checking");
            await Task.Yield();

            finalState = PhiSilicaAvailability.GetReadyState();
            if (finalState == WindowsAIReadyState.Ready)
            {
                return finalState;
            }

            Report("PhiSilicaPreparationProgress_Requesting");
            await Task.Yield();

            Report("PhiSilicaPreparationProgress_Waiting");
            finalState = await PhiSilicaAvailability.Client.EnsureReadyAsync(CancellationToken.None);

            Report("PhiSilicaPreparationProgress_Finalizing");
            return finalState;
        }
        finally
        {
            Complete(finalState);
        }
    }

    private void Report(string resourceKey)
    {
        var snapshot = new PhiSilicaModelPreparationSnapshot(resourceKey, IsPreparing: true);
        lock (_gate)
        {
            _currentSnapshot = snapshot;
        }

        ProgressChanged?.Invoke(this, snapshot);
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
