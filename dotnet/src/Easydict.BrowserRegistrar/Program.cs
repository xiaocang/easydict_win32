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
    internal const string DefaultChromeExtIds = "dmokdfinnomehfpmhoeekomncpobgagf,cbhpnmadpnoedfgonddpmlhaclbicllg";
    private const string DefaultFirefoxExtId = "easydict-ocr@easydict.app";

    static int Main(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();

        var command = args[0].ToLowerInvariant();
        var chrome = HasFlag(args, "--chrome");
        var firefox = HasFlag(args, "--firefox");
        var bridgePath = GetArgValue(args, "--bridge-path");
        var chromeExtIdsRaw = GetArgValue(args, "--chrome-ext-id") ?? DefaultChromeExtIds;
        var chromeExtIds = chromeExtIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                "install" => DoInstall(core, chrome, firefox, bridgePath, chromeExtIds, firefoxExtId),
                "uninstall" => DoUninstall(core, chrome, firefox),
                "status" => DoStatus(core),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            WriteJson(new ErrorOutput(false, ex.Message));
            return 1;
        }
    }

    private static int DoInstall(BrowserRegistrarCore core, bool chrome, bool firefox,
        string? bridgePath, string[] chromeExtIds, string firefoxExtId)
    {
        var result = core.Install(chrome, firefox, bridgePath, chromeExtIds, firefoxExtId);

        if (!result.Success)
        {
            WriteJson(new ErrorOutput(false, result.Error));
            return 1;
        }

        WriteJson(new InstallOutput(true, result.Installed, result.BridgePath));
        return 0;
    }

    private static int DoUninstall(BrowserRegistrarCore core, bool chrome, bool firefox)
    {
        var result = core.Uninstall(chrome, firefox);
        WriteJson(new UninstallOutput(true, result.Uninstalled));
        return 0;
    }

    private static int DoStatus(BrowserRegistrarCore core)
    {
        var status = core.GetStatus();
        WriteJson(new StatusOutput(
            new BrowserStatusEntry(status.ChromeInstalled),
            new BrowserStatusEntry(status.FirefoxInstalled),
            status.BridgeExists,
            status.BridgeDirectory));
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

    internal static void WriteJson(ErrorOutput data) =>
        Console.WriteLine(JsonSerializer.Serialize(data, AppJsonContext.Default.ErrorOutput));

    internal static void WriteJson(InstallOutput data) =>
        Console.WriteLine(JsonSerializer.Serialize(data, AppJsonContext.Default.InstallOutput));

    internal static void WriteJson(UninstallOutput data) =>
        Console.WriteLine(JsonSerializer.Serialize(data, AppJsonContext.Default.UninstallOutput));

    internal static void WriteJson(StatusOutput data) =>
        Console.WriteLine(JsonSerializer.Serialize(data, AppJsonContext.Default.StatusOutput));

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
              --chrome-ext-id IDS   Chrome extension ID(s), comma-separated (default: built-in)
              --firefox-ext-id ID   Firefox extension ID (default: built-in)
            """);
        return 1;
    }
}
