using System.Globalization;
using System.Text;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Models;

public enum SavedQueryKind
{
    Translation,
    GrammarCorrection,
    Ocr
}

public enum QuerySourceKind
{
    Manual,
    Clipboard,
    Selection,
    Ocr,
    HistoryRerun
}

public enum SavedResultContentType
{
    Translation,
    GrammarCorrection
}

public enum FavoriteTargetKind
{
    Query,
    Result
}

public enum SavedItemsSection
{
    History,
    Favorites
}

public enum SavedItemsChangeKind
{
    History,
    Favorite,
    Metadata,
    Cleanup
}

public sealed record SavedItemsNavigationRequest(SavedItemsSection Section);

public sealed record SavedQueryRerunRequest(
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    SavedQueryKind Kind);

public sealed record SavedProviderResultSnapshot(
    Guid Id,
    string ProviderId,
    string ProviderName,
    int DisplayOrder,
    SavedResultContentType ContentType,
    string PlainText,
    string PreviewText,
    string PayloadJson,
    string SearchText,
    long LatencyMs,
    DateTimeOffset CreatedUtc);

public sealed record QuerySnapshot(
    Guid Id,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    SavedQueryKind Kind,
    QuerySourceKind SourceKind,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<SavedProviderResultSnapshot> Results);

public sealed record SavedQueryListItem(
    Guid Id,
    SavedQueryKind Kind,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    QuerySourceKind SourceKind,
    DateTimeOffset CreatedUtc,
    string PreviewProviderId,
    string PreviewProviderName,
    string PreviewText,
    int SuccessResultCount,
    int RelevanceRank = 0);

public sealed record SavedQueryResultDetail(
    Guid Id,
    string ProviderId,
    string ProviderName,
    int DisplayOrder,
    SavedResultContentType ContentType,
    string PlainText,
    string PreviewText,
    string PayloadJson,
    long LatencyMs,
    DateTimeOffset CreatedUtc);

public sealed record SavedQueryDetail(
    SavedQueryListItem Query,
    IReadOnlyList<SavedQueryResultDetail> Results,
    bool IsFavorited);

public sealed record FavoriteListItem(
    Guid Id,
    FavoriteTargetKind TargetKind,
    Guid QueryId,
    Guid? ResultId,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    SavedQueryKind QueryKind,
    string ProviderId,
    string ProviderName,
    string PreviewText,
    bool IsPinned,
    string Note,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int SuccessResultCount = 1);

public sealed record FavoriteDetail(
    FavoriteListItem Favorite,
    SavedQueryDetail QueryDetail);

public sealed record FavoriteToggleResult(Guid FavoriteId, bool IsFavorited, FavoriteTargetKind TargetKind);

public sealed record FavoriteStateMap(bool IsQueryFavorited, IReadOnlySet<Guid> FavoritedResultIds);

public sealed class SavedItemsChangedEventArgs(
    SavedItemsChangeKind kind,
    Guid? queryId = null,
    Guid? favoriteId = null) : EventArgs
{
    public SavedItemsChangeKind Kind { get; } = kind;
    public Guid? QueryId { get; } = queryId;
    public Guid? FavoriteId { get; } = favoriteId;
}

public sealed record SavedItemsCursor(int RelevanceRank, DateTimeOffset CreatedUtc, Guid Id, bool IsPinned = false);

public sealed record SavedItemsPageResult<T>(IReadOnlyList<T> Items, SavedItemsCursor? NextCursor);

public sealed record HistoryListRequest(
    string? SearchText = null,
    SavedQueryKind? Kind = null,
    string? ProviderId = null,
    DateTimeOffset? StartUtc = null,
    DateTimeOffset? EndUtc = null,
    SavedItemsCursor? Cursor = null,
    int PageSize = 50,
    bool BeforeCursor = false,
    bool IncludeCursor = false,
    int SourceTextLimit = 0);

