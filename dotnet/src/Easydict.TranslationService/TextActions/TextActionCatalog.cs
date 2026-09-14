using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.TextActions;

/// <summary>
/// Built-in text actions and the merge rule used when loading a user's saved list.
/// </summary>
public static class TextActionCatalog
{
    /// <summary>Actions shipped with Easydict. Only the first one is on the pop-up strip by default.</summary>
    public static IReadOnlyList<TextAction> Defaults { get; } =
    [
        new TextAction
        {
            Id = "google",
            Title = "Google",
            Type = TextActionType.OpenUrl,
            UrlTemplate = "https://www.google.com/search?q={encodedText}",
            IconGlyph = "\uE721",
            IsBuiltIn = true,
            ShowOnPopButton = true
        },
        new TextAction
        {
            Id = "bing",
            Title = "Bing",
            Type = TextActionType.OpenUrl,
            UrlTemplate = "https://www.bing.com/search?q={encodedText}",
            IconGlyph = "\uE721",
            IsBuiltIn = true
        },
        new TextAction
        {
            Id = "github",
            Title = "GitHub",
            Type = TextActionType.OpenUrl,
            UrlTemplate = "https://github.com/search?q={encodedText}&type=repositories",
            IconGlyph = "\uE943",
            IsBuiltIn = true
        },
        new TextAction
        {
            Id = "wikipedia",
            Title = "Wikipedia",
            Type = TextActionType.OpenUrl,
            UrlTemplate = "https://en.wikipedia.org/wiki/Special:Search?search={encodedText}",
            IconGlyph = "\uE8A5",
            IsBuiltIn = true
        },
        new TextAction
        {
            Id = "baidu",
            Title = "百度",
            Type = TextActionType.OpenUrl,
            UrlTemplate = "https://www.baidu.com/s?wd={encodedText}",
            IconGlyph = "\uE721",
            IsBuiltIn = true
        }
    ];

    /// <summary>
    /// Merge a saved list with the defaults: saved entries keep their order and edits; built-ins
    /// that are missing from the saved list are appended. Used for "reset to defaults" and for
    /// first-run initialization.
    /// </summary>
    public static List<TextAction> Merge(IReadOnlyList<TextAction>? saved)
    {
        if (saved is null || saved.Count == 0)
        {
            return Defaults.ToList();
        }

        var merged = Normalize(saved);
        var seen = new HashSet<string>(merged.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var builtIn in Defaults)
        {
            if (seen.Add(builtIn.Id))
            {
                merged.Add(builtIn);
            }
        }

        return merged;
    }

    /// <summary>
    /// Sanitize a saved list without re-adding built-ins the user removed: drops null entries,
    /// blank ids and duplicates while preserving order. <c>null</c> (nothing saved yet) yields the defaults.
    /// </summary>
    public static List<TextAction> Normalize(IReadOnlyList<TextAction>? saved)
    {
        if (saved is null)
        {
            return Defaults.ToList();
        }

        var result = new List<TextAction>(saved.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in saved)
        {
            if (action is null || string.IsNullOrWhiteSpace(action.Id) || !seen.Add(action.Id))
            {
                continue;
            }

            result.Add(action);
        }

        return result;
    }
}
