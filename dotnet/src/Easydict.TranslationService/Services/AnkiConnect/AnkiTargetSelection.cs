namespace Easydict.TranslationService.Services.AnkiConnect;

/// <summary>Choose the configured default or the last successful destination as a pair.</summary>
public static class AnkiTargetSelection
{
    public static (string Deck, string Model) Choose(bool rememberLast, string defaultDeck, string defaultModel,
        string lastDeck, string lastModel, IReadOnlyDictionary<string, Dictionary<string, string>> templates)
    {
        if (rememberLast && !string.IsNullOrWhiteSpace(lastDeck) && !string.IsNullOrWhiteSpace(lastModel)
            && templates.TryGetValue(lastModel, out var fields) && fields is { Count: > 0 })
            return (lastDeck, lastModel);
        return (defaultDeck, defaultModel);
    }
}
