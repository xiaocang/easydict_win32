namespace Easydict.WinUI.Services;

internal static class SettingsPresentationComparison
{
    // Language checkboxes are grouped for display; their order is not a preference.
    public static bool LanguagesEqual(IEnumerable<string> current, IEnumerable<string> saved) =>
        new HashSet<string>(current, StringComparer.OrdinalIgnoreCase)
            .SetEquals(saved.Append("en"));

    // A missing query-mode entry means automatic query. Disabled services are
    // compared by the separate enabled-service list, not by dormant overrides.
    public static bool QueryModesEqual(IReadOnlyDictionary<string, bool> current, IReadOnlyDictionary<string, bool> saved) =>
        current.All(pair => pair.Value == (saved.TryGetValue(pair.Key, out var automatic) ? automatic : true));
}
