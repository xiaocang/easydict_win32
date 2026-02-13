using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages Native Messaging host installation for Chrome and Firefox browser extensions.
///
/// Two installation approaches:
///   1. Local install (non-MSIX): copy bridge exe from app directory + write registry directly.
///   2. Download install (MSIX / universal): download BrowserHostRegistrar + bridge from GitHub
///      releases, run the registrar outside the MSIX sandbox to write real HKCU registry keys.
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

    // GitHub release download
    private const string GitHubRepo = "xiaocang/easydict_win32";

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

    /// <summary>
    /// Directory where the registrar and bridge are downloaded before running.
    /// %LocalAppData%\Easydict\downloads\
    /// </summary>
    private static string DownloadDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Easydict", "downloads");
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

    // ───────────────────── Download + Registrar Install ─────────────────────

    /// <summary>
    /// Download BrowserHostRegistrar from its dedicated GitHub release (vr-* tag),
    /// then run it to install browser support outside the MSIX sandbox.
    /// The bridge exe comes from the local app directory (bundled in MSIX / installer).
    /// </summary>
    /// <param name="browser">"chrome", "firefox", or "all"</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if registration succeeded.</returns>
    public static async Task<bool> DownloadAndInstallAsync(string browser, CancellationToken ct = default)
    {
        var registrarPath = await DownloadRegistrarAsync(ct);

        // Use bridge from app's own install directory (works for both MSIX and non-MSIX)
        var localBridgePath = Path.Combine(AppContext.BaseDirectory, BridgeExeName);

        return await RunRegistrarAsync(registrarPath, "install", browser, localBridgePath, ct);
    }

    /// <summary>
    /// Download registrar from GitHub releases and run it to uninstall browser support.
    /// Falls back to local uninstall if download fails.
    /// </summary>
    public static async Task<bool> DownloadAndUninstallAsync(string browser, CancellationToken ct = default)
    {
        var registrarPath = Path.Combine(DownloadDirectory, RegistrarExeName);

        // If registrar was previously downloaded, reuse it
        if (!File.Exists(registrarPath))
        {
            try
            {
                registrarPath = await DownloadRegistrarAsync(ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowserSupport] Registrar download failed, using local uninstall: {ex.Message}");
                LocalUninstall(browser);
                return true;
            }
        }

        return await RunRegistrarAsync(registrarPath, "uninstall", browser, bridgePath: null, ct);
    }

    /// <summary>
    /// Download the registrar exe from the latest vr-* release on GitHub.
    /// </summary>
    private static async Task<string> DownloadRegistrarAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadDirectory);
        var registrarPath = Path.Combine(DownloadDirectory, RegistrarExeName);

        using var httpClient = CreateHttpClient();

        // Find latest registrar release via GitHub API
        var (registrarUrl, checksumUrl) = await FindLatestRegistrarReleaseAsync(httpClient, ct);

        // Download checksum file (optional)
        string? checksumContent = null;
        if (checksumUrl != null)
        {
            try
            {
                checksumContent = await httpClient.GetStringAsync(checksumUrl, ct);
                Debug.WriteLine("[BrowserSupport] Downloaded checksum file");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowserSupport] Checksum file not available: {ex.Message}");
            }
        }

        // Download registrar
        await DownloadFileAsync(httpClient, registrarUrl, registrarPath, ct);

        // Verify SHA256 checksum
        if (checksumContent != null)
        {
            var assetName = registrarUrl.Split('/').Last();
            VerifyChecksum(checksumContent, assetName, registrarPath);
        }

        return registrarPath;
    }

    /// <summary>
    /// Query GitHub releases API to find the latest vr-* tagged release
    /// and return the download URL for the registrar asset matching the current platform.
    /// </summary>
    internal static async Task<(string registrarUrl, string? checksumUrl)> FindLatestRegistrarReleaseAsync(
        HttpClient httpClient, CancellationToken ct)
    {
        var platform = GetPlatform();
        var registrarAssetName = $"BrowserHostRegistrar-{platform}.exe";
        var checksumAssetName = $"browser-support-{platform}.sha256";

        var releasesUrl = $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=30";
        Debug.WriteLine($"[BrowserSupport] Fetching releases: {releasesUrl}");

        var json = await httpClient.GetStringAsync(releasesUrl, ct);
        using var doc = JsonDocument.Parse(json);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString();
            if (tagName == null || !tagName.StartsWith("vr-", StringComparison.Ordinal))
                continue;

            string? registrarUrl = null;
            string? checksumUrl = null;

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();

                if (name == registrarAssetName)
                    registrarUrl = url;
                else if (name == checksumAssetName)
                    checksumUrl = url;
            }

            if (registrarUrl != null)
            {
                Debug.WriteLine($"[BrowserSupport] Found registrar release: {tagName}");
                return (registrarUrl, checksumUrl);
            }
        }

        throw new InvalidOperationException(
            $"No registrar release (vr-* tag) found with asset '{registrarAssetName}' " +
            $"at https://github.com/{GitHubRepo}/releases");
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

    // ───────────────────── GitHub Download Helpers ─────────────────────

    internal static string GetPlatform()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Easydict-WinUI");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    private static async Task DownloadFileAsync(HttpClient client, string url, string destPath, CancellationToken ct)
    {
        Debug.WriteLine($"[BrowserSupport] Downloading: {url}");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream, ct);

        // Remove NTFS Zone.Identifier ADS to prevent SmartScreen blocking.
        // Downloaded files are marked as "from the internet" which can cause
        // Process.Start to fail or show a security warning.
        RemoveZoneIdentifier(destPath);

        Debug.WriteLine($"[BrowserSupport] Downloaded: {destPath} ({new FileInfo(destPath).Length} bytes)");
    }

    /// <summary>
    /// Remove the NTFS Zone.Identifier alternate data stream from a downloaded file.
    /// This prevents Windows SmartScreen from blocking Process.Start on the file.
    /// Equivalent to PowerShell's Unblock-File cmdlet.
    /// </summary>
    private static void RemoveZoneIdentifier(string filePath)
    {
        try
        {
            var adsPath = filePath + ":Zone.Identifier";
            if (NativeMethods.DeleteFile(adsPath))
                Debug.WriteLine($"[BrowserSupport] Removed Zone.Identifier from {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserSupport] Zone.Identifier removal failed (non-critical): {ex.Message}");
        }
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool DeleteFile(string lpFileName);
    }

    /// <summary>
    /// Verify SHA256 hash of a downloaded file against a checksum file.
    /// Checksum file format: "hash *filename" or "hash  filename" per line.
    /// </summary>
    internal static void VerifyChecksum(string checksumContent, string expectedFileName, string filePath)
    {
        foreach (var line in checksumContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                var expectedHash = parts[0];
                var actualHash = ComputeSha256(filePath);

                if (!expectedHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"SHA256 checksum mismatch for {expectedFileName}: expected {expectedHash}, got {actualHash}. " +
                        "The downloaded file may be corrupted or tampered with.");
                }

                Debug.WriteLine($"[BrowserSupport] Checksum verified: {expectedFileName}");
                return;
            }
        }

        Debug.WriteLine($"[BrowserSupport] No checksum entry found for {expectedFileName}, skipping verification");
    }

    internal static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

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
