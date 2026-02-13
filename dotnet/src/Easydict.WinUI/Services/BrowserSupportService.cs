using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages Native Messaging host installation for Chrome and Firefox browser extensions.
///
/// Two installation approaches:
///   1. Local install (non-MSIX): copy bridge exe from app directory + write registry directly.
///   2. Registrar install (MSIX / universal): copy bundled BrowserHostRegistrar to
///      %LocalAppData% and run it outside the MSIX sandbox to write real HKCU registry keys.
///
/// "Install" does two things:
///   1. Deploy bridge: copy easydict-native-bridge.exe + host manifest JSON to
///      %LocalAppData%\Easydict\browser-bridge\
///   2. Write registry key: HKCU\Software\{Chrome|Mozilla}\NativeMessagingHosts\com.easydict.bridge
///
/// "Uninstall" reverses both steps.
///
/// The bridge exe receives messages from the browser extension via Native Messaging (stdio)
/// and signals the running Easydict app via a named EventWaitHandle.
/// </summary>
public static class BrowserSupportService
{
    private const string NativeHostName = "com.easydict.bridge";
    private const string BridgeExeName = "easydict-native-bridge.exe";
    private const string BridgeDirName = "browser-bridge";
    private const string RegistrarExeName = "BrowserHostRegistrar.exe";

    // Chrome extension ID — assigned by Chrome Web Store
    private const string ChromeExtensionId = "dmokdfinnomehfpmhoeekomncpobgagf";

    // Firefox extension ID — must match gecko.id in manifest.v2.json
    private const string FirefoxExtensionId = "easydict-ocr@easydict.app";

    private static readonly string ChromeRegistryPath =
        $@"Software\Google\Chrome\NativeMessagingHosts\{NativeHostName}";

    private static readonly string FirefoxRegistryPath =
        $@"Software\Mozilla\NativeMessagingHosts\{NativeHostName}";

