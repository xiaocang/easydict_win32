using System.Text;
using Easydict.WinUI.Models;
using Microsoft.Data.Sqlite;

namespace Easydict.WinUI.Services.SavedItems;

/// <summary>
/// Durable local storage for query history and favorites. Connections are intentionally short-lived
/// so Main, Mini, and Fixed windows share one database without UI-thread affinity.
/// </summary>
public sealed class SavedItemsStore
{
    private const int SchemaVersion = 1;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public SavedItemsStore()
    {
        var directory = SettingsService.ResolveSettingsDirectory();
        Directory.CreateDirectory(directory);
        _dbPath = Path.Combine(directory, "saved_items.db");
    }

    internal SavedItemsStore(string dbPath) => _dbPath = dbPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var version = await ExecuteScalarIntAsync(connection, "PRAGMA user_version", cancellationToken).ConfigureAwait(false);
            if (version > SchemaVersion)
                throw new InvalidOperationException($"saved_items.db schema version {version} is newer than supported version {SchemaVersion}.");

            if (version == 0)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, Schema, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {SchemaVersion}", cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<bool> UpsertTrackedSnapshotAsync(QuerySnapshot snapshot, bool makeHistoryVisible, CancellationToken cancellationToken = default)
    {
        if (snapshot.Results.Count == 0)
            return false;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!makeHistoryVisible && !await HasAnyFavoriteAsync(connection, transaction, snapshot.Id, cancellationToken).ConfigureAwait(false))
            return false;

