namespace Easydict.WinUI.Services;

/// <summary>
/// Built-in system tray icons. The default id keeps the current themed app icon.
/// The other ids read a light/dark pair shipped with the app:
/// <c>Assets/Branding/TrayIcons/{id}.ico</c> and <c>{id}.dark.ico</c>.
/// Optional <c>{id}.png</c> / <c>{id}.dark.png</c> are the settings previews.
/// Original artwork is kept in <c>Assets/Branding/TrayIcons/source</c>.
/// If only one of the two ico files is present, it is used for both taskbar colors.
/// A missing icon keeps the current default icon.
/// </summary>
internal static class TrayIconCatalog
{
    internal const string DefaultId = "default";
    internal const string EId = "e";
    internal const string AjpId = "ajp";
    internal const string WenId = "wen";

    private static readonly string[] BundledIds = [EId, AjpId, WenId];

    internal static string Normalize(string? id)
    {
        var trimmed = id?.Trim();
        foreach (var known in BundledIds)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return DefaultId;
    }

    /// <summary>Repo-relative file the user adds for this id. Shown when the file is not in the build output yet.</summary>
    internal static string ExpectedFileHint(string? id)
    {
        var normalized = Normalize(id);
        return normalized == DefaultId
            ? "Assets/Branding/Unplated/AppIcon.ico"
            : $"Assets/Branding/TrayIcons/{normalized}.ico";
    }

    /// <summary>True when this id has its own icon file, before falling back to the default icon.</summary>
    internal static bool HasBundledIcon(string? id, bool dark, string? baseDirectory = null)
        => FirstExisting(OwnIconCandidates(Normalize(id), dark), BaseDirectory(baseDirectory)) is not null;

    /// <summary>
    /// Existing icon for the selection. A missing bundled icon falls back to the current default icon.
    /// </summary>
    internal static string? ResolveExistingPath(string? id, bool dark, string? baseDirectory = null)
    {
        var root = BaseDirectory(baseDirectory);
        var normalized = Normalize(id);
        var own = FirstExisting(OwnIconCandidates(normalized, dark), root);
        if (own is not null || normalized == DefaultId)
            return own;

        return FirstExisting(OwnIconCandidates(DefaultId, dark), root);
    }

    /// <summary>Settings preview. Prefers a png, then the option's own ico. Does not borrow the default icon.</summary>
    internal static string? TryGetPreviewPath(string? id, bool dark, string? baseDirectory = null)
    {
        var root = BaseDirectory(baseDirectory);
        var normalized = Normalize(id);
        return FirstExisting(PreviewCandidates(normalized, dark), root)
            ?? FirstExisting(OwnIconCandidates(normalized, dark), root);
    }

    private static string BaseDirectory(string? baseDirectory)
        => string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;

    private static bool IsBundled(string id)
    {
        foreach (var known in BundledIds)
        {
            if (known == id)
                return true;
        }

        return false;
    }

    private static IEnumerable<string> OwnIconCandidates(string id, bool dark)
    {
        if (IsBundled(id))
        {
            if (dark)
                yield return Path.Combine("Assets", "Branding", "TrayIcons", id + ".dark.ico");
            yield return Path.Combine("Assets", "Branding", "TrayIcons", id + ".ico");
            if (!dark)
                yield return Path.Combine("Assets", "Branding", "TrayIcons", id + ".dark.ico");
            yield break;
        }

        yield return Path.Combine(
            "Assets", "Branding", "Unplated", dark ? "AppIcon.Dark.ico" : "AppIcon.ico");
        yield return "AppIcon.ico";
    }

    private static IEnumerable<string> PreviewCandidates(string id, bool dark)
    {
        if (IsBundled(id))
        {
            if (dark)
                yield return Path.Combine("Assets", "Branding", "TrayIcons", id + ".dark.png");
            yield return Path.Combine("Assets", "Branding", "TrayIcons", id + ".png");
            if (!dark)
                yield return Path.Combine("Assets", "Branding", "TrayIcons", id + ".dark.png");
            yield break;
        }

        // The 32px tray files are the same artwork as these 256px previews.
        if (dark)
        {
            yield return Path.Combine("Assets", "Branding", "Dark", "Icon-256.png");
            yield return Path.Combine("Assets", "Branding", "Dark", "TrayIcon.png");
            yield return Path.Combine("Assets", "Branding", "Dark", "Icon-32.png");
        }

        yield return Path.Combine("Assets", "Square44x44Logo.targetsize-256.png");
        yield return "TrayIcon.png";
        yield return Path.Combine("Assets", "Square44x44Logo.targetsize-32.png");
    }

    private static string? FirstExisting(IEnumerable<string> relativePaths, string root)
    {
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(root, relative);
            if (File.Exists(full))
                return full;
        }

        return null;
    }
}
