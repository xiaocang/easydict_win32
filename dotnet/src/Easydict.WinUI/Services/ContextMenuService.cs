using System.Diagnostics;
using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages Windows Shell context menu registration via the registry.
/// Adds "OCR Translate" to File Explorer and Desktop right-click menus.
///
/// Registry entries created (HKCU, no admin required):
///   HKCU\Software\Classes\*\shell\EasydictOCR                     — right-click any file
///   HKCU\Software\Classes\Directory\Background\shell\EasydictOCR  — right-click folder/desktop background
///
/// The command launches the app with --ocr-translate. If the app is already running,
/// Program.cs signals it via a named event and exits immediately.
/// </summary>
public static class ContextMenuService
{
    private const string MenuKeyName = "EasydictOCR";
    private const string MenuText = "OCR 截图翻译";

    /// <summary>
    /// Registry paths under HKCU\Software\Classes where context menu entries are created.
    /// </summary>
    private static readonly string[] RegistryPaths =
    [
        @"Software\Classes\*\shell",                    // Right-click any file
        @"Software\Classes\Directory\Background\shell",  // Right-click folder/desktop background
    ];

    /// <summary>
    /// Whether the context menu is currently registered.
    /// </summary>
    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{RegistryPaths[0]}\{MenuKeyName}");
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Register the "OCR Translate" context menu entry in Windows Shell.
    /// Uses HKCU so no admin elevation is required.
    /// </summary>
    public static void Register()
    {
        var appPath = GetAppPath();
        if (string.IsNullOrEmpty(appPath))
        {
            Debug.WriteLine("[ContextMenu] Cannot register: app path not found");
            return;
        }

        var command = $"\"{appPath}\" --ocr-translate";

        foreach (var basePath in RegistryPaths)
        {
            try
            {
                using var shellKey = Registry.CurrentUser.CreateSubKey($@"{basePath}\{MenuKeyName}");
                shellKey.SetValue(null, MenuText);
                shellKey.SetValue("Icon", appPath);

                using var cmdKey = Registry.CurrentUser.CreateSubKey($@"{basePath}\{MenuKeyName}\command");
                cmdKey.SetValue(null, command);

                Debug.WriteLine($"[ContextMenu] Registered: HKCU\\{basePath}\\{MenuKeyName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContextMenu] Failed to register {basePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Unregister (remove) the context menu entry from Windows Shell.
    /// </summary>
    public static void Unregister()
    {
        foreach (var basePath in RegistryPaths)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{basePath}\{MenuKeyName}", throwOnMissingSubKey: false);
                Debug.WriteLine($"[ContextMenu] Unregistered: HKCU\\{basePath}\\{MenuKeyName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContextMenu] Failed to unregister {basePath}: {ex.Message}");
            }
        }
    }

    private static string? GetAppPath()
    {
        try
        {
            return Environment.ProcessPath;
        }
        catch
        {
            return null;
        }
    }
}
