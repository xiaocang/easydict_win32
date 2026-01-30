using System.Diagnostics;
using FlaUI.Core;
using FlaUI.UIA3;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Handles launching and managing the Easydict application for UI tests.
/// Supports both MSIX-installed and unpackaged (exe) launch modes.
/// </summary>
public sealed class AppLauncher : IDisposable
{
    private Application? _application;
    private UIA3Automation? _automation;
    private bool _isDisposed;

    public Application Application => _application ?? throw new InvalidOperationException("App not launched");
    public UIA3Automation Automation => _automation ?? throw new InvalidOperationException("App not launched");

    /// <summary>
    /// Launch the app from an MSIX-installed package using its AppUserModelId.
    /// </summary>
    public void LaunchFromMsix(string packageFamilyName, TimeSpan? timeout = null)
    {
        _automation = new UIA3Automation();
        var appUserModelId = $"{packageFamilyName}!App";
        _application = Application.LaunchStoreApp(appUserModelId);
        WaitForMainWindow(timeout ?? TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Launch the app directly from an exe path (for local development).
    /// </summary>
    public void LaunchFromExe(string exePath, TimeSpan? timeout = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"App executable not found: {exePath}");

        _automation = new UIA3Automation();
        _application = Application.Launch(exePath);
        WaitForMainWindow(timeout ?? TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Try to find and launch the app. Checks MSIX first, then falls back to exe path.
    /// </summary>
    public void LaunchAuto(TimeSpan? timeout = null)
    {
        // Check environment variable for explicit exe path
        var exePath = Environment.GetEnvironmentVariable("EASYDICT_EXE_PATH");
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            LaunchFromExe(exePath, timeout);
            return;
        }

        // Check environment variable for package family name (MSIX)
        var packageFamilyName = Environment.GetEnvironmentVariable("EASYDICT_PACKAGE_FAMILY_NAME");
        if (!string.IsNullOrEmpty(packageFamilyName))
        {
            LaunchFromMsix(packageFamilyName, timeout);
            return;
        }

        // Try to find installed MSIX package via PowerShell
        var familyName = FindInstalledPackageFamilyName();
        if (familyName != null)
        {
            LaunchFromMsix(familyName, timeout);
            return;
        }

        throw new InvalidOperationException(
            "Cannot find Easydict app. Set EASYDICT_EXE_PATH or EASYDICT_PACKAGE_FAMILY_NAME environment variable.");
    }

    public FlaUI.Core.AutomationElements.Window GetMainWindow(TimeSpan? timeout = null)
    {
        var window = Application.GetMainWindow(Automation, timeout ?? TimeSpan.FromSeconds(15));
        if (window == null)
            throw new InvalidOperationException("Main window not found");
        return window;
    }

    private void WaitForMainWindow(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var window = Application.GetMainWindow(Automation);
                if (window != null)
                    return;
            }
            catch
            {
                // Window not ready yet
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Main window did not appear within {timeout.TotalSeconds}s");
    }

    private static string? FindInstalledPackageFamilyName()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-AppxPackage -Name 'xiaocang.EasydictforWindows' | Select-Object -First 1).PackageFamilyName\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);

            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _application?.Close();
            // Give the app time to close gracefully
            if (_application != null && !_application.HasExited)
            {
                Thread.Sleep(2000);
                if (!_application.HasExited)
                    _application.Kill();
            }
        }
        catch
        {
            // Ignore close errors
        }

        _automation?.Dispose();
    }
}
