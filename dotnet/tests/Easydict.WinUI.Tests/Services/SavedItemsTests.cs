using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.SavedItems;
using Microsoft.Data.Sqlite;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class SavedResultPreviewTests
{
    [Fact]
    public void Create_CollapsesWhitespaceAndTruncatesByTextElement()
    {
        var value = string.Concat(Enumerable.Repeat("a", SavedResultPreview.MaxTextElements)) + "👩‍💻";

        SavedResultPreview.Create("  one\t two\r\nthree ").Should().Be("one two three");
        SavedResultPreview.Create(value).Should().Be(new string('a', SavedResultPreview.MaxTextElements) + "…");
    }

    [Fact]
    public void FromTranslation_PrefersDictionaryMeaning()
    {
        var result = new TranslationResult
        {
            OriginalText = "hello",
            TranslatedText = "translated",
            ServiceName = "Example",
            WordResult = new WordResult
            {
                Definitions = [new Definition { Meanings = ["primary definition"] }]
            }
        };

        SavedResultPreview.FromTranslation(result).Should().Be("primary definition");
    }

    [Fact]
    public void Create_HandlesTextElementBoundariesAndUnicodeWhitespace()
    {
        var emoji = "👩‍💻";
        var combiningAccent = "e\u0301";
        var exactlyOneHundred = string.Concat(Enumerable.Repeat(emoji, 98)) + combiningAccent + "𐐷";

        SavedResultPreview.Create(null).Should().BeEmpty();
        SavedResultPreview.Create("\r\n\t\u2003").Should().BeEmpty();
        SavedResultPreview.Create(new string('a', 99)).Should().Be(new string('a', 99));
        SavedResultPreview.Create(exactlyOneHundred).Should().Be(exactlyOneHundred);
        SavedResultPreview.Create(exactlyOneHundred + "x").Should().Be(exactlyOneHundred + "…");
        SavedResultPreview.Create("one\r\n\t\u2003two").Should().Be("one two");
    }

    [Fact]
    public void PreviewSources_UseTranslationAndCorrectedGrammarText()
    {
        var translation = new TranslationResult
        {
            OriginalText = "source",
            TranslatedText = "translated body",
            ServiceName = "Provider"
        };
        var grammar = new GrammarCorrectionResult
        {
            OriginalText = "bad grammar",
            CorrectedText = "corrected grammar",
            Explanation = "explanation",
            ServiceName = "Provider"
        };

        SavedResultPreview.FromTranslation(translation).Should().Be("translated body");
        SavedResultPreview.FromGrammar(grammar).Should().Be("corrected grammar");
    }
}

public sealed class QuerySnapshotDraftTests
{
    [Fact]
    public void Draft_StoresOnlySuccessfulNonemptyProviderResults()
    {
        var draft = new QuerySnapshotDraft("source", "en", "zh-CN", SavedQueryKind.Translation, QuerySourceKind.Manual, true);
        var success = new TranslationResult { OriginalText = "source", TranslatedText = "result", ServiceName = "Provider" };
        var empty = new TranslationResult { OriginalText = "source", TranslatedText = "", ServiceName = "Provider" };

        draft.TryAddTranslation("provider", "Provider", 1, success).Should().BeTrue();
        draft.TryAddTranslation("empty", "Empty", 2, empty).Should().BeFalse();

        var snapshot = draft.Snapshot();
        snapshot.Results.Should().ContainSingle();
        snapshot.Results[0].ProviderId.Should().Be("provider");
        snapshot.Results[0].PlainText.Should().Be("result");
    }