public sealed record FavoriteListRequest(
    string? SearchText = null,
    FavoriteTargetKind? TargetKind = null,
    IReadOnlyList<string>? Tags = null,
    bool PinnedOnly = false,
    SavedItemsCursor? Cursor = null,
    int PageSize = 50,
    bool BeforeCursor = false,
    bool IncludeCursor = false,
    int SourceTextLimit = 0);

public sealed record SavedItemsFilterOptions(
    IReadOnlyList<(string Id, string Name)> Providers,
    IReadOnlyList<string> Tags);

public static class SavedQueryClassifier
{
    public static SavedQueryKind Classify(QueryMode effectiveMode, QuerySourceKind sourceKind)
    {
        if (effectiveMode == QueryMode.LongDocument)
            throw new ArgumentOutOfRangeException(nameof(effectiveMode), "Long-document queries are not saved.");

        if (sourceKind == QuerySourceKind.Ocr)
            return SavedQueryKind.Ocr;

        return effectiveMode == QueryMode.GrammarCorrection
            ? SavedQueryKind.GrammarCorrection
            : SavedQueryKind.Translation;
    }
}

public static class SavedItemsSearch
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var inWhitespace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character))
            {
                inWhitespace = builder.Length > 0;
                continue;
            }

            if (inWhitespace)
            {
                builder.Append(' ');
                inWhitespace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim().ToUpperInvariant();
    }

    public static string EscapeLike(string normalizedValue)
        => normalizedValue.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}

public static class SavedResultPreview
{
    public const int MaxTextElements = 100;

    public static string Create(string? text)
    {
        var collapsed = CollapseWhitespace(text);
        if (collapsed.Length == 0)
            return string.Empty;

        var enumerator = StringInfo.GetTextElementEnumerator(collapsed);
        var builder = new StringBuilder(collapsed.Length);
        var count = 0;
        while (enumerator.MoveNext())
        {
            if (count == MaxTextElements)
                return builder.Append('…').ToString();

            builder.Append(enumerator.GetTextElement());
            count++;
        }

        return builder.ToString();
    }

    public static string FromTranslation(TranslationResult result)
    {
        var definition = result.WordResult?.Definitions?
            .SelectMany(static definition => definition.Meanings ?? [])
            .FirstOrDefault(static meaning => !string.IsNullOrWhiteSpace(meaning));
        return Create(definition ?? result.TranslatedText);
    }

    public static string FromGrammar(GrammarCorrectionResult result) => Create(result.CorrectedText);

    public static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = true;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                    builder.Append(' ');
                previousWasWhitespace = true;
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }
}

public sealed class QuerySnapshotDraft
{
    private readonly object _gate = new();
    // Keep immutable result data (sharing strings), not serialized copies of every result.
    // JSON and search documents exist only for the duration of a persistence operation.
    private readonly Dictionary<string, PendingResult> _results = new(StringComparer.Ordinal);

