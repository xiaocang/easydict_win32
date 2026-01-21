using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service for setting window icons in unpackaged WinUI3 applications.
/// Uses dual-path loading: embedded resource (primary) and file fallback (secondary).
/// </summary>
public static class WindowIconService
{
    /// <summary>
    /// Sets the window icon using dual-path loading strategy.
    /// First attempts to load from embedded resource, falls back to file if needed.
    /// </summary>
    /// <param name="appWindow">The AppWindow instance to set the icon for.</param>
    public static void SetWindowIcon(AppWindow? appWindow)
    {
        if (appWindow == null)
        {
            System.Diagnostics.Debug.WriteLine("[Icon] AppWindow is null, cannot set icon");
            return;
        }

        try
        {
            // Primary path: Try to load icon from embedded resource
            var iconId = GetEmbeddedIconId();
            if (iconId != null)
            {
                appWindow.SetIcon(iconId);
                System.Diagnostics.Debug.WriteLine("[Icon] Successfully set icon from embedded resource");
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Icon] Failed to load embedded icon: {ex.Message}");
        }

        try
        {
            // Fallback path: Try to load icon from file
            SetIconFromFile(appWindow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Icon] Failed to load icon from file: {ex.Message}");
            // Continue without icon - non-critical failure
        }
    }

    /// <summary>
    /// Load icon from embedded resource using Win32 API.
    /// Returns IconId if successful, null otherwise.
    /// </summary>
    private static Microsoft.UI.IconId? GetEmbeddedIconId()
    {
        try
        {
            // Get handle to current module (the EXE)
            var hModule = NativeMethods.GetModuleHandle(null);
            if (hModule == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[Icon] GetModuleHandle returned null");
                return null;
            }

            // Load icon from resource ID 32512 (standard ApplicationIcon)
            // This is the resource ID that MSBuild uses when embedding ApplicationIcon
            const int IDI_APPLICATION = 32512;
            var hIcon = NativeMethods.LoadIcon(hModule, IDI_APPLICATION);
            if (hIcon == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[Icon] LoadIcon returned null for resource ID 32512");
                return null;
            }

            // Convert HICON to IconId using WinUI3 interop
            var iconId = Microsoft.UI.Win32Interop.GetIconIdFromIcon(hIcon);
            return iconId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Icon] Exception in GetEmbeddedIconId: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fallback method to load icon from AppIcon.ico file.
    /// This file is generated and copied by the build process.
    /// </summary>
    private static void SetIconFromFile(AppWindow appWindow)
    {
        const string iconFileName = "AppIcon.ico";
        var iconPath = Path.Combine(AppContext.BaseDirectory, iconFileName);

        if (!File.Exists(iconPath))
        {
            System.Diagnostics.Debug.WriteLine($"[Icon] Icon file not found at: {iconPath}");
            return;
        }

        appWindow.SetIcon(iconPath);
        System.Diagnostics.Debug.WriteLine($"[Icon] Successfully set icon from file: {iconPath}");
    }

    /// <summary>
    /// Win32 interop methods for loading icons from resources.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// Retrieves a module handle for the specified module.
        /// </summary>
        /// <param name="lpModuleName">
        /// The name of the loaded module (DLL or EXE).
        /// If null, returns handle to the current process's executable.
        /// </param>
        /// <returns>Handle to the module, or IntPtr.Zero on failure.</returns>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>
        /// Loads an icon resource from the specified module.
        /// </summary>
        /// <param name="hInstance">Handle to the module containing the icon.</param>
        /// <param name="lpIconName">
        /// The icon resource ID.
        /// For standard application icon (ApplicationIcon in .csproj), use 32512.
        /// </param>
        /// <returns>Handle to the icon, or IntPtr.Zero on failure.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);
    }
}
