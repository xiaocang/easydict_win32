namespace Easydict.WinUI.Services;

/// <summary>
/// Builds autocomplete suggestions for the hotkey input boxes on the settings page.
/// Token names must stay in sync with what <see cref="HotkeyParser.Parse"/> accepts.
/// </summary>
public static class HotkeySuggestionProvider
{
    private const int MaxSuggestions = 12;

    private static readonly string[] _modifiers = ["Ctrl", "Alt", "Shift", "Win"];

    // Modifier spellings HotkeyParser accepts, used to classify already-typed tokens.
    private static readonly string[] _modifierAliases =
        ["CTRL", "CONTROL", "ALT", "SHIFT", "WIN", "WINDOWS"];

    private static readonly string[] _keys = BuildKeys();

    private static string[] BuildKeys()
    {
        var keys = new List<string>();
        for (var c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (var c = '0'; c <= '9'; c++) keys.Add(c.ToString());
        for (var i = 1; i <= 12; i++) keys.Add($"F{i}");
        keys.AddRange([
            "Space", "Enter", "Tab", "Esc", "Backspace", "Delete", "Insert",
            "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
            "PrintScreen",
        ]);
        return [.. keys];
    }

    /// <summary>
    /// Suggestions for a partially-typed hotkey string. Modifier suggestions end
    /// with '+' so the user can keep extending the combination; key suggestions
    /// complete it. Bare keys (no modifier yet) are only offered for function
    /// keys, matching <see cref="HotkeyParser.IsValidCombination"/>.
    /// </summary>
    public static IReadOnlyList<string> GetSuggestions(string? text)
    {
        // Keep empty entries: a trailing '+' yields an empty partial token,
        // meaning "show every candidate for the next part".
        var tokens = (text ?? string.Empty).Split('+');
        var partial = tokens[^1].Trim();
        var completed = tokens[..^1]
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        // A completed non-modifier token means the combination already has its key.
        if (completed.Any(t => !IsModifier(t)))
            return [];

        var prefix = completed.Count > 0 ? string.Join("+", completed) + "+" : string.Empty;
        var suggestions = new List<string>();

        foreach (var modifier in _modifiers)
        {
            if (completed.Any(t => IsSameModifier(t, modifier)))
                continue;
            if (modifier.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                suggestions.Add($"{prefix}{modifier}+");
        }

        foreach (var key in _keys)
        {
            if (suggestions.Count >= MaxSuggestions)
                break;
            // Without a modifier, only function keys form a valid hotkey.
            if (completed.Count == 0 && !key.StartsWith('F'))
                continue;
            if (key.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                suggestions.Add(prefix + key);
        }

        return suggestions.Count > MaxSuggestions
            ? suggestions[..MaxSuggestions]
            : suggestions;
    }

    private static bool IsModifier(string token) =>
        _modifierAliases.Contains(token.ToUpperInvariant());

    private static bool IsSameModifier(string token, string modifier)
    {
        var upper = token.ToUpperInvariant();
        return modifier switch
        {
            "Ctrl" => upper is "CTRL" or "CONTROL",
            "Win" => upper is "WIN" or "WINDOWS",
            _ => upper == modifier.ToUpperInvariant(),
        };
    }
}