    [Fact]
    public void Classifier_EnforcesOcrGrammarTranslationAndLongDocumentRules()
    {
        SavedQueryClassifier.Classify(QueryMode.Translation, QuerySourceKind.Manual)
            .Should().Be(SavedQueryKind.Translation);
        SavedQueryClassifier.Classify(QueryMode.GrammarCorrection, QuerySourceKind.Manual)
            .Should().Be(SavedQueryKind.GrammarCorrection);
        SavedQueryClassifier.Classify(QueryMode.GrammarCorrection, QuerySourceKind.Ocr)
            .Should().Be(SavedQueryKind.Ocr);

        var classifyLongDocument = () => SavedQueryClassifier.Classify(QueryMode.LongDocument, QuerySourceKind.Ocr);
        classifyLongDocument.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Draft_UpsertsProviderAndPreservesDictionarySearchPayload()
    {
        var draft = new QuerySnapshotDraft(
            "word",
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            true);
        var first = new TranslationResult
        {
            OriginalText = "word",
            TranslatedText = "first",
            ServiceName = "Dictionary",
            RawHtml = "<p>rare phrase</p>",
            WordResult = new WordResult
            {
                Definitions = [new Definition { Meanings = ["primary meaning"] }],
                Examples = ["example sentence"]
            }
        };
        draft.TryAddTranslation("dictionary", "Dictionary", 4, first).Should().BeTrue();
        var originalId = draft.Snapshot().Results.Single().Id;

        draft.TryAddTranslation("dictionary", "Dictionary", 1, first with { TranslatedText = "updated" })
            .Should().BeTrue();
        var snapshot = draft.Snapshot();

        snapshot.Kind.Should().Be(SavedQueryKind.Translation);
        snapshot.Results.Should().ContainSingle();
        snapshot.Results[0].Id.Should().Be(originalId);
        snapshot.Results[0].DisplayOrder.Should().Be(1);
        snapshot.Results[0].PlainText.Should().Be("updated");
        snapshot.Results[0].PreviewText.Should().Be("primary meaning");
        snapshot.Results[0].SearchText.Should().Contain("rare phrase").And.Contain("example sentence");
    }

    [Fact]
    public void Draft_RejectsNoResultAndRetainsConcurrentSuccessfulProviders()
    {
        var draft = new QuerySnapshotDraft(
            "source",
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            true);
        var noResult = new TranslationResult
        {
            OriginalText = "source",
            TranslatedText = "not saved",
            ServiceName = "Missing",
            ResultKind = TranslationResultKind.NoResult
        };
        draft.TryAddTranslation("missing", "Missing", 0, noResult).Should().BeFalse();

        Parallel.For(0, 20, index =>
        {
            draft.TryAddTranslation(
                $"provider-{index}",
                $"Provider {index}",
                index,
                new TranslationResult
                {
                    OriginalText = "source",
                    TranslatedText = $"result-{index}",
                    ServiceName = $"Provider {index}"
                });
        });

        var snapshot = draft.Snapshot();
        snapshot.Results.Should().HaveCount(20);
        snapshot.Results.Select(result => result.DisplayOrder).Should().BeInAscendingOrder();
        snapshot.Results.Select(result => result.ProviderId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Draft_StoresGrammarPayloadAndSearchableExplanation()
    {
        var draft = new QuerySnapshotDraft(
            "he go",
            "en",
            "en",
            SavedQueryKind.GrammarCorrection,
            QuerySourceKind.Manual,
            true);
        var grammar = new GrammarCorrectionResult
        {
            OriginalText = "he go",
            CorrectedText = "he goes",
            Explanation = "third-person agreement",
            ServiceName = "Grammar"
        };

        draft.TryAddGrammar("grammar", "Grammar", 0, grammar).Should().BeTrue();

        var result = draft.Snapshot().Results.Should().ContainSingle().Subject;
        result.ContentType.Should().Be(SavedResultContentType.GrammarCorrection);
        result.PlainText.Should().Be("he goes");
        result.SearchText.Should().Contain("third-person agreement");
    }
}

public sealed class SavedItemsStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "easydict-saved-items-" + Guid.NewGuid());
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
    public async Task DisabledHistory_PersistsOnlyWhileFavoriteExists()
    {
        var draft = CreateTranslationDraft("hello", "provider", "Provider", "你好", historyEnabled: false);
        var snapshot = draft.Snapshot();

        await _store.UpsertTrackedSnapshotAsync(snapshot, makeHistoryVisible: false);
        (await _store.ListHistoryAsync(new HistoryListRequest())).Items.Should().BeEmpty();
        (await _store.GetQueryDetailAsync(snapshot.Id)).Should().BeNull();

        var toggle = await _store.AddQueryFavoriteAsync(snapshot);
        toggle.IsFavorited.Should().BeTrue();
        var favorites = await _store.ListFavoritesAsync(new FavoriteListRequest());
        favorites.Items.Should().ContainSingle();
        favorites.Items[0].SourceText.Should().Be("hello");

        var detail = await _store.GetQueryDetailAsync(snapshot.Id);
        detail.Should().NotBeNull();
        detail!.Results.Should().ContainSingle();
        detail.Results[0].PlainText.Should().Be("你好");

        var removed = await _store.ToggleStoredQueryFavoriteAsync(snapshot.Id);
        removed.IsFavorited.Should().BeFalse();
        (await _store.ListFavoritesAsync(new FavoriteListRequest())).Items.Should().BeEmpty();
        (await _store.GetQueryDetailAsync(snapshot.Id)).Should().BeNull();
    }

    [Fact]
    public async Task VisibleHistory_SupportsQueryAndResultFavoritesWithMetadata()
    {
        var draft = CreateTranslationDraft("hello", "provider", "Provider", "你好", historyEnabled: true);
        var snapshot = draft.Snapshot();
        await _store.UpsertTrackedSnapshotAsync(snapshot, makeHistoryVisible: true);

        var queryFavorite = await _store.ToggleStoredQueryFavoriteAsync(snapshot.Id);
        var resultFavorite = await _store.ToggleStoredResultFavoriteAsync(snapshot.Id, snapshot.Results[0].Id);

        queryFavorite.IsFavorited.Should().BeTrue();
        resultFavorite.IsFavorited.Should().BeTrue();
        resultFavorite.TargetKind.Should().Be(FavoriteTargetKind.Result);
        (await _store.ListFavoritesAsync(new FavoriteListRequest())).Items.Should().HaveCount(2);

        await _store.SetFavoritePinnedAsync(queryFavorite.FavoriteId, pinned: true);
        await _store.UpdateFavoriteMetadataAsync(queryFavorite.FavoriteId, "Useful translation", ["work", "Reference"]);
        var favoriteDetail = await _store.GetFavoriteDetailAsync(queryFavorite.FavoriteId);
        favoriteDetail.Should().NotBeNull();
        favoriteDetail!.Favorite.IsPinned.Should().BeTrue();
        favoriteDetail.Favorite.Note.Should().Be("Useful translation");
        favoriteDetail.Favorite.Tags.Should().BeEquivalentTo("work", "Reference");
    }

    [Fact]
    public async Task HistorySearch_TreatsLikeMetacharactersLiterally()
    {
        var draft = new QuerySnapshotDraft(@"C:\100%_complete", "en", "zh-CN", SavedQueryKind.Translation, QuerySourceKind.Manual, true);
        draft.TryAddTranslation("provider", "Provider", 0, new TranslationResult
        {
            OriginalText = @"C:\100%_complete",
            TranslatedText = "done",
            ServiceName = "Provider"
        });
        await _store.UpsertTrackedSnapshotAsync(draft.Snapshot(), makeHistoryVisible: true);

        var exact = await _store.ListHistoryAsync(new HistoryListRequest(@"C:\100%_complete"));
        var wildcard = await _store.ListHistoryAsync(new HistoryListRequest(@"C:\100Xcomplete"));
        var differentSlash = await _store.ListHistoryAsync(new HistoryListRequest("C:/100%_complete"));
        exact.Items.Should().ContainSingle();
        wildcard.Items.Should().BeEmpty();
        differentSlash.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFavoriteDetail_FindsFavoriteBeyondFirstPage()
    {
        Guid oldestFavoriteId = Guid.Empty;
        for (var index = 0; index < 51; index++)
        {
            var draft = new QuerySnapshotDraft($"source-{index}", "en", "zh-CN", SavedQueryKind.Translation, QuerySourceKind.Manual, false);
            draft.TryAddTranslation("provider", "Provider", 0, new TranslationResult
            {
                OriginalText = draft.SourceText,
                TranslatedText = $"result-{index}",
                ServiceName = "Provider"
            });
            var favorite = await _store.AddQueryFavoriteAsync(draft.Snapshot());
            if (index == 0)
                oldestFavoriteId = favorite.FavoriteId;
        }

        await _store.UpdateFavoriteMetadataAsync(oldestFavoriteId, "older favorite", ["archive"]);
        var firstPage = await _store.ListFavoritesAsync(new FavoriteListRequest(PageSize: 50));
        firstPage.Items.Should().HaveCount(50);
        firstPage.Items.Should().NotContain(item => item.Id == oldestFavoriteId);
        firstPage.NextCursor.Should().NotBeNull();
        var secondPage = await _store.ListFavoritesAsync(
            new FavoriteListRequest(Cursor: firstPage.NextCursor, PageSize: 50));
        secondPage.Items.Should().ContainSingle(item => item.Id == oldestFavoriteId);
        firstPage.Items.Concat(secondPage.Items).Select(item => item.Id)
            .Should().OnlyHaveUniqueItems().And.HaveCount(51);

        var detail = await _store.GetFavoriteDetailAsync(oldestFavoriteId);
        detail.Should().NotBeNull();
        detail!.Favorite.Note.Should().Be("older favorite");
        detail.Favorite.Tags.Should().Equal("archive");
    }


    [Fact]
    public async Task Initialize_CreatesVersionedSchemaAndRejectsFutureVersion()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "saved_items.db")}"))
        {
            await connection.OpenAsync();
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version";
            Convert.ToInt32(await versionCommand.ExecuteScalarAsync()).Should().Be(1);

            await using var foreignKeyCommand = connection.CreateCommand();
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_list(saved_results)";
            await using var reader = await foreignKeyCommand.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
        }

