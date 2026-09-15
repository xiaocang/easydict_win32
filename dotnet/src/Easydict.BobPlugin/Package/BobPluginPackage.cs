using System.IO.Compression;
using Easydict.BobPlugin.Manifest;

namespace Easydict.BobPlugin.Package;

/// <summary>Bounds applied when extracting an untrusted plugin archive.</summary>
/// <param name="MaxEntries">Maximum number of entries in the archive.</param>
/// <param name="MaxTotalUncompressedBytes">Maximum total uncompressed size.</param>
/// <param name="MaxEntryUncompressedBytes">Maximum uncompressed size of a single entry.</param>
public sealed record BobPackageLimits(
    int MaxEntries = 2_000,
    long MaxTotalUncompressedBytes = 64L * 1024 * 1024,
    long MaxEntryUncompressedBytes = 32L * 1024 * 1024)
{
    public static BobPackageLimits Default { get; } = new();
}

/// <summary>
/// An extracted plugin on disk: its manifest, entry script and icon.
/// A .bobplugin file is a zip archive; extraction is bounded and refuses entries that would
/// escape the destination directory.
/// </summary>
public sealed class BobPluginPackage
{
    /// <summary>Manifest file name inside a plugin package.</summary>
    public const string ManifestFileName = "info.json";

    /// <summary>Entry script file name inside a plugin package.</summary>
    public const string MainScriptFileName = "main.js";

    private BobPluginPackage(string directory, BobPluginManifest manifest, string mainScriptPath, string? iconPath)
    {
        Directory = directory;
        Manifest = manifest;
        MainScriptPath = mainScriptPath;
        IconPath = iconPath;
    }

    /// <summary>Directory holding the extracted plugin files.</summary>
    public string Directory { get; }

    /// <summary>Parsed info.json.</summary>
    public BobPluginManifest Manifest { get; }

    /// <summary>Absolute path of main.js.</summary>
    public string MainScriptPath { get; }

    /// <summary>Absolute path of the plugin icon, when it ships one.</summary>
    public string? IconPath { get; }

    /// <summary>
    /// Extract a .bobplugin archive into <paramref name="destinationDirectory"/> and load it.
    /// </summary>
    public static BobPluginPackage Extract(string archivePath, string destinationDirectory, BobPackageLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        limits ??= BobPackageLimits.Default;

        if (!File.Exists(archivePath))
        {
            throw new BobPluginException(BobPluginError.InvalidPackage, $"Plugin package not found: {archivePath}");
        }

        var destinationRoot = System.IO.Directory.CreateDirectory(destinationDirectory).FullName;
        var rootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            if (archive.Entries.Count > limits.MaxEntries)
            {
                throw new BobPluginException(
                    BobPluginError.InvalidPackage,
                    $"Plugin package has {archive.Entries.Count} entries, more than the {limits.MaxEntries} allowed.");
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;   // directory entry
                }

                if (entry.Length > limits.MaxEntryUncompressedBytes)
                {
                    throw new BobPluginException(
                        BobPluginError.InvalidPackage,
                        $"Plugin entry '{entry.FullName}' is larger than the {limits.MaxEntryUncompressedBytes} byte limit.");
                }

                total += entry.Length;
                if (total > limits.MaxTotalUncompressedBytes)
                {
                    throw new BobPluginException(
                        BobPluginError.InvalidPackage,
                        $"Plugin package expands to more than the {limits.MaxTotalUncompressedBytes} byte limit.");
                }

                var targetPath = ResolveEntryPath(rootWithSeparator, entry.FullName);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }
        catch (BobPluginException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new BobPluginException(BobPluginError.InvalidPackage, $"Plugin package is not a readable archive: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new BobPluginException(BobPluginError.InvalidPackage, $"Failed to extract plugin package: {ex.Message}", ex);
        }

        return LoadFromDirectory(destinationRoot);
    }

    /// <summary>Load an already-extracted plugin directory.</summary>
    public static BobPluginPackage LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = FindPluginRoot(directory)
            ?? throw new BobPluginException(
                BobPluginError.InvalidPackage,
                $"No {ManifestFileName} found in '{directory}'.");

        var manifest = BobPluginManifest.Parse(File.ReadAllText(Path.Combine(root, ManifestFileName)));

        var mainScript = Path.Combine(root, MainScriptFileName);
        if (!File.Exists(mainScript))
        {
            throw new BobPluginException(BobPluginError.ScriptLoadFailed, $"Plugin is missing {MainScriptFileName}.");
        }

        return new BobPluginPackage(root, manifest, mainScript, FindIcon(root, manifest));
    }

    /// <summary>
    /// Locate the directory holding info.json: either the given directory, or a single wrapping
    /// folder, which is how archives created from a checkout are usually shaped.
    /// </summary>
    private static string? FindPluginRoot(string directory)
    {
        if (File.Exists(Path.Combine(directory, ManifestFileName)))
        {
            return Path.GetFullPath(directory);
        }

        var subdirectories = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetDirectories(directory)
            : [];
        if (subdirectories.Length == 1 && File.Exists(Path.Combine(subdirectories[0], ManifestFileName)))
        {
            return Path.GetFullPath(subdirectories[0]);
        }

        return null;
    }

    private static string? FindIcon(string root, BobPluginManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            // manifest.Icon is untrusted plugin content, not a validated zip entry name, so it gets
            // the same root-escape check applied during extraction (an absolute path or "../"
            // segment must not resolve outside the plugin's own directory).
            var declared = TryResolveWithinRoot(root, manifest.Icon);
            if (declared is not null && File.Exists(declared))
            {
                return declared;
            }
        }

        foreach (var candidate in new[] { "icon.png", "icon.jpg", "icon.jpeg" })
        {
            var path = Path.Combine(root, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve a manifest-declared relative path against <paramref name="root"/>, returning
    /// <c>null</c> for an absolute path or any path that would escape <paramref name="root"/>.
    /// </summary>
    private static string? TryResolveWithinRoot(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            return null;
        }

        var rootFull = Path.GetFullPath(root);
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        var fullPath = Path.GetFullPath(Path.Combine(rootWithSeparator, normalized));
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>
    /// Map an archive entry name to a path inside the destination, rejecting absolute paths and
    /// any name that escapes the destination directory (zip slip).
    /// </summary>
    private static string ResolveEntryPath(string destinationRootWithSeparator, string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new BobPluginException(BobPluginError.InvalidPackage, $"Plugin entry '{entryName}' has an absolute path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(destinationRootWithSeparator, normalized));
        if (!fullPath.StartsWith(destinationRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new BobPluginException(BobPluginError.InvalidPackage, $"Plugin entry '{entryName}' escapes the install directory.");
        }

        return fullPath;
    }
}
