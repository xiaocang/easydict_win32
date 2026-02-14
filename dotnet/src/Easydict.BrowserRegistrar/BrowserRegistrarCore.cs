using System.Text.Json;
using Microsoft.Win32;

namespace Easydict.BrowserRegistrar;

/// <summary>
/// Core registration logic — testable with injectable paths.
/// Handles: bridge deployment, manifest generation, registry operations.
/// </summary>
internal sealed class BrowserRegistrarCore
{
    internal const string NativeHostName = "com.easydict.bridge";
    internal const string BridgeExeName = "easydict-native-bridge.exe";

    internal static readonly string ChromeRegistryPath =
        $@"Software\Google\Chrome\NativeMessagingHosts\{NativeHostName}";

    internal static readonly string FirefoxRegistryPath =
        $@"Software\Mozilla\NativeMessagingHosts\{NativeHostName}";

    private readonly string _bridgeDirectory;

    internal BrowserRegistrarCore(string bridgeDirectory)
    {
        _bridgeDirectory = bridgeDirectory;
    }

    internal string BridgeExePath => Path.Combine(_bridgeDirectory, BridgeExeName);

    // ───────────────────── Install ─────────────────────

    internal InstallResult Install(bool chrome, bool firefox, string? sourceBridgePath,
        string[] chromeExtIds, string firefoxExtId)
    {
        // Resolve bridge source
        if (string.IsNullOrEmpty(sourceBridgePath))
            sourceBridgePath = Path.Combine(AppContext.BaseDirectory, BridgeExeName);

        if (!File.Exists(sourceBridgePath))
            return new InstallResult(false, Error: $"Bridge exe not found: {sourceBridgePath}");

        // Deploy bridge to stable location
        Directory.CreateDirectory(_bridgeDirectory);
        File.Copy(sourceBridgePath, BridgeExePath, overwrite: true);

        var installed = new List<string>();

        if (chrome)
        {
            var manifestPath = WriteChromeManifest(chromeExtIds);
            WriteRegistryKey(ChromeRegistryPath, manifestPath);
            installed.Add("chrome");
        }

        if (firefox)
        {
            var manifestPath = WriteFirefoxManifest(firefoxExtId);
            WriteRegistryKey(FirefoxRegistryPath, manifestPath);
            installed.Add("firefox");
        }

        return new InstallResult(true, Installed: installed, BridgePath: BridgeExePath);
    }

    // ───────────────────── Uninstall ─────────────────────

    internal UninstallResult Uninstall(bool chrome, bool firefox)
    {
        var uninstalled = new List<string>();

        if (chrome)
        {
            DeleteRegistryKey(ChromeRegistryPath);
            DeleteFile(Path.Combine(_bridgeDirectory, "chrome-manifest.json"));
            uninstalled.Add("chrome");
        }

        if (firefox)
        {
            DeleteRegistryKey(FirefoxRegistryPath);
            DeleteFile(Path.Combine(_bridgeDirectory, "firefox-manifest.json"));
            uninstalled.Add("firefox");
        }

        // Clean up bridge directory if no browser remains registered
        if (!IsRegistered(ChromeRegistryPath) && !IsRegistered(FirefoxRegistryPath))
        {
            try
            {
                if (Directory.Exists(_bridgeDirectory))
                    Directory.Delete(_bridgeDirectory, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }

        return new UninstallResult(true, uninstalled);
    }

    // ───────────────────── Status ─────────────────────

    internal StatusResult GetStatus()
    {
        return new StatusResult(
            ChromeInstalled: IsRegistered(ChromeRegistryPath),
            FirefoxInstalled: IsRegistered(FirefoxRegistryPath),
            BridgeExists: File.Exists(BridgeExePath),
            BridgeDirectory: _bridgeDirectory);
    }

    // ───────────────────── Manifest Generation ─────────────────────

    internal string WriteChromeManifest(string[] chromeExtIds)
    {
        var manifest = new ChromeManifest(
            NativeHostName,
            "Easydict native messaging bridge",
            BridgeExePath,
            "stdio",
            chromeExtIds.Select(id => $"chrome-extension://{id}/").ToArray());

        var path = Path.Combine(_bridgeDirectory, "chrome-manifest.json");
        WriteManifestFile(path, manifest);
        return path;
    }

    internal string WriteFirefoxManifest(string firefoxExtId)
    {
        var manifest = new FirefoxManifest(
            NativeHostName,
            "Easydict native messaging bridge",
            BridgeExePath,
            "stdio",
            [firefoxExtId]);

        var path = Path.Combine(_bridgeDirectory, "firefox-manifest.json");
        WriteManifestFile(path, manifest);
        return path;
    }

    private static void WriteManifestFile(string path, ChromeManifest data)
    {
        var json = JsonSerializer.Serialize(data, AppJsonContext.IndentedDefault.ChromeManifest);
        File.WriteAllText(path, json);
    }

    private static void WriteManifestFile(string path, FirefoxManifest data)
    {
        var json = JsonSerializer.Serialize(data, AppJsonContext.IndentedDefault.FirefoxManifest);
        File.WriteAllText(path, json);
    }

    // ───────────────────── Registry ─────────────────────

    internal static void WriteRegistryKey(string registryPath, string manifestAbsolutePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath);
        key.SetValue(null, manifestAbsolutePath);
    }

    internal static void DeleteRegistryKey(string registryPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKey(registryPath, throwOnMissingSubKey: false);
        }
        catch { /* best effort */ }
    }

    internal static bool IsRegistered(string registryPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            if (key?.GetValue(null) is not string manifestPath)
                return false;

            if (!File.Exists(manifestPath))
                return false;

            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("path", out var pathProp))
            {
                var exePath = pathProp.GetString();
                return !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
            }
        }
        catch { }
        return false;
    }

    // ───────────────────── Helpers ─────────────────────

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }

    // ───────────────────── Result Types ─────────────────────

    internal sealed record InstallResult(
        bool Success,
        List<string>? Installed = null,
        string? BridgePath = null,
        string? Error = null);

    internal sealed record UninstallResult(bool Success, List<string> Uninstalled);

    internal sealed record StatusResult(
        bool ChromeInstalled,
        bool FirefoxInstalled,
        bool BridgeExists,
        string BridgeDirectory);
}
