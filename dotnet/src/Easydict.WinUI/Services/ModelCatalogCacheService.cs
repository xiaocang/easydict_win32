using System.Text.Json;
using System.Text.Json.Serialization;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.WinUI.Services;

/// <summary>
/// Persists a translation service's fetched model catalog to disk, so opening Settings does
/// not re-fetch a provider's model list every time. Storage mirrors the convention used by
/// <see cref="TranslationCacheService"/> (a JSON file under <c>%LOCALAPPDATA%\Easydict</c>),
/// but is a plain file per service rather than a database — the payload is small and read
/// far more often than written.
/// </summary>
public sealed class ModelCatalogCacheService
{
    /// <summary>
    /// How long a cached catalog is considered fresh enough to use without a network call.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private sealed record CachedModel(string Id, string? Name, bool IsFree, long? ContextLength);

    private sealed record CachedCatalog(DateTime FetchedUtc, List<CachedModel> Models);

    private readonly string _cacheDirectory;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _locksGuard = new();

    public ModelCatalogCacheService() : this(DefaultCacheDirectory(), DefaultTtl) { }

    /// <summary>
    /// Test constructor that accepts a custom cache directory and TTL.
    /// </summary>
    internal ModelCatalogCacheService(string cacheDirectory, TimeSpan ttl)
    {
        _cacheDirectory = cacheDirectory;
        _ttl = ttl;
        Directory.CreateDirectory(_cacheDirectory);
    }

    private static string DefaultCacheDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict",
            "model-catalog");
    }

    /// <summary>
    /// Returns the cached catalog for <paramref name="serviceId"/> only if it is still within
    /// TTL. Returns null if absent, corrupt, or stale.
    /// </summary>
    public async Task<IReadOnlyList<ModelCatalogEntry>?> TryGetFreshAsync(string serviceId)
    {
        var cached = await ReadAsync(serviceId).ConfigureAwait(false);
        if (cached is null)
        {
            return null;
        }

        var age = DateTime.UtcNow - cached.FetchedUtc;
        return age <= _ttl ? ToEntries(cached) : null;
    }

    /// <summary>
    /// Returns the cached catalog for <paramref name="serviceId"/> regardless of age. Used as
    /// a stale-but-better-than-nothing fallback when a live refetch fails. Returns null if
    /// nothing has ever been cached or the cache file is corrupt.
    /// </summary>
    public async Task<IReadOnlyList<ModelCatalogEntry>?> TryGetAnyAsync(string serviceId)
    {
        var cached = await ReadAsync(serviceId).ConfigureAwait(false);
        return cached is null ? null : ToEntries(cached);
    }

    /// <summary>
    /// Overwrite the cached catalog for <paramref name="serviceId"/>. Writes atomically
    /// (temp file + rename) so a crash mid-write never leaves a truncated cache file.
    /// </summary>
    public async Task SaveAsync(string serviceId, IReadOnlyList<ModelCatalogEntry> models)
    {
        var payload = new CachedCatalog(
            DateTime.UtcNow,
            models.Select(m => new CachedModel(m.Id, m.Name, m.IsFree, m.ContextLength)).ToList());

        var gate = GetLock(serviceId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = PathFor(serviceId);
            var tempPath = path + ".tmp";
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Persisting the cache is a best-effort convenience; a failed write should never
            // surface as a translation-blocking error.
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CachedCatalog?> ReadAsync(string serviceId)
    {
        var gate = GetLock(serviceId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = PathFor(serviceId);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CachedCatalog>(json, JsonOptions);
        }
        catch
        {
            // Corrupt or unreadable cache file: treat as absent.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<ModelCatalogEntry> ToEntries(CachedCatalog cached)
    {
        return cached.Models
            .Select(m => new ModelCatalogEntry(m.Id, m.Name, m.IsFree, m.ContextLength))
            .ToList();
    }

    private string PathFor(string serviceId)
    {
        // Service ids are developer-controlled ASCII identifiers (e.g. "openrouter"), safe as
        // file names as-is.
        return Path.Combine(_cacheDirectory, $"{serviceId}.json");
    }

    private SemaphoreSlim GetLock(string serviceId)
    {
        lock (_locksGuard)
        {
            if (!_locks.TryGetValue(serviceId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[serviceId] = gate;
            }
            return gate;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