        await UpsertSnapshotAsync(connection, transaction, snapshot, makeHistoryVisible, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<FavoriteToggleResult> AddQueryFavoriteAsync(
        QuerySnapshot snapshot,
        CancellationToken cancellationToken = default,
        int retentionDays = 30)
    {
        if (snapshot.Results.Count == 0)
            throw new InvalidOperationException("A query must contain a successful result before it can be favorited.");

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSnapshotAsync(connection, transaction, snapshot, makeHistoryVisible: false, cancellationToken).ConfigureAwait(false);

        var existingId = await GetFavoriteIdAsync(connection, transaction, snapshot.Id, null, cancellationToken).ConfigureAwait(false);
        if (existingId is { } favoriteId)
        {
            await DeleteFavoriteAsync(connection, transaction, favoriteId, cancellationToken).ConfigureAwait(false);
            await DeleteHiddenOrExpiredUnfavoritedQueryAsync(
                connection,
                transaction,
                snapshot.Id,
                retentionDays,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FavoriteToggleResult(favoriteId, false, FavoriteTargetKind.Query);
        }

        var createdId = Guid.NewGuid();
        await InsertFavoriteAsync(connection, transaction, createdId, snapshot.Id, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FavoriteToggleResult(createdId, true, FavoriteTargetKind.Query);
    }

    public async Task<FavoriteToggleResult> ToggleStoredQueryFavoriteAsync(
        Guid queryId,
        CancellationToken cancellationToken = default,
        int retentionDays = 30)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM saved_queries WHERE id = @id", cancellationToken, transaction, ("@id", queryId.ToString())).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"The saved query '{queryId}' does not exist.");

        var existingId = await GetFavoriteIdAsync(connection, transaction, queryId, null, cancellationToken).ConfigureAwait(false);
        if (existingId is { } favoriteId)
        {
            await DeleteFavoriteAsync(connection, transaction, favoriteId, cancellationToken).ConfigureAwait(false);
            await DeleteHiddenOrExpiredUnfavoritedQueryAsync(
                connection,
                transaction,
                queryId,
                retentionDays,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FavoriteToggleResult(favoriteId, false, FavoriteTargetKind.Query);
        }

        var createdId = Guid.NewGuid();
        await InsertFavoriteAsync(connection, transaction, createdId, queryId, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FavoriteToggleResult(createdId, true, FavoriteTargetKind.Query);
    }

    public async Task<FavoriteToggleResult> ToggleStoredResultFavoriteAsync(
        Guid queryId,
        Guid resultId,
        CancellationToken cancellationToken = default,
        int retentionDays = 30)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM saved_results WHERE id = @result AND query_id = @query", cancellationToken, transaction,
                ("@result", resultId.ToString()), ("@query", queryId.ToString())).ConfigureAwait(false) == 0)
            throw new ArgumentException("The result does not belong to the saved query.", nameof(resultId));

        var existingId = await GetFavoriteIdAsync(connection, transaction, queryId, resultId, cancellationToken).ConfigureAwait(false);
        if (existingId is { } favoriteId)
        {
            await DeleteFavoriteAsync(connection, transaction, favoriteId, cancellationToken).ConfigureAwait(false);
            await DeleteHiddenOrExpiredUnfavoritedQueryAsync(
                connection,
                transaction,
                queryId,
                retentionDays,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FavoriteToggleResult(favoriteId, false, FavoriteTargetKind.Result);
        }

        var createdId = Guid.NewGuid();
        await InsertFavoriteAsync(connection, transaction, createdId, queryId, resultId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FavoriteToggleResult(createdId, true, FavoriteTargetKind.Result);
    }

    public async Task<FavoriteToggleResult> AddResultFavoriteAsync(
        QuerySnapshot snapshot,
        Guid resultId,
        CancellationToken cancellationToken = default,
        int retentionDays = 30)
    {
        if (!snapshot.Results.Any(result => result.Id == resultId))
            throw new ArgumentException("The result does not belong to the supplied query snapshot.", nameof(resultId));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSnapshotAsync(connection, transaction, snapshot, makeHistoryVisible: false, cancellationToken).ConfigureAwait(false);

        var existingId = await GetFavoriteIdAsync(connection, transaction, snapshot.Id, resultId, cancellationToken).ConfigureAwait(false);
        if (existingId is { } favoriteId)
        {
            await DeleteFavoriteAsync(connection, transaction, favoriteId, cancellationToken).ConfigureAwait(false);
            await DeleteHiddenOrExpiredUnfavoritedQueryAsync(
                connection,
                transaction,
                snapshot.Id,
                retentionDays,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FavoriteToggleResult(favoriteId, false, FavoriteTargetKind.Result);
        }

        var createdId = Guid.NewGuid();
        await InsertFavoriteAsync(connection, transaction, createdId, snapshot.Id, resultId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FavoriteToggleResult(createdId, true, FavoriteTargetKind.Result);
    }

    public async Task RemoveFavoriteAsync(Guid favoriteId, int retentionDays, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var queryId = await GetFavoriteQueryIdAsync(connection, transaction, favoriteId, cancellationToken).ConfigureAwait(false);
        if (queryId is null)
            return;

        await DeleteFavoriteAsync(connection, transaction, favoriteId, cancellationToken).ConfigureAwait(false);
        await DeleteHiddenOrExpiredUnfavoritedQueryAsync(connection, transaction, queryId.Value, retentionDays, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetFavoritePinnedAsync(Guid favoriteId, bool pinned, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "UPDATE favorites SET pinned = @pinned, updated_utc = @updated WHERE id = @id", cancellationToken,
            ("@pinned", pinned ? 1 : 0), ("@updated", FormatUtc(DateTimeOffset.UtcNow)), ("@id", favoriteId.ToString())).ConfigureAwait(false);
    }

    public async Task UpdateFavoriteMetadataAsync(Guid favoriteId, string note, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        if (TextElementCount(note) > 2000)
            throw new ArgumentOutOfRangeException(nameof(note), "Notes are limited to 2,000 text elements.");

        var normalizedTags = tags.Select(static tag => tag.Trim())
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTags.Length > 20 || normalizedTags.Any(static tag => TextElementCount(tag) > 40))
            throw new ArgumentOutOfRangeException(nameof(tags), "Favorites support at most 20 tags of 40 text elements each.");

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE favorites SET note = @note, note_search_text = @search, updated_utc = @updated WHERE id = @id", cancellationToken,
            ("@note", note), ("@search", SavedItemsSearch.Normalize(note)), ("@updated", FormatUtc(DateTimeOffset.UtcNow)), ("@id", favoriteId.ToString())).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM favorite_tags WHERE favorite_id = @id", cancellationToken, ("@id", favoriteId.ToString())).ConfigureAwait(false);
        foreach (var tag in normalizedTags)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO favorite_tags (favorite_id, tag, tag_search_text) VALUES (@id, @tag, @search)", cancellationToken,
                ("@id", favoriteId.ToString()), ("@tag", tag), ("@search", SavedItemsSearch.Normalize(tag))).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FavoriteStateMap> GetFavoriteStatesAsync(Guid queryId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_id FROM favorites WHERE query_id = @query";
        command.Parameters.AddWithValue("@query", queryId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var queryFavorited = false;
        var resultIds = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
                queryFavorited = true;
            else if (Guid.TryParse(reader.GetString(0), out var id))
                resultIds.Add(id);
        }
        return new FavoriteStateMap(queryFavorited, resultIds);
    }

    public async Task<SavedItemsPageResult<SavedQueryListItem>> ListHistoryAsync(HistoryListRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var query = SavedItemsSearch.Normalize(request.SearchText);
        var sql = new StringBuilder("""
            WITH matching_results AS (
              SELECT r.*,
                ROW_NUMBER() OVER (
                  PARTITION BY r.query_id
                  ORDER BY
                    CASE
                      WHEN r.provider_search_text = @search OR r.plain_search_text = @search THEN 1
                      WHEN r.provider_search_text LIKE @prefix ESCAPE '\' OR r.plain_search_text LIKE @prefix ESCAPE '\' THEN 2
                      ELSE 3
                    END,
                    r.display_order,
                    r.id
                ) AS match_order
              FROM saved_results r
              WHERE @search <> '' AND r.search_text LIKE @contains ESCAPE '\'
            ),
            ranked AS (
              SELECT q.*,
                CASE
                  WHEN @search = '' THEN 0
                  WHEN q.source_search_text = @search OR EXISTS (SELECT 1 FROM saved_results r WHERE r.query_id = q.id AND (r.provider_search_text = @search OR r.plain_search_text = @search)) THEN 1
                  WHEN q.source_search_text LIKE @prefix ESCAPE '\' OR EXISTS (SELECT 1 FROM saved_results r WHERE r.query_id = q.id AND (r.provider_search_text LIKE @prefix ESCAPE '\' OR r.plain_search_text LIKE @prefix ESCAPE '\')) THEN 2
                  ELSE 3
                END AS relevance_rank
              FROM saved_queries q
              WHERE q.history_visible = 1
                AND (@search = '' OR q.source_search_text LIKE @contains ESCAPE '\' OR EXISTS (SELECT 1 FROM saved_results r WHERE r.query_id = q.id AND r.search_text LIKE @contains ESCAPE '\'))
                AND (@kind = '' OR q.mode = @kind)
                AND (@provider = '' OR EXISTS (SELECT 1 FROM saved_results r WHERE r.query_id = q.id AND r.provider_id = @provider))
                AND (@start = '' OR q.created_utc >= @start)
                AND (@end = '' OR q.created_utc < @end)
            )
            SELECT q.id, q.mode, q.source_text, q.source_language, q.target_language,
                   q.source_kind, q.created_utc, q.success_result_count, q.relevance_rank,
                   CASE WHEN @provider <> '' THEN selected_provider.provider_id WHEN @search = '' OR q.source_search_text LIKE @contains ESCAPE '\' OR mr.id IS NULL THEN q.preview_provider_id ELSE mr.provider_id END AS preview_provider_id,
                   CASE WHEN @provider <> '' THEN selected_provider.provider_name WHEN @search = '' OR q.source_search_text LIKE @contains ESCAPE '\' OR mr.id IS NULL THEN q.preview_provider_name ELSE mr.provider_name END AS preview_provider_name,
                   CASE WHEN @provider <> '' THEN selected_provider.preview_text WHEN @search = '' OR q.source_search_text LIKE @contains ESCAPE '\' OR mr.id IS NULL THEN q.preview_text ELSE mr.preview_text END AS preview_text
            FROM ranked q
            LEFT JOIN matching_results mr ON mr.query_id = q.id AND mr.match_order = 1
            LEFT JOIN saved_results selected_provider ON selected_provider.query_id = q.id AND selected_provider.provider_id = @provider
            WHERE (@cursorId = '' OR q.relevance_rank > @cursorRank OR (q.relevance_rank = @cursorRank AND (q.created_utc < @cursorCreated OR (q.created_utc = @cursorCreated AND q.id < @cursorId))))
            ORDER BY q.relevance_rank, q.created_utc DESC, q.id DESC
            LIMIT @take
            """);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        AddHistoryParameters(command, request, query);
        command.Parameters.AddWithValue("@take", Math.Clamp(request.PageSize, 1, 50) + 1);
        var items = new List<SavedQueryListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(ReadQueryListItem(reader));

        return Page(items, request.PageSize);
    }

    public async Task<SavedQueryDetail?> GetQueryDetailAsync(Guid queryId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT *, 0 AS relevance_rank FROM saved_queries WHERE id = @id";
        command.Parameters.AddWithValue("@id", queryId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var query = ReadQueryListItem(reader);
        await reader.DisposeAsync().ConfigureAwait(false);

        var results = new List<SavedQueryResultDetail>();
        await using var resultsCommand = connection.CreateCommand();
        resultsCommand.CommandText = "SELECT * FROM saved_results WHERE query_id = @id ORDER BY display_order";
        resultsCommand.Parameters.AddWithValue("@id", queryId.ToString());
        await using var resultsReader = await resultsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await resultsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadResultDetail(resultsReader));
        var states = await GetFavoriteStatesAsync(queryId, cancellationToken).ConfigureAwait(false);
        return new SavedQueryDetail(query, results, states.IsQueryFavorited);
    }

    public async Task DeleteHistoryAsync(Guid queryId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE saved_queries SET history_visible = 0 WHERE id = @id", cancellationToken, ("@id", queryId.ToString())).ConfigureAwait(false);
        await DeleteUnfavoritedHiddenQueriesAsync(connection, transaction, "AND id = @id", cancellationToken, ("@id", queryId.ToString())).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE saved_queries SET history_visible = 0 WHERE history_visible = 1", cancellationToken).ConfigureAwait(false);
        await DeleteUnfavoritedHiddenQueriesAsync(connection, transaction, string.Empty, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedItemsPageResult<FavoriteListItem>> ListFavoritesAsync(FavoriteListRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.*, q.source_text, q.source_language, q.target_language, q.mode, q.success_result_count,
                   COALESCE(r.provider_id, q.preview_provider_id) AS provider_id,
                   COALESCE(r.provider_name, q.preview_provider_name) AS provider_name,
                   COALESCE(r.preview_text, q.preview_text) AS preview_text
            FROM favorites f
            JOIN saved_queries q ON q.id = f.query_id
            LEFT JOIN saved_results r ON r.id = f.result_id
            WHERE (
                    @search = ''
                    OR q.source_search_text LIKE @contains ESCAPE '\'
                    OR (f.result_id IS NULL AND EXISTS (
                        SELECT 1 FROM saved_results search_result
                        WHERE search_result.query_id = q.id AND search_result.search_text LIKE @contains ESCAPE '\'))
                    OR (f.result_id IS NOT NULL AND r.search_text LIKE @contains ESCAPE '\')
                    OR f.note_search_text LIKE @contains ESCAPE '\'
                    OR EXISTS (
                        SELECT 1 FROM favorite_tags search_tag
                        WHERE search_tag.favorite_id = f.id AND search_tag.tag_search_text LIKE @contains ESCAPE '\')
                  )
              AND (@target = '' OR (@target = 'query' AND f.result_id IS NULL) OR (@target = 'result' AND f.result_id IS NOT NULL))
              AND (@pinned = 0 OR f.pinned = 1)
              AND (@tagCount = 0 OR EXISTS (
                    SELECT 1 FROM favorite_tags filter_tag
                    WHERE filter_tag.favorite_id = f.id
                      AND filter_tag.tag_search_text IN (
                        @tag0, @tag1, @tag2, @tag3, @tag4, @tag5, @tag6, @tag7, @tag8, @tag9,
                        @tag10, @tag11, @tag12, @tag13, @tag14, @tag15, @tag16, @tag17, @tag18, @tag19)))
              AND (@cursorId = '' OR f.pinned < @cursorPinned OR (f.pinned = @cursorPinned AND (f.created_utc < @cursorCreated OR (f.created_utc = @cursorCreated AND f.id < @cursorId))))
            ORDER BY f.pinned DESC, f.created_utc DESC, f.id DESC
            LIMIT @take
            """;
        var search = SavedItemsSearch.Normalize(request.SearchText);
        command.Parameters.AddWithValue("@search", search);
        command.Parameters.AddWithValue("@contains", $"%{SavedItemsSearch.EscapeLike(search)}%");
        command.Parameters.AddWithValue("@target", request.TargetKind switch { FavoriteTargetKind.Query => "query", FavoriteTargetKind.Result => "result", _ => "" });
        command.Parameters.AddWithValue("@pinned", request.PinnedOnly ? 1 : 0);
        command.Parameters.AddWithValue("@cursorId", request.Cursor?.Id.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@cursorPinned", request.Cursor?.IsPinned == true ? 1 : 0);
        command.Parameters.AddWithValue("@cursorCreated", request.Cursor is { } cursor ? FormatUtc(cursor.CreatedUtc) : string.Empty);
        command.Parameters.AddWithValue("@take", Math.Clamp(request.PageSize, 1, 50) + 1);
        var tags = (request.Tags ?? [])
            .Select(SavedItemsSearch.Normalize)
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        command.Parameters.AddWithValue("@tagCount", tags.Length);
        for (var index = 0; index < 20; index++)
            command.Parameters.AddWithValue($"@tag{index}", index < tags.Length ? tags[index] : string.Empty);

        var items = new List<FavoriteListItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(ReadFavoriteListItem(reader));
        }
        for (var index = 0; index < items.Count; index++)
            items[index] = items[index] with { Tags = await GetTagsAsync(connection, items[index].Id, cancellationToken).ConfigureAwait(false) };

        return Page(items, request.PageSize);
    }

    public async Task<FavoriteDetail?> GetFavoriteDetailAsync(Guid favoriteId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.*, q.source_text, q.source_language, q.target_language, q.mode, q.success_result_count,
                   COALESCE(r.provider_id, q.preview_provider_id) AS provider_id,
                   COALESCE(r.provider_name, q.preview_provider_name) AS provider_name,
                   COALESCE(r.preview_text, q.preview_text) AS preview_text
            FROM favorites f
            JOIN saved_queries q ON q.id = f.query_id
            LEFT JOIN saved_results r ON r.id = f.result_id
            WHERE f.id = @id
            """;
        command.Parameters.AddWithValue("@id", favoriteId.ToString());
        FavoriteListItem item;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            item = ReadFavoriteListItem(reader);
        }
        item = item with { Tags = await GetTagsAsync(connection, item.Id, cancellationToken).ConfigureAwait(false) };
        var detail = await GetQueryDetailAsync(item.QueryId, cancellationToken).ConfigureAwait(false);
        return detail is null ? null : new FavoriteDetail(item, detail);
    }

    public async Task<SavedItemsFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var providers = new List<(string, string)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT provider_id, MIN(provider_name) FROM saved_results GROUP BY provider_id ORDER BY MIN(provider_name)";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                providers.Add((reader.GetString(0), reader.GetString(1)));
        }
        var tags = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MIN(tag) FROM favorite_tags GROUP BY tag_search_text ORDER BY MIN(tag)";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tags.Add(reader.GetString(0));
        }
        return new SavedItemsFilterOptions(providers, tags);
    }

    public async Task PruneExpiredHistoryAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 3650);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = FormatUtc(now.AddDays(-retentionDays));
        await ExecuteAsync(connection, transaction, "UPDATE saved_queries SET history_visible = 0 WHERE history_visible = 1 AND created_utc < @cutoff", cancellationToken, ("@cutoff", cutoff)).ConfigureAwait(false);
        await DeleteUnfavoritedHiddenQueriesAsync(connection, transaction, string.Empty, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, QuerySnapshot snapshot, bool makeHistoryVisible, CancellationToken cancellationToken)
    {
        if (snapshot.Results.Count == 0)
            return;
        var first = snapshot.Results.OrderBy(static result => result.DisplayOrder).First();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO saved_queries (id, mode, source_text, source_search_text, source_language, target_language, source_kind, created_utc, history_visible, preview_provider_id, preview_provider_name, preview_text, success_result_count)
            VALUES (@id, @mode, @source, @sourceSearch, @sourceLanguage, @targetLanguage, @sourceKind, @created, @visible, @previewProviderId, @previewProviderName, @previewText, @count)
            ON CONFLICT(id) DO UPDATE SET
              history_visible = CASE WHEN saved_queries.history_visible = 1 OR excluded.history_visible = 1 THEN 1 ELSE 0 END,
              preview_provider_id = excluded.preview_provider_id,
              preview_provider_name = excluded.preview_provider_name,
              preview_text = excluded.preview_text,
              success_result_count = excluded.success_result_count
            """, cancellationToken,
            ("@id", snapshot.Id.ToString()), ("@mode", ToDb(snapshot.Kind)), ("@source", snapshot.SourceText), ("@sourceSearch", SavedItemsSearch.Normalize(snapshot.SourceText)),
            ("@sourceLanguage", snapshot.SourceLanguage), ("@targetLanguage", snapshot.TargetLanguage), ("@sourceKind", ToDb(snapshot.SourceKind)), ("@created", FormatUtc(snapshot.CreatedUtc)),
            ("@visible", makeHistoryVisible ? 1 : 0), ("@previewProviderId", first.ProviderId), ("@previewProviderName", first.ProviderName), ("@previewText", first.PreviewText), ("@count", snapshot.Results.Count)).ConfigureAwait(false);
        foreach (var result in snapshot.Results)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO saved_results (id, query_id, provider_id, provider_name, provider_search_text, display_order, content_type, plain_text, plain_search_text, preview_text, payload_json, search_text, latency_ms, created_utc)
                VALUES (@id, @query, @providerId, @providerName, @providerSearch, @order, @contentType, @plain, @plainSearch, @preview, @payload, @search, @latency, @created)
                ON CONFLICT(query_id, provider_id) DO UPDATE SET
                  id = excluded.id, provider_name = excluded.provider_name, display_order = excluded.display_order, content_type = excluded.content_type,
                  plain_text = excluded.plain_text, plain_search_text = excluded.plain_search_text, preview_text = excluded.preview_text, payload_json = excluded.payload_json,
                  search_text = excluded.search_text, latency_ms = excluded.latency_ms
                """, cancellationToken,
                ("@id", result.Id.ToString()), ("@query", snapshot.Id.ToString()), ("@providerId", result.ProviderId), ("@providerName", result.ProviderName), ("@providerSearch", SavedItemsSearch.Normalize(result.ProviderName)),
                ("@order", result.DisplayOrder), ("@contentType", ToDb(result.ContentType)), ("@plain", result.PlainText), ("@plainSearch", SavedItemsSearch.Normalize(result.PlainText)),
                ("@preview", result.PreviewText), ("@payload", result.PayloadJson), ("@search", SavedItemsSearch.Normalize($"{result.ProviderName} {result.SearchText}")),
                ("@latency", result.LatencyMs), ("@created", FormatUtc(result.CreatedUtc))).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = true, Mode = SqliteOpenMode.ReadWriteCreate };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasAnyFavoriteAsync(SqliteConnection connection, SqliteTransaction transaction, Guid queryId, CancellationToken cancellationToken)
        => await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM favorites WHERE query_id = @id", cancellationToken, transaction, ("@id", queryId.ToString())).ConfigureAwait(false) > 0;

    private static async Task<Guid?> GetFavoriteIdAsync(SqliteConnection connection, SqliteTransaction transaction, Guid queryId, Guid? resultId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = resultId is null ? "SELECT id FROM favorites WHERE query_id = @query AND result_id IS NULL" : "SELECT id FROM favorites WHERE query_id = @query AND result_id = @result";
        command.Parameters.AddWithValue("@query", queryId.ToString());
        if (resultId is { } id) command.Parameters.AddWithValue("@result", id.ToString());
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string) is { } value && Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    private static async Task<Guid?> GetFavoriteQueryIdAsync(SqliteConnection connection, SqliteTransaction transaction, Guid favoriteId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT query_id FROM favorites WHERE id = @id";
        command.Parameters.AddWithValue("@id", favoriteId.ToString());
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string) is { } value && Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    private static Task InsertFavoriteAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, Guid queryId, Guid? resultId, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, "INSERT INTO favorites (id, query_id, result_id, created_utc, updated_utc) VALUES (@id, @query, @result, @now, @now)", cancellationToken,
            ("@id", id.ToString()), ("@query", queryId.ToString()), ("@result", resultId?.ToString() ?? (object)DBNull.Value), ("@now", FormatUtc(DateTimeOffset.UtcNow)));

    private static Task DeleteFavoriteAsync(SqliteConnection connection, SqliteTransaction transaction, Guid favoriteId, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, "DELETE FROM favorites WHERE id = @id", cancellationToken, ("@id", favoriteId.ToString()));

    private static Task DeleteUnfavoritedHiddenQueriesAsync(SqliteConnection connection, SqliteTransaction transaction, string additionalWhere, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
        => ExecuteAsync(connection, transaction, $"DELETE FROM saved_queries WHERE history_visible = 0 {additionalWhere} AND NOT EXISTS (SELECT 1 FROM favorites f WHERE f.query_id = saved_queries.id)", cancellationToken, parameters);

    private static async Task DeleteHiddenOrExpiredUnfavoritedQueryAsync(SqliteConnection connection, SqliteTransaction transaction, Guid queryId, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = FormatUtc(now.AddDays(-Math.Clamp(retentionDays, 1, 3650)));
        await ExecuteAsync(connection, transaction, "UPDATE saved_queries SET history_visible = 0 WHERE id = @id AND created_utc < @cutoff", cancellationToken, ("@id", queryId.ToString()), ("@cutoff", cutoff)).ConfigureAwait(false);
        await DeleteUnfavoritedHiddenQueriesAsync(connection, transaction, "AND id = @id", cancellationToken, ("@id", queryId.ToString())).ConfigureAwait(false);
    }

    private static async Task<List<string>> GetTagsAsync(SqliteConnection connection, Guid favoriteId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag FROM favorite_tags WHERE favorite_id = @id ORDER BY tag";
        command.Parameters.AddWithValue("@id", favoriteId.ToString());
        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tags.Add(reader.GetString(0));
        return tags;
    }

    private static FavoriteListItem ReadFavoriteListItem(SqliteDataReader reader)
    {
        var favoriteId = Guid.Parse(reader.GetString(reader.GetOrdinal("id")));
        return new FavoriteListItem(
            favoriteId,
            reader.IsDBNull(reader.GetOrdinal("result_id")) ? FavoriteTargetKind.Query : FavoriteTargetKind.Result,
            Guid.Parse(reader.GetString(reader.GetOrdinal("query_id"))),
            reader.IsDBNull(reader.GetOrdinal("result_id")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("result_id"))),
            reader.GetString(reader.GetOrdinal("source_text")),
            reader.GetString(reader.GetOrdinal("source_language")),
            reader.GetString(reader.GetOrdinal("target_language")),
            ParseKind(reader.GetString(reader.GetOrdinal("mode"))),
            reader.GetString(reader.GetOrdinal("provider_id")),
            reader.GetString(reader.GetOrdinal("provider_name")),
            reader.GetString(reader.GetOrdinal("preview_text")),
            reader.GetInt64(reader.GetOrdinal("pinned")) != 0,
            reader.GetString(reader.GetOrdinal("note")),
            [],
            ParseUtc(reader.GetString(reader.GetOrdinal("created_utc"))),
            ParseUtc(reader.GetString(reader.GetOrdinal("updated_utc"))),
            Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("success_result_count"))));
    }

    private static SavedItemsPageResult<T> Page<T>(List<T> items, int pageSize) where T : notnull
    {
        var take = Math.Clamp(pageSize, 1, 50);
        var hasMore = items.Count > take;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var cursor = items.LastOrDefault() switch
        {
            SavedQueryListItem query => new SavedItemsCursor(query.RelevanceRank, query.CreatedUtc, query.Id),
            FavoriteListItem favorite => new SavedItemsCursor(0, favorite.CreatedUtc, favorite.Id, favorite.IsPinned),
            _ => null
        };
        return new SavedItemsPageResult<T>(items, hasMore ? cursor : null);
    }

    private static void AddHistoryParameters(SqliteCommand command, HistoryListRequest request, string search)
    {
        command.Parameters.AddWithValue("@search", search);
        command.Parameters.AddWithValue("@prefix", $"{SavedItemsSearch.EscapeLike(search)}%");
        command.Parameters.AddWithValue("@contains", $"%{SavedItemsSearch.EscapeLike(search)}%");
        command.Parameters.AddWithValue("@kind", request.Kind is { } kind ? ToDb(kind) : string.Empty);
        command.Parameters.AddWithValue("@provider", request.ProviderId ?? string.Empty);
        command.Parameters.AddWithValue("@start", request.StartUtc is { } start ? FormatUtc(start) : string.Empty);
        command.Parameters.AddWithValue("@end", request.EndUtc is { } end ? FormatUtc(end) : string.Empty);
        command.Parameters.AddWithValue("@cursorId", request.Cursor?.Id.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@cursorRank", request.Cursor?.RelevanceRank ?? 0);
        command.Parameters.AddWithValue("@cursorCreated", request.Cursor is { } cursor ? FormatUtc(cursor.CreatedUtc) : string.Empty);
    }

    private static SavedQueryListItem ReadQueryListItem(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("id"))), ParseKind(reader.GetString(reader.GetOrdinal("mode"))),
        reader.GetString(reader.GetOrdinal("source_text")), reader.GetString(reader.GetOrdinal("source_language")), reader.GetString(reader.GetOrdinal("target_language")),
        ParseSourceKind(reader.GetString(reader.GetOrdinal("source_kind"))), ParseUtc(reader.GetString(reader.GetOrdinal("created_utc"))),
        reader.GetString(reader.GetOrdinal("preview_provider_id")), reader.GetString(reader.GetOrdinal("preview_provider_name")), reader.GetString(reader.GetOrdinal("preview_text")),
        Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("success_result_count"))), reader.GetOrdinal("relevance_rank") >= 0 ? Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("relevance_rank"))) : 0);

    private static SavedQueryResultDetail ReadResultDetail(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("id"))), reader.GetString(reader.GetOrdinal("provider_id")), reader.GetString(reader.GetOrdinal("provider_name")),
        Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("display_order"))), ParseContentType(reader.GetString(reader.GetOrdinal("content_type"))),
        reader.GetString(reader.GetOrdinal("plain_text")), reader.GetString(reader.GetOrdinal("preview_text")), reader.GetString(reader.GetOrdinal("payload_json")),
        reader.GetInt64(reader.GetOrdinal("latency_ms")), ParseUtc(reader.GetString(reader.GetOrdinal("created_utc"))));

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string ToDb(SavedQueryKind kind) => kind switch { SavedQueryKind.Translation => "translation", SavedQueryKind.GrammarCorrection => "grammar", SavedQueryKind.Ocr => "ocr", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static string ToDb(QuerySourceKind kind) => kind switch { QuerySourceKind.Manual => "manual", QuerySourceKind.Clipboard => "clipboard", QuerySourceKind.Selection => "selection", QuerySourceKind.Ocr => "ocr", QuerySourceKind.HistoryRerun => "history_rerun", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static string ToDb(SavedResultContentType type) => type == SavedResultContentType.Translation ? "translation" : "grammar";
    private static SavedQueryKind ParseKind(string value) => value switch { "translation" => SavedQueryKind.Translation, "grammar" => SavedQueryKind.GrammarCorrection, "ocr" => SavedQueryKind.Ocr, _ => throw new InvalidDataException($"Unknown saved query kind '{value}'.") };
    private static QuerySourceKind ParseSourceKind(string value) => value switch { "manual" => QuerySourceKind.Manual, "clipboard" => QuerySourceKind.Clipboard, "selection" => QuerySourceKind.Selection, "ocr" => QuerySourceKind.Ocr, "history_rerun" => QuerySourceKind.HistoryRerun, _ => throw new InvalidDataException($"Unknown saved source kind '{value}'.") };
    private static SavedResultContentType ParseContentType(string value) => value == "translation" ? SavedResultContentType.Translation : value == "grammar" ? SavedResultContentType.GrammarCorrection : throw new InvalidDataException($"Unknown saved result content type '{value}'.");
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static int TextElementCount(string value) { var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value); var count = 0; while (enumerator.MoveNext()) count++; return count; }

    private const string Schema = """
        CREATE TABLE saved_queries (
            id TEXT PRIMARY KEY, mode TEXT NOT NULL, source_text TEXT NOT NULL, source_search_text TEXT NOT NULL,
            source_language TEXT NOT NULL, target_language TEXT NOT NULL, source_kind TEXT NOT NULL, created_utc TEXT NOT NULL,
            history_visible INTEGER NOT NULL CHECK (history_visible IN (0, 1)), preview_provider_id TEXT NOT NULL,
            preview_provider_name TEXT NOT NULL, preview_text TEXT NOT NULL, success_result_count INTEGER NOT NULL CHECK (success_result_count > 0)
        );
        CREATE TABLE saved_results (
            id TEXT PRIMARY KEY, query_id TEXT NOT NULL, provider_id TEXT NOT NULL, provider_name TEXT NOT NULL,
            provider_search_text TEXT NOT NULL, display_order INTEGER NOT NULL, content_type TEXT NOT NULL, plain_text TEXT NOT NULL,
            plain_search_text TEXT NOT NULL, preview_text TEXT NOT NULL, payload_json TEXT NOT NULL, search_text TEXT NOT NULL,
            latency_ms INTEGER NOT NULL, created_utc TEXT NOT NULL, FOREIGN KEY (query_id) REFERENCES saved_queries(id) ON DELETE CASCADE,
            UNIQUE (query_id, provider_id), UNIQUE (query_id, id)
        );
        CREATE TABLE favorites (
            id TEXT PRIMARY KEY, query_id TEXT NOT NULL, result_id TEXT NULL, note TEXT NOT NULL DEFAULT '', note_search_text TEXT NOT NULL DEFAULT '',
            pinned INTEGER NOT NULL DEFAULT 0 CHECK (pinned IN (0, 1)), created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
            FOREIGN KEY (query_id) REFERENCES saved_queries(id) ON DELETE CASCADE,
            FOREIGN KEY (query_id, result_id) REFERENCES saved_results(query_id, id) ON DELETE CASCADE
        );
        CREATE TABLE favorite_tags (
            favorite_id TEXT NOT NULL, tag TEXT NOT NULL, tag_search_text TEXT NOT NULL,
            FOREIGN KEY (favorite_id) REFERENCES favorites(id) ON DELETE CASCADE, PRIMARY KEY (favorite_id, tag_search_text)
        );
        CREATE INDEX idx_queries_history_created ON saved_queries(history_visible, created_utc DESC, id DESC);
        CREATE INDEX idx_results_query_order ON saved_results(query_id, display_order);
        CREATE INDEX idx_favorites_created ON favorites(pinned DESC, created_utc DESC, id DESC);
        CREATE INDEX idx_favorite_tags_search ON favorite_tags(tag_search_text, favorite_id);
        CREATE UNIQUE INDEX uq_favorite_query ON favorites(query_id) WHERE result_id IS NULL;
        CREATE UNIQUE INDEX uq_favorite_result ON favorites(result_id) WHERE result_id IS NOT NULL;
        """;
}
