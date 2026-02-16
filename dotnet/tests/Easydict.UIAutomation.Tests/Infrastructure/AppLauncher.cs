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
        var resolvedTimeout = ResolveLaunchTimeout(timeout ?? TimeSpan.FromSeconds(30));
        var appUserModelId = $"{packageFamilyName}!App";
        LaunchWithRetry(() => Application.LaunchStoreApp(appUserModelId), resolvedTimeout);
    }

    /// <summary>
    /// Launch the app directly from an exe path (for local development).
    /// </summary>
    public void LaunchFromExe(string exePath, TimeSpan? timeout = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"App executable not found: {exePath}");

        var resolvedTimeout = ResolveLaunchTimeout(timeout ?? TimeSpan.FromSeconds(30));
        LaunchWithRetry(() => Application.Launch(exePath), resolvedTimeout);
    }

    /// <summary>
    /// Try to find and launch the app. Prefer package activation for MSIX; EXE fallback is opt-in.
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
            try
            {
                LaunchFromMsix(packageFamilyName, timeout);
                return;
            }
            catch (TimeoutException)
            {
                // Fall back to executable discovery when AUMID activation is flaky.
            }
        }

        // Try to find installed package info via PowerShell
        var packageInfo = FindInstalledPackageInfo();
        var allowExeFallback = ResolveAllowExeFallback();
        if (packageInfo.FamilyName != null)
        {
            try
            {
                LaunchFromMsix(packageInfo.FamilyName, timeout);
                return;
            }
            catch (TimeoutException)
            {
                if (!allowExeFallback || packageInfo.ExePath == null)
                {
                    throw;
                }
            }
        }

        if (allowExeFallback && packageInfo.ExePath != null)
        {
            LaunchFromExe(packageInfo.ExePath, timeout);
            return;
        }

        throw new InvalidOperationException(
            "Cannot find Easydict app. Set EASYDICT_PACKAGE_FAMILY_NAME (preferred) or EASYDICT_EXE_PATH. EXE fallback from installed package can be enabled with EASYDICT_UIA_ALLOW_EXE_FALLBACK=1.");
    }


    private void LaunchWithRetry(Func<Application> launch, TimeSpan timeout)
    {
        var attempts = ResolveLaunchAttempts();
        Exception? lastException = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            TryCloseApplication();
            _automation?.Dispose();
            _automation = new UIA3Automation();

            try
            {
                _application = launch();
                WaitForMainWindow(timeout);
                return;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
                if (attempt == attempts)
                {
                    throw;
                }

                TryCloseApplication();
                Thread.Sleep(1000);
            }
        }

        throw lastException ?? new TimeoutException($"Main window did not appear within {timeout.TotalSeconds}s");
    }

    private static int ResolveLaunchAttempts()
    {
        const int defaultAttempts = 2;
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_LAUNCH_ATTEMPTS");
        if (!int.TryParse(value, out var attempts))
        {
            return defaultAttempts;
        }

        return Math.Clamp(attempts, 1, 5);
    }

    private static TimeSpan ResolveLaunchTimeout(TimeSpan defaultTimeout)
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MAINWINDOW_TIMEOUT_SECONDS");
        if (!double.TryParse(value, out var seconds) || seconds <= 0)
        {
            return defaultTimeout;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
    }

    private static bool ResolveAllowExeFallback()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_ALLOW_EXE_FALLBACK");
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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

    private static (string? FamilyName, string? ExePath) FindInstalledPackageInfo()
    {
        try
        {
            const string command = "& { $p = Get-AppxPackage -Name 'xiaocang.EasydictforWindows' | Select-Object -First 1; if (-not $p) { return }; $family = $p.PackageFamilyName; $exe = Join-Path $p.InstallLocation 'Easydict.WinUI.exe'; if (Test-Path $exe) { Write-Output ($family + '|' + $exe) } else { Write-Output ($family + '|') } }";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi);
            if (process == null)
            {
                return (null, null);
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            if (string.IsNullOrEmpty(output))
            {
                return (null, null);
            }

            var parts = output.Split('|', 2, StringSplitOptions.TrimEntries);
            var family = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : null;
            var exe = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
            return (family, exe);
        }
        catch
        {
            return (null, null);
        }
    }

    private void TryCloseApplication()
    {
        try
        {
            _application?.Close();
            if (_application != null && !_application.HasExited)
            {
                Thread.Sleep(2000);
                if (!_application.HasExited)
                {
                    _application.Kill();
                }
            }
        }
        catch
        {
            // Ignore close errors
        }
        finally
        {
            _application = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        TryCloseApplication();
        _automation?.Dispose();
    }
}
