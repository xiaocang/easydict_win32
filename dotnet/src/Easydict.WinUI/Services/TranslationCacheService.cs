using System.Security.Cryptography;
using System.Text;
using Easydict.TranslationService.Models;
using Microsoft.Data.Sqlite;

namespace Easydict.WinUI.Services;

public sealed class TranslationCacheService : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private bool _initialized;
    private bool _disposed;

    public TranslationCacheService()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict");
        Directory.CreateDirectory(baseDir);
        _dbPath = Path.Combine(baseDir, "translation_cache.db");
    }

    /// <summary>
    /// Test constructor that accepts a custom database path.
    /// </summary>
    internal TranslationCacheService(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync(ct);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS translation_cache (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                service_id TEXT NOT NULL,
                from_lang TEXT NOT NULL,
                to_lang TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                source_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                last_used_utc TEXT NOT NULL,
                hit_count INTEGER DEFAULT 0,
                UNIQUE(service_id, from_lang, to_lang, source_hash)
            );
            CREATE INDEX IF NOT EXISTS idx_cache_lookup
                ON translation_cache(service_id, from_lang, to_lang, source_hash);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
    }

    public async Task<string?> TryGetAsync(
        string serviceId, Language from, Language to, string sourceTextHash, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            UPDATE translation_cache
            SET hit_count = hit_count + 1, last_used_utc = @now
            WHERE service_id = @sid AND from_lang = @from AND to_lang = @to AND source_hash = @hash
            RETURNING translated_text
            """;
        cmd.Parameters.AddWithValue("@sid", serviceId);
        cmd.Parameters.AddWithValue("@from", from.ToString());
        cmd.Parameters.AddWithValue("@to", to.ToString());
        cmd.Parameters.AddWithValue("@hash", sourceTextHash);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetAsync(
        string serviceId, Language from, Language to, string sourceTextHash,
        string sourceText, string translatedText, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO translation_cache (service_id, from_lang, to_lang, source_hash, source_text, translated_text, created_utc, last_used_utc, hit_count)
            VALUES (@sid, @from, @to, @hash, @source, @translated, @now, @now, 0)
            ON CONFLICT(service_id, from_lang, to_lang, source_hash)
            DO UPDATE SET translated_text = @translated, last_used_utc = @now, hit_count = hit_count + 1
            """;
        cmd.Parameters.AddWithValue("@sid", serviceId);
        cmd.Parameters.AddWithValue("@from", from.ToString());
        cmd.Parameters.AddWithValue("@to", to.ToString());
        cmd.Parameters.AddWithValue("@hash", sourceTextHash);
        cmd.Parameters.AddWithValue("@source", sourceText);
        cmd.Parameters.AddWithValue("@translated", translatedText);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> GetEntryCountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM translation_cache";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM translation_cache";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static string ComputeHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            await InitializeAsync(ct);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
    }
}