    private sealed record PendingResult(Guid Id, string ProviderId, string ProviderName, int DisplayOrder,
        DateTimeOffset CreatedUtc, TranslationResult? Translation, GrammarCorrectionResult? Grammar)
    {
        public SavedProviderResultSnapshot Materialize()
        {
            if (Translation is { } translation)
                return new(Id, ProviderId, ProviderName, DisplayOrder, SavedResultContentType.Translation,
                    translation.TranslatedText, SavedResultPreview.FromTranslation(translation),
                    System.Text.Json.JsonSerializer.Serialize(translation), BuildTranslationSearchText(translation),
                    translation.TimingMs, CreatedUtc);

            var grammar = Grammar!;
            return new(Id, ProviderId, ProviderName, DisplayOrder, SavedResultContentType.GrammarCorrection,
                grammar.CorrectedText, SavedResultPreview.FromGrammar(grammar),
                System.Text.Json.JsonSerializer.Serialize(grammar),
                string.Join(' ', new[] { grammar.CorrectedText, grammar.Explanation }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))), grammar.TimingMs, CreatedUtc);
        }
    }

    public QuerySnapshotDraft(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        SavedQueryKind kind,
        QuerySourceKind sourceKind,
        bool historyEnabled,
        DateTimeOffset? createdUtc = null)
    {
        Id = Guid.NewGuid();
        SourceText = sourceText;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Kind = kind;
        SourceKind = sourceKind;
        HistoryEnabled = historyEnabled;
        CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }
    public string SourceText { get; }
    public string SourceLanguage { get; }
    public string TargetLanguage { get; }
    public SavedQueryKind Kind { get; }
    public QuerySourceKind SourceKind { get; }
    public bool HistoryEnabled { get; }
    public DateTimeOffset CreatedUtc { get; }

    public bool HasResults { get { lock (_gate) return _results.Count > 0; } }

    public Guid? GetResultId(string providerId)
    {
        lock (_gate) return _results.TryGetValue(providerId, out var result) ? result.Id : null;
    }

    public bool TryAddTranslation(string providerId, string providerName, int displayOrder, TranslationResult result)
    {
        if (string.IsNullOrWhiteSpace(providerId) || result.ResultKind != TranslationResultKind.Success || string.IsNullOrWhiteSpace(result.TranslatedText))
            return false;

        // Providers can expose mutable collections through IReadOnlyList. Freeze their
        // structure now so deferred serialization still describes the completed query.
        return Add(providerId, providerName, displayOrder, Freeze(result), null);
    }

    public bool TryAddGrammar(string providerId, string providerName, int displayOrder, GrammarCorrectionResult result)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(result.CorrectedText))
            return false;

        return Add(providerId, providerName, displayOrder, null, result);
    }

    public QuerySnapshot Snapshot()
    {
        PendingResult[] results;
        lock (_gate) results = _results.Values.ToArray();
        return new QuerySnapshot(Id, SourceText, SourceLanguage, TargetLanguage, Kind, SourceKind, CreatedUtc,
            results.OrderBy(static result => result.DisplayOrder).Select(static result => result.Materialize()).ToArray());
    }

    private bool Add(string providerId, string providerName, int displayOrder,
        TranslationResult? translation, GrammarCorrectionResult? grammar)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        lock (_gate)
        {
            var id = _results.TryGetValue(providerId, out var existing) ? existing.Id : Guid.NewGuid();
            _results[providerId] = new PendingResult(id, providerId, providerName, displayOrder,
                DateTimeOffset.UtcNow, translation, grammar);
            return true;
        }
    }

    private static TranslationResult Freeze(TranslationResult result) => result with
    {
        Alternatives = result.Alternatives?.ToArray(),
        WordResult = result.WordResult is not { } word ? null : new WordResult
        {
            Phonetics = word.Phonetics?.Select(p => new Phonetic { Text = p.Text, AudioUrl = p.AudioUrl, Accent = p.Accent }).ToArray(),
            Definitions = word.Definitions?.Select(d => new Definition { PartOfSpeech = d.PartOfSpeech, Meanings = d.Meanings?.ToArray() }).ToArray(),
            Examples = word.Examples?.ToArray(),
            WordForms = word.WordForms?.Select(w => new WordForm { Name = w.Name, Value = w.Value }).ToArray(),
            Synonyms = word.Synonyms?.Select(s => new Synonym { PartOfSpeech = s.PartOfSpeech, Meaning = s.Meaning, Words = s.Words?.ToArray() }).ToArray()
        }
    };

    private static string BuildTranslationSearchText(TranslationResult result)
    {
        var words = result.WordResult?.Definitions?
            .SelectMany(static definition => definition.Meanings ?? []) ?? [];
        var examples = result.WordResult?.Examples ?? [];
        return string.Join(' ', new[] { result.TranslatedText, result.RawHtml }
            .Concat(words)
            .Concat(examples)
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }
}
