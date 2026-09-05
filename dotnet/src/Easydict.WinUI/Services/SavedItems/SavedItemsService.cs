using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services.SavedItems;

/// <summary>
/// Process-wide serialized facade for saved-items writes and lifecycle cleanup.
/// Database work never touches XAML objects; listeners marshal notifications themselves.
/// </summary>
public sealed class SavedItemsService : IAsyncDisposable
{
    private static readonly Lazy<SavedItemsService> _instance = new(() => new SavedItemsService(
        new SavedItemsStore(),
        TimeProvider.System,
        TimeSpan.FromHours(24),
        static () => SettingsService.Instance.HistoryRetentionDays));
    private readonly SavedItemsStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cleanupInterval;
    private readonly Func<int> _retentionDaysProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycleCts = new();
    private Task? _cleanupTask;
    private volatile bool _acceptBackgroundRecords = true;
    private int _initialized;

    public static SavedItemsService Instance => _instance.Value;

    internal SavedItemsService(
        SavedItemsStore store,
        TimeProvider timeProvider,
        TimeSpan cleanupInterval,
        Func<int>? retentionDaysProvider = null)
    {
        _store = store;
        _timeProvider = timeProvider;
        _cleanupInterval = cleanupInterval;
        _retentionDaysProvider = retentionDaysProvider ?? (static () => SettingsService.Instance.HistoryRetentionDays);
    }

    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public event EventHandler<SavedItemsChangedEventArgs>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await PruneExpiredHistoryAsync(cancellationToken).ConfigureAwait(false);
            _cleanupTask = RunCleanupLoopAsync(_lifecycleCts.Token);
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    /// <summary>Best-effort translation-path write. Errors are diagnostic-only.</summary>
    public async Task RecordSnapshotAsync(QuerySnapshotDraft? draft, CancellationToken cancellationToken = default)
    {
        if (draft is null || !_acceptBackgroundRecords)
            return;

        var snapshot = draft.Snapshot();
        if (snapshot.Results.Count == 0)
            return;

        try
        {
            await SerializeWriteAsync(async token =>
            {
                var changed = await _store.UpsertTrackedSnapshotAsync(snapshot, draft.HistoryEnabled, token).ConfigureAwait(false);
                if (changed)
                    OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.History, snapshot.Id));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CrashDiagnostics.LogException("SavedItemsService.RecordSnapshotAsync", exception, isTerminating: false, isHandled: true);
        }
    }

    public Task<FavoriteToggleResult> ToggleQueryFavoriteAsync(QuerySnapshotDraft draft, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            var result = await _store.AddQueryFavoriteAsync(
                draft.Snapshot(),
                token,
                RetentionDays).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Favorite, draft.Id, result.FavoriteId));
            return result;
        }, cancellationToken);

    public Task<FavoriteToggleResult> ToggleStoredQueryFavoriteAsync(Guid queryId, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            var result = await _store.ToggleStoredQueryFavoriteAsync(
                queryId,
                token,
                RetentionDays).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Favorite, queryId, result.FavoriteId));
            return result;
        }, cancellationToken);

    public Task<FavoriteToggleResult> ToggleStoredResultFavoriteAsync(Guid queryId, Guid resultId, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            var result = await _store.ToggleStoredResultFavoriteAsync(
                queryId,
                resultId,
                token,
                RetentionDays).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Favorite, queryId, result.FavoriteId));
            return result;
        }, cancellationToken);

    public Task<FavoriteToggleResult> ToggleResultFavoriteAsync(QuerySnapshotDraft draft, Guid resultId, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            var result = await _store.AddResultFavoriteAsync(
                draft.Snapshot(),
                resultId,
                token,
                RetentionDays).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Favorite, draft.Id, result.FavoriteId));
            return result;
        }, cancellationToken);

    public Task RemoveFavoriteAsync(Guid favoriteId, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            await _store.RemoveFavoriteAsync(favoriteId, RetentionDays, token).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Favorite, favoriteId: favoriteId));
        }, cancellationToken);

    public Task SetFavoritePinnedAsync(Guid favoriteId, bool pinned, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            await _store.SetFavoritePinnedAsync(favoriteId, pinned, token).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Metadata, favoriteId: favoriteId));
        }, cancellationToken);

    public Task UpdateFavoriteMetadataAsync(Guid favoriteId, string note, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            await _store.UpdateFavoriteMetadataAsync(favoriteId, note, tags, token).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Metadata, favoriteId: favoriteId));
        }, cancellationToken);

    public Task<FavoriteStateMap> GetFavoriteStatesAsync(Guid queryId, CancellationToken cancellationToken = default)
        => _store.GetFavoriteStatesAsync(queryId, cancellationToken);

    public Task<SavedItemsPageResult<SavedQueryListItem>> ListHistoryAsync(HistoryListRequest request, CancellationToken cancellationToken = default)
        => _store.ListHistoryAsync(request, cancellationToken);

    public Task<SavedQueryDetail?> GetQueryDetailAsync(Guid queryId, CancellationToken cancellationToken = default)
        => _store.GetQueryDetailAsync(queryId, cancellationToken);

    public Task<SavedItemsPageResult<FavoriteListItem>> ListFavoritesAsync(FavoriteListRequest request, CancellationToken cancellationToken = default)
        => _store.ListFavoritesAsync(request, cancellationToken);

    public Task<FavoriteDetail?> GetFavoriteDetailAsync(Guid favoriteId, CancellationToken cancellationToken = default)
        => _store.GetFavoriteDetailAsync(favoriteId, cancellationToken);

    public Task<SavedItemsFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
        => _store.GetFilterOptionsAsync(cancellationToken);

    public Task DeleteHistoryAsync(Guid queryId, CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            await _store.DeleteHistoryAsync(queryId, token).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.History, queryId));
        }, cancellationToken);

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            await _store.ClearHistoryAsync(token).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.History));
        }, cancellationToken);

    public Task PruneExpiredHistoryAsync(CancellationToken cancellationToken = default)
        => SerializeWriteAsync(async token =>
        {
            await _store.PruneExpiredHistoryAsync(RetentionDays, _timeProvider.GetUtcNow(), token).ConfigureAwait(false);
            OnChanged(new SavedItemsChangedEventArgs(SavedItemsChangeKind.Cleanup));
        }, cancellationToken);

    /// <summary>Stops timer and rejects new best-effort recordings without blocking the UI thread.</summary>
    public void BeginCleanupServices()
    {
        _acceptBackgroundRecords = false;
        _lifecycleCts.Cancel();
    }

    public async Task CleanupServicesAsync()
    {
        BeginCleanupServices();
        if (_cleanupTask is not null)
        {
            try { await _cleanupTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        _writeGate.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupServicesAsync().ConfigureAwait(false);
        _lifecycleCts.Dispose();
        _writeGate.Dispose();
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_cleanupInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try { await PruneExpiredHistoryAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) { CrashDiagnostics.LogException("SavedItemsService.Cleanup", exception, isTerminating: false, isHandled: true); }
        }
    }

    private int RetentionDays => Math.Clamp(_retentionDaysProvider(), 1, 3650);

    private async Task SerializeWriteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await action(cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task<T> SerializeWriteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action(cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private void OnChanged(SavedItemsChangedEventArgs args)
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke(this, args);
    }
}
