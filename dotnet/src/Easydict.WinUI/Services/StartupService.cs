using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service for managing Windows startup registry entries.
/// Uses HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run for user-level startup.
/// </summary>
public static class StartupService
{
    private const string AppName = "Easydict";
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Checks if the application is configured to launch at Windows startup.
    /// </summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return key?.GetValue(AppName) != null;
    }

    /// <summary>
    /// Enables or disables the application's Windows startup entry.
    /// </summary>
    /// <param name="enabled">True to enable startup, false to disable.</param>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (key == null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                // Settings applies this on every save; leave the Run entry alone when it
                // already points at this executable.
                var value = $"\"{exePath}\"";
                if (!string.Equals(key.GetValue(AppName) as string, value, StringComparison.Ordinal))
                {
                    key.SetValue(AppName, value);
                }
            }
        }
        else if (key.GetValue(AppName) != null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
