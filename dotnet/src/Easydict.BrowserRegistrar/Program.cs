using System.Text.Json;

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
    private const string DefaultChromeExtId = "dmokdfinnomehfpmhoeekomncpobgagf";
    private const string DefaultFirefoxExtId = "easydict-ocr@easydict.app";

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

        var core = new BrowserRegistrarCore(GetDefaultBridgeDirectory());

        try
        {
            return command switch
            {
                "install" => DoInstall(core, chrome, firefox, bridgePath, chromeExtId, firefoxExtId),
                "uninstall" => DoUninstall(core, chrome, firefox),
                "status" => DoStatus(core),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            WriteJson(new { success = false, error = ex.Message });
            return 1;
        }
    }

    private static int DoInstall(BrowserRegistrarCore core, bool chrome, bool firefox,
        string? bridgePath, string chromeExtId, string firefoxExtId)
    {
        var result = core.Install(chrome, firefox, bridgePath, chromeExtId, firefoxExtId);

        if (!result.Success)
        {
            WriteJson(new { success = false, error = result.Error });
            return 1;
        }

        WriteJson(new
        {
            success = true,
            installed = result.Installed,
            bridge_path = result.BridgePath
        });
        return 0;
    }

    private static int DoUninstall(BrowserRegistrarCore core, bool chrome, bool firefox)
    {
        var result = core.Uninstall(chrome, firefox);
        WriteJson(new { success = true, uninstalled = result.Uninstalled });
        return 0;
    }

    private static int DoStatus(BrowserRegistrarCore core)
    {
        var status = core.GetStatus();
        WriteJson(new
        {
            chrome = new { installed = status.ChromeInstalled },
            firefox = new { installed = status.FirefoxInstalled },
            bridge_exists = status.BridgeExists,
            bridge_directory = status.BridgeDirectory
        });
        return 0;
    }

    // ───────────────────── Helpers ─────────────────────

    internal static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    internal static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static string GetDefaultBridgeDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Easydict", "browser-bridge");
    }

    internal static void WriteJson(object data)
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