        var futurePath = Path.Combine(_directory, "future.db");
        await using (var connection = new SqliteConnection($"Data Source={futurePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2";
            await command.ExecuteNonQueryAsync();
        }

        var futureStore = new SavedItemsStore(futurePath);
        var initializeFuture = () => futureStore.InitializeAsync();
        await initializeFuture.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Payloads_RoundTripAndProviderUpsertDoesNotDuplicateRows()
    {
        var dictionaryDraft = new QuerySnapshotDraft(
            "word",
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            true);
        var dictionaryResult = new TranslationResult
        {
            OriginalText = "word",
            TranslatedText = "definition",
            ServiceName = "Dictionary",
            RawHtml = "<p>dictionary html</p>",
            WordResult = new WordResult
            {
                Definitions = [new Definition { Meanings = ["meaning"] }],
                Examples = ["usage example"]
            }
        };
        dictionaryDraft.TryAddTranslation("dictionary", "Dictionary", 0, dictionaryResult).Should().BeTrue();
        await _store.UpsertTrackedSnapshotAsync(dictionaryDraft.Snapshot(), makeHistoryVisible: true);
        dictionaryDraft.TryAddTranslation(
            "dictionary",
            "Dictionary",
            0,
            dictionaryResult with { TranslatedText = "updated definition" }).Should().BeTrue();
        await _store.UpsertTrackedSnapshotAsync(dictionaryDraft.Snapshot(), makeHistoryVisible: true);

        var dictionaryDetail = await _store.GetQueryDetailAsync(dictionaryDraft.Id);
        dictionaryDetail.Should().NotBeNull();
        var storedDictionary = dictionaryDetail!.Results.Should().ContainSingle().Subject;
        var deserializedDictionary = System.Text.Json.JsonSerializer.Deserialize<TranslationResult>(storedDictionary.PayloadJson);
        deserializedDictionary.Should().NotBeNull();
        deserializedDictionary!.TranslatedText.Should().Be("updated definition");
        deserializedDictionary.RawHtml.Should().Be("<p>dictionary html</p>");
        deserializedDictionary.WordResult!.Definitions![0].Meanings.Should().ContainSingle("meaning");

        var grammarDraft = new QuerySnapshotDraft(
            "he go",
            "en",
            "en",
            SavedQueryKind.GrammarCorrection,
            QuerySourceKind.Manual,
            true);
        grammarDraft.TryAddGrammar("grammar", "Grammar", 0, new GrammarCorrectionResult
        {
            OriginalText = "he go",
            CorrectedText = "he goes",
            Explanation = "agreement",
            ServiceName = "Grammar"
        }).Should().BeTrue();
        await _store.UpsertTrackedSnapshotAsync(grammarDraft.Snapshot(), makeHistoryVisible: true);

        var grammarDetail = await _store.GetQueryDetailAsync(grammarDraft.Id);
        var storedGrammar = grammarDetail!.Results.Should().ContainSingle().Subject;
        System.Text.Json.JsonSerializer.Deserialize<GrammarCorrectionResult>(storedGrammar.PayloadJson)!
            .Explanation.Should().Be("agreement");
    }

    [Fact]
    public async Task HistoryCursor_IsStableAtEqualTimestampsWithoutDuplicates()
    {
        var createdUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        for (var index = 0; index < 51; index++)
        {
            var draft = CreateTranslationDraft(
                $"source-{index}",
                "provider",
                "Provider",
                $"result-{index}",
                historyEnabled: true,
                createdUtc);
            await _store.UpsertTrackedSnapshotAsync(draft.Snapshot(), makeHistoryVisible: true);
        }

        var first = await _store.ListHistoryAsync(new HistoryListRequest(PageSize: 50));
        var second = await _store.ListHistoryAsync(new HistoryListRequest(Cursor: first.NextCursor, PageSize: 50));

        first.Items.Should().HaveCount(50);
        first.NextCursor.Should().NotBeNull();
        second.Items.Should().ContainSingle();
        first.Items.Concat(second.Items).Select(item => item.Id).Should().OnlyHaveUniqueItems().And.HaveCount(51);
    }

    [Fact]
    public async Task HistorySearch_RanksMatchesAndUsesMatchingProviderPreview()
    {
        var exact = CreateTranslationDraft("needle", "primary", "Primary", "default exact", true);
        var prefix = CreateTranslationDraft("needle suffix", "primary", "Primary", "default prefix", true);
        var contains = CreateTranslationDraft("unrelated source", "primary", "Primary", "default contains", true);
        contains.TryAddTranslation("secondary", "Secondary", 1, new TranslationResult
        {
            OriginalText = contains.SourceText,
            TranslatedText = "hay needle stack",
            ServiceName = "Secondary"
        }).Should().BeTrue();
        var unicode = CreateTranslationDraft("Straße", "unicode", "Unicode", "street", true);

        foreach (var draft in new[] { exact, prefix, contains, unicode })
            await _store.UpsertTrackedSnapshotAsync(draft.Snapshot(), makeHistoryVisible: true);

        var matches = await _store.ListHistoryAsync(new HistoryListRequest("needle"));
        matches.Items.Select(item => item.RelevanceRank).Should().Equal(1, 2, 3);
        matches.Items[0].PreviewProviderId.Should().Be("primary", "a source-text hit keeps the default preview");
        matches.Items[2].PreviewProviderId.Should().Be("secondary", "a result-only hit previews the matching provider");
        matches.Items[2].PreviewText.Should().Be("hay needle stack");

        var providerMatches = await _store.ListHistoryAsync(new HistoryListRequest(ProviderId: "secondary"));
        providerMatches.Items.Should().ContainSingle(item => item.Id == contains.Id);
        providerMatches.Items.Single(item => item.Id == contains.Id).PreviewProviderId.Should().Be("secondary",
            "a provider filter must always preview that provider");
        (await _store.ListHistoryAsync(new HistoryListRequest("straße"))).Items
            .Should().ContainSingle(item => item.Id == unicode.Id);
    }

    [Fact]
    public async Task Favorites_FilterTagsWithOrSemanticsAndKeepResultPreviewFixed()
    {
        var work = CreateTranslationDraft("work source", "primary", "Primary", "work result", false);
        var personal = CreateTranslationDraft("personal source", "primary", "Primary", "personal result", false);
        var archive = CreateTranslationDraft("archive source", "primary", "Primary", "archive result", false);
        var workFavorite = await _store.AddQueryFavoriteAsync(work.Snapshot());
        var personalFavorite = await _store.AddQueryFavoriteAsync(personal.Snapshot());
        var archiveFavorite = await _store.AddQueryFavoriteAsync(archive.Snapshot());
        await _store.UpdateFavoriteMetadataAsync(workFavorite.FavoriteId, "work note", ["Work", "work"]);
        await _store.UpdateFavoriteMetadataAsync(personalFavorite.FavoriteId, "", ["Personal"]);
        await _store.UpdateFavoriteMetadataAsync(archiveFavorite.FavoriteId, "", ["Archive"]);
        await _store.SetFavoritePinnedAsync(personalFavorite.FavoriteId, pinned: true);

        var filtered = await _store.ListFavoritesAsync(new FavoriteListRequest(Tags: ["work", "PERSONAL"]));
        filtered.Items.Select(item => item.Id).Should().BeEquivalentTo(
            [workFavorite.FavoriteId, personalFavorite.FavoriteId]);
        filtered.Items.Single(item => item.Id == workFavorite.FavoriteId).Tags.Should().ContainSingle().Which.Should().Be("Work");
        (await _store.ListFavoritesAsync(new FavoriteListRequest())).Items[0].Id.Should().Be(personalFavorite.FavoriteId);

        var resultDraft = CreateTranslationDraft("multi", "primary", "Primary", "primary result", false);
        resultDraft.TryAddTranslation("secondary", "Secondary", 1, new TranslationResult
        {
            OriginalText = "multi",
            TranslatedText = "secondary result",
            ServiceName = "Secondary"
        }).Should().BeTrue();
        var resultSnapshot = resultDraft.Snapshot();
        var targetResult = resultSnapshot.Results.Single(result => result.ProviderId == "secondary");
        var resultFavorite = await _store.AddResultFavoriteAsync(resultSnapshot, targetResult.Id);
        var storedResultFavorite = (await _store.ListFavoritesAsync(
            new FavoriteListRequest(TargetKind: FavoriteTargetKind.Result))).Items
            .Single(item => item.Id == resultFavorite.FavoriteId);
        storedResultFavorite.ProviderId.Should().Be("secondary");
        storedResultFavorite.PreviewText.Should().Be("secondary result");

        var addForeignResult = () => _store.AddResultFavoriteAsync(resultSnapshot, Guid.NewGuid());
        await addForeignResult.Should().ThrowAsync<ArgumentException>();
        var toggleForeignResult = () => _store.ToggleStoredResultFavoriteAsync(work.Id, targetResult.Id);
        await toggleForeignResult.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PruneAndClear_PreserveFavoritesAndSiblingResultsUntilLastFavoriteIsRemoved()
    {
        var now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var expired = CreateTranslationDraft("expired", "primary", "Primary", "first", true, now.AddDays(-31));
        expired.TryAddTranslation("secondary", "Secondary", 1, new TranslationResult
        {
            OriginalText = "expired",
            TranslatedText = "second",
            ServiceName = "Secondary"
        }).Should().BeTrue();
        var cutoff = CreateTranslationDraft("cutoff", "primary", "Primary", "cutoff", true, now.AddDays(-30));
        var recent = CreateTranslationDraft("recent", "primary", "Primary", "recent", true, now.AddDays(-1));
        foreach (var draft in new[] { expired, cutoff, recent })
            await _store.UpsertTrackedSnapshotAsync(draft.Snapshot(), makeHistoryVisible: true);

        var expiredSnapshot = expired.Snapshot();
        var resultFavorite = await _store.ToggleStoredResultFavoriteAsync(
            expired.Id,
            expiredSnapshot.Results.Single(result => result.ProviderId == "secondary").Id);
        await _store.PruneExpiredHistoryAsync(30, now);

        var history = await _store.ListHistoryAsync(new HistoryListRequest());
        history.Items.Select(item => item.SourceText).Should().BeEquivalentTo("cutoff", "recent");
        var preserved = await _store.GetQueryDetailAsync(expired.Id);
        preserved.Should().NotBeNull();
        preserved!.Results.Should().HaveCount(2);

        await _store.ClearHistoryAsync();
        (await _store.ListHistoryAsync(new HistoryListRequest())).Items.Should().BeEmpty();
        (await _store.GetQueryDetailAsync(expired.Id))!.Results.Should().HaveCount(2);

        await _store.RemoveFavoriteAsync(resultFavorite.FavoriteId, retentionDays: 30);
        (await _store.GetQueryDetailAsync(expired.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CorruptPayload_IsReturnedWithoutDeletingStoredResult()
    {
        var resultId = Guid.NewGuid();
        var snapshot = new QuerySnapshot(
            Guid.NewGuid(),
            "source",
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            DateTimeOffset.UtcNow,
            [new SavedProviderResultSnapshot(
                resultId,
                "provider",
                "Provider",
                0,
                SavedResultContentType.Translation,
                "plain text",
                "plain text",
                "{not-json",
                "plain text",
                1,
                DateTimeOffset.UtcNow)]);

        await _store.UpsertTrackedSnapshotAsync(snapshot, makeHistoryVisible: true);

        var detail = await _store.GetQueryDetailAsync(snapshot.Id);
        detail.Should().NotBeNull();
        detail!.Results.Should().ContainSingle();
        detail.Results[0].Id.Should().Be(resultId);
        detail.Results[0].PayloadJson.Should().Be("{not-json");
    }
    private static QuerySnapshotDraft CreateTranslationDraft(
        string sourceText,
        string providerId,
        string providerName,
        string translatedText,
        bool historyEnabled,
        DateTimeOffset? createdUtc = null,
        int displayOrder = 0)
    {
        var draft = new QuerySnapshotDraft(
            sourceText,
            "en",
            "zh-CN",
            SavedQueryKind.Translation,
            QuerySourceKind.Manual,
            historyEnabled,
            createdUtc);
        draft.TryAddTranslation(providerId, providerName, displayOrder, new TranslationResult
        {
            OriginalText = sourceText,
            TranslatedText = translatedText,
            ServiceName = providerName
        }).Should().BeTrue();
        return draft;
    }
}
