using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.SavedItems;
using Microsoft.Data.Sqlite;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Guards the SavedItemsStore mapping of the hover word lookup source kind
/// (ToDb / ParseSourceKind are exhaustive switches that throw on unknown values).
/// </summary>
public sealed class SavedItemsHoverSourceKindTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "easydict-saved-items-hover-" + Guid.NewGuid());
    private SavedItemsStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SavedItemsStore(Path.Combine(_directory, "saved_items.db"));
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void Classify_HoverSource_IsATranslationQuery()
    {
        SavedQueryClassifier.Classify(QueryMode.Translation, QuerySourceKind.Hover)
            .Should().Be(SavedQueryKind.Translation);
        SavedQueryClassifier.Classify(QueryMode.GrammarCorrection, QuerySourceKind.Hover)
            .Should().Be(SavedQueryKind.GrammarCorrection);
    }

    [Fact]
    public async Task HoverSourceKind_RoundTripsThroughTheStore()
    {
        var draft = new QuerySnapshotDraft("hello", "en", "zh-CN", SavedQueryKind.Translation, QuerySourceKind.Hover, historyEnabled: true);
        draft.TryAddTranslation("provider", "Provider", 0, new TranslationResult
        {
            OriginalText = "hello",
            TranslatedText = "你好",
            ServiceName = "Provider"
        }).Should().BeTrue();
        var snapshot = draft.Snapshot();
        snapshot.SourceKind.Should().Be(QuerySourceKind.Hover);

        await _store.UpsertTrackedSnapshotAsync(snapshot, makeHistoryVisible: true);

        var detail = await _store.GetQueryDetailAsync(snapshot.Id);
        detail.Should().NotBeNull();
        detail!.Results.Should().ContainSingle();

        var history = await _store.ListHistoryAsync(new HistoryListRequest());
        history.Items.Should().ContainSingle();
        history.Items[0].SourceText.Should().Be("hello");
    }
}
