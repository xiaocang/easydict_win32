using System.Diagnostics;
using System.Net;
using Easydict.TranslationService.LocalApi;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// WinUI-side glue between <see cref="SettingsService"/> and <see cref="LocalApiServer"/>.
///
/// Responsibilities:
///   - Build an immutable <see cref="LocalApiOptions"/> snapshot from settings.
///   - Auto-generate the bearer token on first enable; persist back to settings.
///   - In a packaged context, ensure the loopback exemption is applied so browsers
///     outside the AppContainer can reach 127.0.0.1.
///   - Debounce settings changes so toggling several checkboxes doesn't churn the listener.
///   - Surface lifecycle/errors via events so the settings page can render them.
/// </summary>
public sealed class LocalApiCoordinator : IDisposable
{
    private readonly TranslationManagerService _managerService;
    private readonly SettingsService _settings;
    private readonly LocalApiServer _server;
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(200);

    private CancellationTokenSource? _debounceCts;
    private readonly object _stateLock = new();
    private bool _loopbackExemptionAttempted;
    private bool _disposed;

    public event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;

    public LocalApiCoordinator(TranslationManagerService managerService, SettingsService settings)
    {
        _managerService = managerService ?? throw new ArgumentNullException(nameof(managerService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _server = new LocalApiServer(() => _managerService.Manager);
    }

    public bool IsRunning => _server.IsRunning;
    public string? CurrentBaseUrl => _server.CurrentBaseUrl;

    /// <summary>
    /// If <see cref="SettingsService.LocalApiEnabled"/> is true, start the server.
    /// Generates and persists a token on first enable. Loopback exemption is applied
    /// once per process when packaged.
    /// </summary>
    public async Task StartIfEnabledAsync()
    {
        if (!_settings.LocalApiEnabled) return;
        await StartCoreAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Notify the coordinator that settings changed. Debounces 200 ms before applying.
    /// </summary>
    public void NotifySettingsChanged()
    {
        CancellationTokenSource cts;
        lock (_stateLock)
        {
            if (_disposed) return;
            // Cancel the previous debounce but don't dispose its CTS — the in-flight task
            // still holds the token. The previous task will observe OperationCanceledException
            // and exit; the CTS becomes unreachable and is GC'd.
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
                await ApplyAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalApiCoordinator] debounce/apply failed: {ex}");
            }
        });
    }

    public async Task StopAsync()
    {
        await _server.StopAsync().ConfigureAwait(false);
        RaiseStateChanged(success: true, errorMessage: null);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        try { _server.Dispose(); } catch { }
    }

    private async Task ApplyAsync()
    {
        if (_settings.LocalApiEnabled)
        {
            if (_server.IsRunning)
            {
                try
                {
                    await _server.ReconfigureAsync(BuildOptions()).ConfigureAwait(false);
                    RaiseStateChanged(success: true, errorMessage: null);
                }
                catch (HttpListenerException ex)
                {
                    Debug.WriteLine($"[LocalApiCoordinator] reconfigure failed: {ex}");
                    RaiseStateChanged(success: false, errorMessage: ex.Message);
                }
            }
            else
            {
                await StartCoreAsync().ConfigureAwait(false);
            }
        }
        else if (_server.IsRunning)
        {
            await _server.StopAsync().ConfigureAwait(false);
            RaiseStateChanged(success: true, errorMessage: null);
        }
    }

    private async Task StartCoreAsync()
    {
        if (string.IsNullOrEmpty(_settings.LocalApiToken))
        {
            _settings.LocalApiToken = LocalApiTokenGenerator.Generate();
            try { _settings.Save(); } catch (Exception ex) { Debug.WriteLine($"[LocalApiCoordinator] token persist failed: {ex.Message}"); }
        }

        if (!_loopbackExemptionAttempted)
        {
            _loopbackExemptionAttempted = true;
            await TryApplyLoopbackExemptionAsync().ConfigureAwait(false);
        }

        try
        {
            await _server.StartAsync(BuildOptions()).ConfigureAwait(false);
            RaiseStateChanged(success: true, errorMessage: null);
        }
        catch (HttpListenerException ex)
        {
            Debug.WriteLine($"[LocalApiCoordinator] start failed: code={ex.ErrorCode} {ex.Message}");
            RaiseStateChanged(success: false, errorMessage: ex.Message);
        }
    }

    private LocalApiOptions BuildOptions()
    {
        var corsMode = string.Equals(_settings.LocalApiCorsMode, "AllowList", StringComparison.OrdinalIgnoreCase)
            ? LocalApiCorsMode.AllowList
            : LocalApiCorsMode.Any;

        var defaultTarget = LanguageCodes.TryParseIsoCode(_settings.LocalApiDefaultTargetLanguage, out var parsed)
            ? parsed
            : Language.SimplifiedChinese;

        return new LocalApiOptions
        {
            Port = _settings.LocalApiPort,
            Token = _settings.LocalApiToken,
            ExposedServiceIds = new HashSet<string>(_settings.LocalApiExposedServices, StringComparer.Ordinal),
            CorsMode = corsMode,
            AllowedOrigins = _settings.LocalApiCorsAllowList.ToArray(),
            DefaultTargetLanguage = defaultTarget,
        };
    }

    private static async Task TryApplyLoopbackExemptionAsync()
    {
        string? pfn = TryGetPackageFamilyName();
        if (string.IsNullOrEmpty(pfn)) return;
        try
        {
            var result = await LoopbackExemption.EnsureAsync(pfn).ConfigureAwait(false);
            Debug.WriteLine($"[LocalApiCoordinator] loopback exemption: {result}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalApiCoordinator] loopback exemption failed: {ex.Message}");
        }
    }

    private static string? TryGetPackageFamilyName()
    {
        try
        {
            // Packaged WinUI 3 apps expose Package.Current; throws InvalidOperationException
            // when unpackaged. We catch and treat as "no packaged identity".
            return Windows.ApplicationModel.Package.Current.Id.FamilyName;
        }
        catch
        {
            return null;
        }
    }

    private void RaiseStateChanged(bool success, string? errorMessage)
    {
        try
        {
            StateChanged?.Invoke(this, new LocalApiStateChangedEventArgs
            {
                IsRunning = _server.IsRunning,
                BaseUrl = _server.CurrentBaseUrl,
                LastErrorMessage = errorMessage,
                LastChangeSucceeded = success,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalApiCoordinator] StateChanged handler threw: {ex}");
        }
    }
}

public sealed class LocalApiStateChangedEventArgs : EventArgs
{
    public required bool IsRunning { get; init; }
    public required string? BaseUrl { get; init; }
    public required string? LastErrorMessage { get; init; }
    public required bool LastChangeSucceeded { get; init; }
}
