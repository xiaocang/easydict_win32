using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service for managing Windows Explorer context menu registration.
/// Adds "Translate with Easydict" to the right-click menu for all file types.
/// When clicked, the running instance receives selected text via named pipe IPC.
/// </summary>
public static class ContextMenuService
{
    private const string AppName = "Easydict";
    private const string RegistryKeyPath = @"SOFTWARE\Classes\*\shell\EasydictTranslate";
    private const string CommandSubKey = "command";

    /// <summary>
    /// Checks if the context menu entry is currently registered.
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers or unregisters the context menu entry.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Register();
        }
        else
        {
            Unregister();
        }
    }

    private static void Register()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var shellKey = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (shellKey == null) return;

            shellKey.SetValue("", "Translate with Easydict");
            shellKey.SetValue("Icon", $"\"{exePath}\",0");

            using var commandKey = shellKey.CreateSubKey(CommandSubKey);
            commandKey?.SetValue("", $"\"{exePath}\" --translate-clipboard");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContextMenu] Failed to register: {ex.Message}");
        }
    }

    private static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContextMenu] Failed to unregister: {ex.Message}");
        }
    }
}