    /// <summary>
    /// Directory where the bridge exe and host manifests are deployed.
    /// %LocalAppData%\Easydict\browser-bridge\
    /// </summary>
    private static string BridgeDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Easydict", BridgeDirName);
        }
    }

    private static string BridgeExePath => Path.Combine(BridgeDirectory, BridgeExeName);

    // ───────────────────── Status Detection ─────────────────────

    /// <summary>
    /// Checks whether Chrome Native Messaging support is fully installed:
    ///   1. Registry key exists and points to a valid manifest
    ///   2. Manifest file exists
    ///   3. Bridge exe referenced in manifest exists
    /// </summary>
    public static bool IsChromeSupportInstalled => IsInstalled(ChromeRegistryPath);

    /// <summary>
    /// Checks whether Firefox Native Messaging support is fully installed.
    /// </summary>
    public static bool IsFirefoxSupportInstalled => IsInstalled(FirefoxRegistryPath);

    private static bool IsInstalled(string registryPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            if (key?.GetValue(null) is not string manifestPath)
                return false;

            if (!File.Exists(manifestPath))
                return false;

            // Verify bridge exe referenced in manifest exists
            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("path", out var pathProp))
            {
                var bridgePath = pathProp.GetString();
                return !string.IsNullOrEmpty(bridgePath) && File.Exists(bridgePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] IsInstalled check failed for {registryPath}: {ex.Message}");
        }
        return false;
    }

    // ───────────────────── Registrar Install (MSIX-safe) ─────────────────────

    /// <summary>
    /// Run the bundled BrowserHostRegistrar to install browser support outside the MSIX sandbox.
    /// The registrar exe is bundled in the app directory alongside the bridge exe.
    /// It is copied to %LocalAppData% before running so it operates outside the MSIX container.
    /// </summary>
    /// <param name="browser">"chrome", "firefox", or "all"</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if registration succeeded.</returns>
    public static async Task<bool> InstallWithRegistrarAsync(string browser, CancellationToken ct = default)
    {
        var registrarPath = DeployRegistrar();
        if (registrarPath == null)
            return false;

        // Use bridge from app's own install directory (works for both MSIX and non-MSIX)
        var localBridgePath = Path.Combine(AppContext.BaseDirectory, BridgeExeName);

        return await RunRegistrarAsync(registrarPath, "install", browser, localBridgePath, ct);
    }

    /// <summary>
    /// Run the bundled BrowserHostRegistrar to uninstall browser support.
    /// Falls back to local uninstall if registrar is not available.
    /// </summary>
    public static async Task<bool> UninstallWithRegistrarAsync(string browser, CancellationToken ct = default)
    {
        var registrarPath = DeployRegistrar();
        if (registrarPath == null)
        {
            Debug.WriteLine("[BrowserSupport] Registrar not available, using local uninstall");
            LocalUninstall(browser);
            return true;
        }

        return await RunRegistrarAsync(registrarPath, "uninstall", browser, bridgePath: null, ct);
    }

    /// <summary>
    /// Copy the bundled BrowserHostRegistrar exe from the app directory to
    /// %LocalAppData%\Easydict\browser-bridge\ so it runs outside the MSIX sandbox.
    /// Returns the deployed path, or null if the registrar is not bundled.
    /// </summary>
    private static string? DeployRegistrar()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, RegistrarExeName);
        if (!File.Exists(sourcePath))
        {
            Debug.WriteLine($"[BrowserSupport] WARNING: Registrar exe not found at {sourcePath}");
            return null;
        }

        Directory.CreateDirectory(BridgeDirectory);
        var destPath = Path.Combine(BridgeDirectory, RegistrarExeName);

        File.Copy(sourcePath, destPath, overwrite: true);
        Debug.WriteLine($"[BrowserSupport] Registrar deployed: {sourcePath} → {destPath}");
        return destPath;
    }

    // ───────────────────── Local Install (non-MSIX fallback) ─────────────────────

    /// <summary>
    /// Install Chrome Native Messaging support using local copy:
    ///   1. Deploy bridge exe and Chrome host manifest
    ///   2. Write Chrome registry key
    /// </summary>
    public static void InstallChrome()
    {
        DeployBridge();
        var manifestPath = WriteChromeManifest();
        WriteRegistryKey(ChromeRegistryPath, manifestPath);
        Debug.WriteLine("[BrowserSupport] Chrome support installed");
    }

    /// <summary>
    /// Install Firefox Native Messaging support using local copy:
    ///   1. Deploy bridge exe and Firefox host manifest
    ///   2. Write Firefox registry key
    /// </summary>
    public static void InstallFirefox()
    {
        DeployBridge();
        var manifestPath = WriteFirefoxManifest();
        WriteRegistryKey(FirefoxRegistryPath, manifestPath);
        Debug.WriteLine("[BrowserSupport] Firefox support installed");
    }

    /// <summary>
    /// Install both Chrome and Firefox support using local copy.
    /// </summary>
    public static void InstallAll()
    {
        DeployBridge();

        var chromeManifest = WriteChromeManifest();
        WriteRegistryKey(ChromeRegistryPath, chromeManifest);

        var firefoxManifest = WriteFirefoxManifest();
        WriteRegistryKey(FirefoxRegistryPath, firefoxManifest);

        Debug.WriteLine("[BrowserSupport] All browser support installed");
    }

    // ───────────────────── Uninstall ─────────────────────

    /// <summary>
    /// Uninstall Chrome Native Messaging support.
    /// </summary>
    public static void UninstallChrome()
    {
        DeleteRegistryKey(ChromeRegistryPath);
        DeleteManifest("chrome-manifest.json");
        CleanupBridgeIfUnused();
        Debug.WriteLine("[BrowserSupport] Chrome support uninstalled");
    }

    /// <summary>
    /// Uninstall Firefox Native Messaging support.
    /// </summary>
    public static void UninstallFirefox()
    {
        DeleteRegistryKey(FirefoxRegistryPath);
        DeleteManifest("firefox-manifest.json");
        CleanupBridgeIfUnused();
        Debug.WriteLine("[BrowserSupport] Firefox support uninstalled");
    }

    /// <summary>
    /// Uninstall all browser support and remove the bridge directory.
    /// </summary>
    public static void UninstallAll()
    {
        DeleteRegistryKey(ChromeRegistryPath);
        DeleteRegistryKey(FirefoxRegistryPath);

        // Delete entire bridge directory
        try
        {
            if (Directory.Exists(BridgeDirectory))
            {
                Directory.Delete(BridgeDirectory, recursive: true);
                Debug.WriteLine($"[BrowserSupport] Deleted bridge directory: {BridgeDirectory}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] Failed to delete bridge directory: {ex.Message}");
        }
    }

    // ───────────────────── Bridge Deployment (local) ─────────────────────

    /// <summary>
    /// Copy bridge exe from app directory to %LocalAppData%\Easydict\browser-bridge\.
    /// MSIX install directory is read-only with versioned paths,
    /// so we copy to a stable LocalAppData location.
    /// </summary>
    private static void DeployBridge()
    {
        Directory.CreateDirectory(BridgeDirectory);

        var sourcePath = Path.Combine(AppContext.BaseDirectory, BridgeExeName);
        var destPath = BridgeExePath;

        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destPath, overwrite: true);
            Debug.WriteLine($"[BrowserSupport] Bridge deployed: {sourcePath} → {destPath}");
        }
        else
        {
            // Bridge exe not found in app directory — may not be built yet.
            // The install will still create manifests and registry keys.
            Debug.WriteLine($"[BrowserSupport] WARNING: Bridge exe not found at {sourcePath}");
        }
    }

    // ───────────────────── Manifest Generation ─────────────────────

    private static string WriteChromeManifest()
    {
        var manifest = new
        {
            name = NativeHostName,
            description = "Easydict native messaging bridge",
            path = BridgeExePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ChromeExtensionId}/" }
        };

        var path = Path.Combine(BridgeDirectory, "chrome-manifest.json");
        WriteJsonFile(path, manifest);
        return path;
    }

    private static string WriteFirefoxManifest()
    {
        var manifest = new
        {
            name = NativeHostName,
            description = "Easydict native messaging bridge",
            path = BridgeExePath,
            type = "stdio",
            allowed_extensions = new[] { FirefoxExtensionId }
        };

        var path = Path.Combine(BridgeDirectory, "firefox-manifest.json");
        WriteJsonFile(path, manifest);
        return path;
    }

    private static void WriteJsonFile(string path, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        File.WriteAllText(path, json);
        Debug.WriteLine($"[BrowserSupport] Wrote manifest: {path}");
    }

    // ───────────────────── Registry ─────────────────────

    private static void WriteRegistryKey(string registryPath, string manifestAbsolutePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            key.SetValue(null, manifestAbsolutePath);
            Debug.WriteLine($"[BrowserSupport] Registry set: HKCU\\{registryPath} → {manifestAbsolutePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] Registry write failed for {registryPath}: {ex.Message}");
        }
    }

    private static void DeleteRegistryKey(string registryPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKey(registryPath, throwOnMissingSubKey: false);
            Debug.WriteLine($"[BrowserSupport] Registry deleted: HKCU\\{registryPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] Registry delete failed for {registryPath}: {ex.Message}");
        }
    }

    // ───────────────────── Cleanup ─────────────────────

    private static void DeleteManifest(string manifestFileName)
    {
        try
        {
            var path = Path.Combine(BridgeDirectory, manifestFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
                Debug.WriteLine($"[BrowserSupport] Deleted manifest: {path}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] Failed to delete manifest {manifestFileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// If neither Chrome nor Firefox is installed, delete the bridge exe
    /// to keep the user's system clean.
    /// </summary>
    private static void CleanupBridgeIfUnused()
    {
        if (IsChromeSupportInstalled || IsFirefoxSupportInstalled)
            return;

        try
        {
            if (Directory.Exists(BridgeDirectory))
            {
                Directory.Delete(BridgeDirectory, recursive: true);
                Debug.WriteLine($"[BrowserSupport] No browsers remain — cleaned up {BridgeDirectory}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] Cleanup failed: {ex.Message}");
        }
    }

    // ───────────────────── Store Pages ─────────────────────

    /// <summary>
    /// Open Chrome Web Store extension page in default browser.
    /// </summary>
    public static void OpenChromeStorePage()
    {
        Process.Start(new ProcessStartInfo($"https://chromewebstore.google.com/detail/{ChromeExtensionId}") { UseShellExecute = true });
    }

    /// <summary>
    /// Open Firefox Add-ons extension page in default browser.
    /// TODO: Replace with actual Firefox Add-ons URL after publishing.
    /// </summary>
    public static void OpenFirefoxStorePage()
    {
        // Placeholder — update after publishing to Firefox Add-ons
        // Process.Start(new ProcessStartInfo("https://addons.mozilla.org/en-US/firefox/addon/ADDON_SLUG/") { UseShellExecute = true });
        Debug.WriteLine("[BrowserSupport] Firefox Add-ons page not yet available (extension not published)");
    }

    // ───────────────────── Registrar Process ─────────────────────

    /// <summary>
    /// Run the BrowserHostRegistrar process with the given command and arguments.
    /// </summary>
    private static async Task<bool> RunRegistrarAsync(
        string registrarPath, string command, string browser, string? bridgePath, CancellationToken ct)
    {
        var args = new List<string> { command };

        switch (browser)
        {
            case "chrome":
                args.Add("--chrome");
                break;
            case "firefox":
                args.Add("--firefox");
                break;
            // "all": omit browser flags → registrar defaults to both
        }

        if (bridgePath != null)
        {
            args.Add("--bridge-path");
            args.Add(bridgePath);
        }

        args.AddRange(new[] { "--chrome-ext-id", ChromeExtensionId });
        args.AddRange(new[] { "--firefox-ext-id", FirefoxExtensionId });

        var argsString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        Debug.WriteLine($"[BrowserSupport] Running registrar: {registrarPath} {argsString}");

        var psi = new ProcessStartInfo
        {
            FileName = registrarPath,
            Arguments = argsString,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            Debug.WriteLine("[BrowserSupport] Failed to start registrar process");
            return false;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        Debug.WriteLine($"[BrowserSupport] Registrar exit code: {process.ExitCode}");
        Debug.WriteLine($"[BrowserSupport] Registrar stdout: {stdout}");
        if (!string.IsNullOrEmpty(stderr))
            Debug.WriteLine($"[BrowserSupport] Registrar stderr: {stderr}");

        return process.ExitCode == 0;
    }

    /// <summary>
    /// Local uninstall helper — used as fallback when registrar is not available.
    /// </summary>
    private static void LocalUninstall(string browser)
    {
        switch (browser)
        {
            case "chrome":
                UninstallChrome();
                break;
            case "firefox":
                UninstallFirefox();
                break;
            case "all":
                UninstallAll();
                break;
        }
    }
}
