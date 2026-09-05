using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.SavedItems;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class SavedItemsServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "easydict-saved-items-service-" + Guid.NewGuid());
    private SavedItemsService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _service = new SavedItemsService(
            new SavedItemsStore(Path.Combine(_directory, "saved_items.db")),
            TimeProvider.System,
            TimeSpan.FromDays(1));
        await _service.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task RecordSnapshotAsync_CommitsBeforePublishingHistoryChange()
    {
        var events = new List<SavedItemsChangedEventArgs>();
        _service.Changed += (_, args) => events.Add(args);
        var draft = new QuerySnapshotDraft("source", "en", "zh-CN", SavedQueryKind.Translation, QuerySourceKind.Manual, true);
        draft.TryAddTranslation("provider", "Provider", 0, new TranslationResult
        {
            OriginalText = "source",
            TranslatedText = "result",
            ServiceName = "Provider"
        });

        await _service.RecordSnapshotAsync(draft);

        events.Should().ContainSingle(args => args.Kind == SavedItemsChangeKind.History && args.QueryId == draft.Id);
        var history = await _service.ListHistoryAsync(new HistoryListRequest());
        history.Items.Should().ContainSingle(item => item.Id == draft.Id);
    }

    [Fact]
    public async Task RecordSnapshotAsync_IgnoresEmptyDraft()
    {
        var events = new List<SavedItemsChangedEventArgs>();
        _service.Changed += (_, args) => events.Add(args);
        var draft = new QuerySnapshotDraft("source", "en", "zh-CN", SavedQueryKind.Translation, QuerySourceKind.Manual, true);

        await _service.RecordSnapshotAsync(draft);

        events.Should().BeEmpty();
        (await _service.ListHistoryAsync(new HistoryListRequest())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordSnapshotAsync_DoesNotPersistOrPublishWhenHistoryIsDisabled()
    {
        var events = new List<SavedItemsChangedEventArgs>();
        _service.Changed += (_, args) => events.Add(args);
        var draft = CreateDraft("private source", historyEnabled: false);

        await _service.RecordSnapshotAsync(draft);

        events.Should().BeEmpty();
        (await _service.ListHistoryAsync(new HistoryListRequest())).Items.Should().BeEmpty();
        (await _service.GetQueryDetailAsync(draft.Id)).Should().BeNull();
    }

    [Fact]
    public async Task UserFavoriteFailure_IsReturnedToCaller()
    {
        var emptyDraft = new QuerySnapshotDraft(
            "source",
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            false);

        var toggle = () => _service.ToggleQueryFavoriteAsync(emptyDraft);

        await toggle.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CleanupLoop_UsesInjectedClockAndDisposeStopsTimer()
    {
        var path = Path.Combine(_directory, "cleanup.db");
        var store = new SavedItemsStore(path);
        await store.InitializeAsync();
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var draft = CreateDraft("expires after clock advance", historyEnabled: true, createdUtc: now);
        await store.UpsertTrackedSnapshotAsync(draft.Snapshot(), makeHistoryVisible: true);

        var timeProvider = new MutableTimeProvider(now);
        var service = new SavedItemsService(
            store,
            timeProvider,
            TimeSpan.FromMilliseconds(20),
            static () => 30);
        var secondCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;
        service.Changed += (_, args) =>
        {
            if (args.Kind == SavedItemsChangeKind.Cleanup &&
                Interlocked.Increment(ref cleanupCount) == 2)
            {
                secondCleanup.TrySetResult();
            }
        };

        await service.InitializeAsync();
        (await service.ListHistoryAsync(new HistoryListRequest())).Items.Should().ContainSingle();
        timeProvider.Advance(TimeSpan.FromDays(31));
        await secondCleanup.Task.WaitAsync(TimeSpan.FromSeconds(2));

        (await service.ListHistoryAsync(new HistoryListRequest())).Items.Should().BeEmpty();
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static QuerySnapshotDraft CreateDraft(
        string source,
        bool historyEnabled,
        DateTimeOffset? createdUtc = null)
    {
        var draft = new QuerySnapshotDraft(
            source,
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            historyEnabled,
            createdUtc);
        draft.TryAddTranslation("provider", "Provider", 0, new TranslationResult
        {
            OriginalText = source,
            TranslatedText = "result",
            ServiceName = "Provider"
        }).Should().BeTrue();
        return draft;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
}
