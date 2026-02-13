using System.Text.Json;
using Microsoft.Win32;

namespace Easydict.BrowserRegistrar;

/// <summary>
/// Standalone browser Native Messaging host registrar.
/// Runs outside the MSIX sandbox to write real HKCU registry keys.
///
/// Usage:
///   BrowserHostRegistrar install [--chrome] [--firefox] [--bridge-path PATH]
///                                [--chrome-ext-id ID] [--firefox-ext-id ID]
///   BrowserHostRegistrar uninstall [--chrome] [--firefox]
///   BrowserHostRegistrar status
///
/// Output: JSON to stdout for the calling process to parse.
/// </summary>
public static class Program
{
    private const string NativeHostName = "com.easydict.bridge";
    private const string BridgeExeName = "easydict-native-bridge.exe";
    private const string BridgeDirName = "browser-bridge";

    private const string DefaultChromeExtId = "dmokdfinnomehfpmhoeekomncpobgagf";
    private const string DefaultFirefoxExtId = "easydict-ocr@easydict.app";

    private static readonly string ChromeRegistryPath =
        $@"Software\Google\Chrome\NativeMessagingHosts\{NativeHostName}";

    private static readonly string FirefoxRegistryPath =
        $@"Software\Mozilla\NativeMessagingHosts\{NativeHostName}";

    private static string BridgeDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Easydict", BridgeDirName);
        }
    }

    private static string BridgeExePath => Path.Combine(BridgeDirectory, BridgeExeName);

    static int Main(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();

        var command = args[0].ToLowerInvariant();
        var chrome = HasFlag(args, "--chrome");
        var firefox = HasFlag(args, "--firefox");
        var bridgePath = GetArgValue(args, "--bridge-path");
        var chromeExtId = GetArgValue(args, "--chrome-ext-id") ?? DefaultChromeExtId;
        var firefoxExtId = GetArgValue(args, "--firefox-ext-id") ?? DefaultFirefoxExtId;

        // Default to both browsers if neither specified
        if (!chrome && !firefox && command != "status")
        {
            chrome = true;
            firefox = true;
        }

        try
        {
            return command switch
            {
                "install" => Install(chrome, firefox, bridgePath, chromeExtId, firefoxExtId),
                "uninstall" => Uninstall(chrome, firefox),
                "status" => PrintStatus(),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            WriteJson(new { success = false, error = ex.Message });
            return 1;
        }
    }

    // ───────────────────── Install ─────────────────────

    private static int Install(bool chrome, bool firefox, string? bridgePath,
        string chromeExtId, string firefoxExtId)
    {
        // If no explicit bridge path, look next to this registrar exe
        if (string.IsNullOrEmpty(bridgePath))
            bridgePath = Path.Combine(AppContext.BaseDirectory, BridgeExeName);

        if (!File.Exists(bridgePath))
        {
            WriteJson(new { success = false, error = $"Bridge exe not found: {bridgePath}" });
            return 1;
        }

        // Deploy bridge to stable location
        Directory.CreateDirectory(BridgeDirectory);
        File.Copy(bridgePath, BridgeExePath, overwrite: true);

        var installed = new List<string>();

        if (chrome)
        {
            var manifestPath = WriteChromeManifest(chromeExtId);
            WriteRegistryKey(ChromeRegistryPath, manifestPath);
            installed.Add("chrome");
        }

        if (firefox)
        {
            var manifestPath = WriteFirefoxManifest(firefoxExtId);
            WriteRegistryKey(FirefoxRegistryPath, manifestPath);
            installed.Add("firefox");
        }

        WriteJson(new
        {
            success = true,
            installed,
            bridge_path = BridgeExePath
        });
        return 0;
    }

    // ───────────────────── Uninstall ─────────────────────

    private static int Uninstall(bool chrome, bool firefox)
    {
        var uninstalled = new List<string>();

        if (chrome)
        {
            DeleteRegistryKey(ChromeRegistryPath);
            DeleteFile(Path.Combine(BridgeDirectory, "chrome-manifest.json"));
            uninstalled.Add("chrome");
        }

        if (firefox)
        {
            DeleteRegistryKey(FirefoxRegistryPath);
            DeleteFile(Path.Combine(BridgeDirectory, "firefox-manifest.json"));
            uninstalled.Add("firefox");
        }

        // Clean up bridge directory if no browser remains registered
        if (!IsRegistered(ChromeRegistryPath) && !IsRegistered(FirefoxRegistryPath))
        {
            try
            {
                if (Directory.Exists(BridgeDirectory))
                    Directory.Delete(BridgeDirectory, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }

        WriteJson(new { success = true, uninstalled });
        return 0;
    }

    // ───────────────────── Status ─────────────────────

    private static int PrintStatus()
    {
        WriteJson(new
        {
            chrome = new { installed = IsRegistered(ChromeRegistryPath) },
            firefox = new { installed = IsRegistered(FirefoxRegistryPath) },
            bridge_exists = File.Exists(BridgeExePath),
            bridge_directory = BridgeDirectory
        });
        return 0;
    }

    // ───────────────────── Manifest Generation ─────────────────────

    private static string WriteChromeManifest(string chromeExtId)
    {
        var manifest = new
        {
            name = NativeHostName,
            description = "Easydict native messaging bridge",
            path = BridgeExePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{chromeExtId}/" }
        };

        var path = Path.Combine(BridgeDirectory, "chrome-manifest.json");
        WriteManifestFile(path, manifest);
        return path;
    }

    private static string WriteFirefoxManifest(string firefoxExtId)
    {
        var manifest = new
        {
            name = NativeHostName,
            description = "Easydict native messaging bridge",
            path = BridgeExePath,
            type = "stdio",
            allowed_extensions = new[] { firefoxExtId }
        };

        var path = Path.Combine(BridgeDirectory, "firefox-manifest.json");
        WriteManifestFile(path, manifest);
        return path;
    }

    private static void WriteManifestFile(string path, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        File.WriteAllText(path, json);
    }

    // ───────────────────── Registry ─────────────────────

    private static void WriteRegistryKey(string registryPath, string manifestAbsolutePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath);
        key.SetValue(null, manifestAbsolutePath);
    }

    private static void DeleteRegistryKey(string registryPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKey(registryPath, throwOnMissingSubKey: false);
        }
        catch { /* best effort */ }
    }

    private static bool IsRegistered(string registryPath)
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

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static void WriteJson(object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        Console.WriteLine(json);
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            BrowserHostRegistrar - Easydict browser Native Messaging host registrar

            Usage:
              BrowserHostRegistrar install [options]    Register native messaging host
              BrowserHostRegistrar uninstall [options]  Remove native messaging host
              BrowserHostRegistrar status               Show installation status

            Options:
              --chrome              Target Chrome/Edge (default: both)
              --firefox             Target Firefox (default: both)
              --bridge-path PATH    Path to easydict-native-bridge.exe
              --chrome-ext-id ID    Chrome extension ID (default: built-in)
              --firefox-ext-id ID   Firefox extension ID (default: built-in)
            """);
        return 1;
    }
}
