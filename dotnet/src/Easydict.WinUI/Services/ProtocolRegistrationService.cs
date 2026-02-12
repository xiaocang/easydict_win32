using System.Diagnostics;
using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Ensures HKCU protocol registration for easydict:// in unpackaged installs.
/// This is a runtime fallback in case installer registration is missing or stale.
/// </summary>
public static class ProtocolRegistrationService
{
    private const string BaseKeyPath = @"Software\Classes\easydict";
    private const string CommandKeyPath = @"Software\Classes\easydict\shell\open\command";
    private const string ProtocolDescription = "URL:Easydict Protocol";

    public static void EnsureRegistered()
    {
        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
        {
            Log($"[ProtocolRegistration] Skipped: invalid app path '{appPath ?? "(null)"}'");
            return;
        }

        var expectedCommand = $"\"{appPath}\" \"%1\"";

        try
        {
            using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath);
            using var commandKey = Registry.CurrentUser.CreateSubKey(CommandKeyPath);

            if (baseKey is null || commandKey is null)
            {
                Log("[ProtocolRegistration] Failed: unable to create/open registry keys");
                return;
            }

            var changed = false;

            var currentDescription = baseKey.GetValue(null) as string;
            if (!string.Equals(currentDescription, ProtocolDescription, StringComparison.Ordinal))
            {
                baseKey.SetValue(null, ProtocolDescription, RegistryValueKind.String);
                changed = true;
            }

            if (baseKey.GetValue("URL Protocol") is null)
            {
                baseKey.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
                changed = true;
            }

            var currentCommand = commandKey.GetValue(null) as string;
            if (!string.Equals(currentCommand, expectedCommand, StringComparison.Ordinal))
            {
                commandKey.SetValue(null, expectedCommand, RegistryValueKind.String);
                changed = true;
            }

            if (changed)
            {
                Log($"[ProtocolRegistration] Updated HKCU\\{CommandKeyPath} = {expectedCommand}");
            }
            else
            {
                Log("[ProtocolRegistration] Already valid; no changes required");
            }
        }
        catch (Exception ex)
        {
            Log($"[ProtocolRegistration] Failed: {ex}");
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine(message);

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "debug.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] {message}\n");
        }
        catch
        {
            // Logging must not throw.
        }
    }
}
