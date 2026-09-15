using System.Text.Json;

namespace Easydict.BobPlugin.Runtime.Bridges;

/// <summary>
/// Backs <c>$file</c>. Writes are confined to the instance's sandbox directory and reads may also
/// reach the plugin's own package directory, so a plugin can ship data files but cannot read or
/// write anywhere else on disk.
/// </summary>
internal sealed class FileBridge
{
    /// <summary>Prefix Bob plugins use for their private storage.</summary>
    public const string SandboxPrefix = "$sandbox/";

    private const long MaxWriteBytes = 8 * 1024 * 1024;

    private readonly string _sandboxRoot;
    private readonly string _packageRoot;
    private readonly IBobHostLogger _logger;

    public FileBridge(string sandboxDirectory, string packageDirectory, IBobHostLogger logger)
    {
        _sandboxRoot = EnsureTrailingSeparator(Path.GetFullPath(sandboxDirectory));
        _packageRoot = EnsureTrailingSeparator(Path.GetFullPath(packageDirectory));
        _logger = logger;
    }

    /// <summary>Read a file as base64, or <c>null</c> when it is missing or out of bounds.</summary>
    public string? ReadBase64(string path)
    {
        if (!TryResolve(path, forWrite: false, out var resolved))
        {
            return null;
        }

        try
        {
            return File.Exists(resolved) ? Convert.ToBase64String(File.ReadAllBytes(resolved)) : null;
        }
        catch (IOException ex)
        {
            _logger.Log("warn", $"$file.read failed for '{path}': {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Log("warn", $"$file.read denied for '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Write base64 content into the sandbox.</summary>
    public bool WriteBase64(string path, string base64)
    {
        if (!TryResolve(path, forWrite: true, out var resolved))
        {
            return false;
        }

        var bytes = DataCodec.FromBase64(base64);
        if (bytes.LongLength > MaxWriteBytes)
        {
            _logger.Log("warn", $"$file.write rejected: {bytes.LongLength} bytes exceeds the {MaxWriteBytes} byte limit.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            File.WriteAllBytes(resolved, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log("warn", $"$file.write failed for '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>True when the path exists and is readable by this plugin.</summary>
    public bool Exists(string path)
        => TryResolve(path, forWrite: false, out var resolved) && (File.Exists(resolved) || Directory.Exists(resolved));

    /// <summary>Delete a file inside the sandbox.</summary>
    public bool Delete(string path)
    {
        if (!TryResolve(path, forWrite: true, out var resolved))
        {
            return false;
        }

        try
        {
            if (File.Exists(resolved))
            {
                File.Delete(resolved);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log("warn", $"$file.delete failed for '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Create a directory inside the sandbox.</summary>
    public bool CreateDirectory(string path)
    {
        if (!TryResolve(path, forWrite: true, out var resolved))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(resolved);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log("warn", $"$file.mkdir failed for '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>List a directory's entries as a JSON array of names.</summary>
    public string List(string path)
    {
        if (!TryResolve(path, forWrite: false, out var resolved) || !Directory.Exists(resolved))
        {
            return "[]";
        }

        try
        {
            var names = Directory.GetFileSystemEntries(resolved).Select(Path.GetFileName).ToList();
            return JsonSerializer.Serialize(names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log("warn", $"$file.list failed for '{path}': {ex.Message}");
            return "[]";
        }
    }

    /// <summary>
    /// Map a plugin path to a real one. Writes may only land in the sandbox; reads may also come
    /// from the package directory. Anything that escapes either root is refused.
    /// </summary>
    private bool TryResolve(string? path, bool forWrite, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var relative = path.Trim().Replace('\\', '/');
        var root = _sandboxRoot;

        if (relative.StartsWith(SandboxPrefix, StringComparison.OrdinalIgnoreCase))
        {
            relative = relative.Substring(SandboxPrefix.Length);
        }
        else if (Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            _logger.Log("warn", $"$file refused absolute path '{path}'.");
            return false;
        }
        else if (!forWrite)
        {
            // Plain relative reads resolve against the package first so plugins can ship data files.
            var packageCandidate = Path.GetFullPath(Path.Combine(_packageRoot, relative));
            if (packageCandidate.StartsWith(_packageRoot, StringComparison.OrdinalIgnoreCase)
                && File.Exists(packageCandidate))
            {
                resolved = packageCandidate;
                return true;
            }
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log("warn", $"$file refused path outside the sandbox: '{path}'.");
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
