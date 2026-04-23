using System.Diagnostics;
using System.Text.Json;
using Easydict.WinUI.Models;
using LexIndex;
using LexIndexFile = LexIndex.LexIndex;

namespace Easydict.WinUI.Services;

/// <summary>
/// Builds and queries persistent local dictionary key indexes for imported MDX services.
/// </summary>
public sealed class LocalDictionaryIndexService
{
    private const int CurrentIndexFormatVersion = 1;
    private const string IndexFileName = "index.bin";
    private const string ManifestFileName = "manifest.json";

    private static readonly Lazy<LocalDictionaryIndexService> _instance =
        new(() => new LocalDictionaryIndexService());

    private readonly string _indexRoot;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, ServiceDescriptor> _serviceDescriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedIndexEntry> _loadedIndexes = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public static LocalDictionaryIndexService Instance => _instance.Value;

    public LocalDictionaryIndexService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict",
                "mdx_index"))
    {
    }

    internal LocalDictionaryIndexService(string indexRoot)
    {
        _indexRoot = indexRoot;
        Directory.CreateDirectory(_indexRoot);
    }

    public Task EnsureIndexAsync(
        SettingsService.ImportedMdxDictionary dictionary,
        MdxDictionaryTranslationService service,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(service);

        UpsertDescriptor(dictionary, service);

        if (!service.CanEnumerateKeys)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => EnsureIndexCoreAsync(dictionary, service, ct), ct);
    }

    public void RemoveDictionary(string serviceId, bool deleteFiles = true)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return;
        }

        lock (_gate)
        {
            _serviceDescriptors.Remove(serviceId);
            _loadedIndexes.Remove(serviceId);
        }

        if (!deleteFiles)
        {
            return;
        }

        var folderPath = GetDictionaryFolder(serviceId);
        try
        {
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDictionaryIndexService] Failed to delete index folder for '{serviceId}': {ex.Message}");
        }
    }

    public Task<IReadOnlyList<SuggestionItem>> CompleteAsync(
        string prefix,
        IReadOnlyList<string> serviceIds,
        int limit,
        CancellationToken ct = default)
    {
        return QueryAsync(
            prefix,
            serviceIds,
            limit,
            static (index, query, resultLimit) => index.Complete(query, resultLimit),
            ct);
    }

    public Task<IReadOnlyList<SuggestionItem>> MatchAsync(
        string pattern,
        IReadOnlyList<string> serviceIds,
        int limit,
        CancellationToken ct = default)
    {
        return QueryAsync(
            pattern,
            serviceIds,
            limit,
            static (index, query, resultLimit) => index.Match(query, resultLimit),
            ct);
    }

    private async Task EnsureIndexCoreAsync(
        SettingsService.ImportedMdxDictionary dictionary,
        MdxDictionaryTranslationService service,
        CancellationToken ct)
    {
        if (!File.Exists(dictionary.FilePath))
        {
            return;
        }

        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            var folderPath = GetDictionaryFolder(dictionary.ServiceId);
            var indexPath = Path.Combine(folderPath, IndexFileName);
            var manifestPath = Path.Combine(folderPath, ManifestFileName);
            Directory.CreateDirectory(folderPath);

            var sourceInfo = new FileInfo(dictionary.FilePath);
            var currentFingerprint = new ManifestData
            {
                ServiceId = dictionary.ServiceId,
                SourcePath = dictionary.FilePath,
                SourceLastWriteUtc = sourceInfo.LastWriteTimeUtc,
                SourceLength = sourceInfo.Length,
                IndexFormatVersion = CurrentIndexFormatVersion,
                NormalizationId = LexIndexBuildOptions.DefaultNormalizationId
            };

            var existingManifest = LoadManifest(manifestPath);
            if (existingManifest is not null &&
                File.Exists(indexPath) &&
                existingManifest.Matches(currentFingerprint))
            {
                lock (_gate)
                {
                    _loadedIndexes.Remove(dictionary.ServiceId);
                }

                return;
            }

            var tempIndexPath = indexPath + ".tmp";
            var tempManifestPath = manifestPath + ".tmp";

            try
            {
                await using (var output = File.Create(tempIndexPath))
                {
                    await LexIndexBuilder.BuildAsync(service.EnumerateKeys(), output, ct: ct).ConfigureAwait(false);
                }

                var builtIndex = LexIndexFile.Open(tempIndexPath);
                currentFingerprint.EntryCount = builtIndex.Metadata.EntryCount;

                var manifestJson = JsonSerializer.Serialize(currentFingerprint, _jsonOptions);
                await File.WriteAllTextAsync(tempManifestPath, manifestJson, ct).ConfigureAwait(false);

                File.Move(tempIndexPath, indexPath, overwrite: true);
                File.Move(tempManifestPath, manifestPath, overwrite: true);

                lock (_gate)
                {
                    _loadedIndexes[dictionary.ServiceId] = new LoadedIndexEntry(
                        builtIndex,
                        currentFingerprint,
                        service.DisplayName,
                        IsQueryable: true);
                }
            }
            finally
            {
                TryDeleteFile(tempIndexPath);
                TryDeleteFile(tempManifestPath);
            }
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private Task<IReadOnlyList<SuggestionItem>> QueryAsync(
        string query,
        IReadOnlyList<string> serviceIds,
        int limit,
        Func<ILexIndex, string, int, IReadOnlyList<string>> executor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || serviceIds.Count == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<SuggestionItem>>(Array.Empty<SuggestionItem>());
        }

        return Task.Run<IReadOnlyList<SuggestionItem>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            var results = new List<SuggestionItem>(Math.Min(limit, 20));
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var serviceId in serviceIds)
            {
                ct.ThrowIfCancellationRequested();

                if (!TryGetQueryableIndex(serviceId, out var entry))
                {
                    continue;
                }

                foreach (var key in executor(entry.Index, query, limit))
                {
                    if (!seenKeys.Add(key))
                    {
                        continue;
                    }

                    results.Add(new SuggestionItem
                    {
                        Key = key,
                        DictDisplayName = entry.DisplayName,
                        DictServiceId = serviceId
                    });

                    if (results.Count >= limit)
                    {
                        return (IReadOnlyList<SuggestionItem>)results;
                    }
                }
            }

            return (IReadOnlyList<SuggestionItem>)results;
        }, ct);
    }

    private bool TryGetQueryableIndex(string serviceId, out LoadedIndexEntry entry)
    {
        lock (_gate)
        {
            if (_loadedIndexes.TryGetValue(serviceId, out entry!) && entry.IsQueryable)
            {
                return true;
            }

            if (_serviceDescriptors.TryGetValue(serviceId, out var descriptor) && !descriptor.IsQueryable)
            {
                entry = default!;
                return false;
            }
        }

        var folderPath = GetDictionaryFolder(serviceId);
        var indexPath = Path.Combine(folderPath, IndexFileName);
        var manifestPath = Path.Combine(folderPath, ManifestFileName);
        if (!File.Exists(indexPath) || !File.Exists(manifestPath))
        {
            entry = default!;
            return false;
        }

        var manifest = LoadManifest(manifestPath);
        if (manifest is null || manifest.IndexFormatVersion != CurrentIndexFormatVersion)
        {
            entry = default!;
            return false;
        }

        var displayName = manifest.ServiceId;
        var isQueryable = true;
        lock (_gate)
        {
            if (_serviceDescriptors.TryGetValue(serviceId, out var descriptor))
            {
                displayName = descriptor.DisplayName;
                isQueryable = descriptor.IsQueryable;
            }
        }

        if (!isQueryable)
        {
            entry = default!;
            return false;
        }

        try
        {
            var index = LexIndexFile.Open(indexPath);
            entry = new LoadedIndexEntry(index, manifest, displayName, IsQueryable: true);
            lock (_gate)
            {
                _loadedIndexes[serviceId] = entry;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDictionaryIndexService] Failed to open index for '{serviceId}': {ex.Message}");
            entry = default!;
            return false;
        }
    }

    private void UpsertDescriptor(SettingsService.ImportedMdxDictionary dictionary, MdxDictionaryTranslationService service)
    {
        var descriptor = new ServiceDescriptor(
            dictionary.DisplayName,
            dictionary.FilePath,
            service.CanEnumerateKeys);

        lock (_gate)
        {
            _serviceDescriptors[dictionary.ServiceId] = descriptor;

            if (_loadedIndexes.TryGetValue(dictionary.ServiceId, out var existing))
            {
                _loadedIndexes[dictionary.ServiceId] = existing with
                {
                    DisplayName = dictionary.DisplayName,
                    IsQueryable = service.CanEnumerateKeys
                };
            }
        }
    }

    private ManifestData? LoadManifest(string manifestPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<ManifestData>(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDictionaryIndexService] Failed to load manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    private string GetDictionaryFolder(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        // MDX service IDs contain characters like ':' that are invalid in Windows folder names.
        return Path.Combine(_indexRoot, Uri.EscapeDataString(serviceId));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed record ServiceDescriptor(string DisplayName, string SourcePath, bool IsQueryable);

    private sealed record LoadedIndexEntry(
        ILexIndex Index,
        ManifestData Manifest,
        string DisplayName,
        bool IsQueryable);

    internal sealed class ManifestData
    {
        public string ServiceId { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        public DateTime SourceLastWriteUtc { get; set; }

        public long SourceLength { get; set; }

        public int IndexFormatVersion { get; set; }

        public string NormalizationId { get; set; } = string.Empty;

        public int EntryCount { get; set; }

        public bool Matches(ManifestData other)
        {
            return string.Equals(ServiceId, other.ServiceId, StringComparison.Ordinal) &&
                   string.Equals(SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                   SourceLastWriteUtc == other.SourceLastWriteUtc &&
                   SourceLength == other.SourceLength &&
                   IndexFormatVersion == other.IndexFormatVersion &&
                   string.Equals(NormalizationId, other.NormalizationId, StringComparison.Ordinal);
        }
    }
}
