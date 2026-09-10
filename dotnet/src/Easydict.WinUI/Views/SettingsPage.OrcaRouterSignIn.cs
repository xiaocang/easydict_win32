using System.Diagnostics;
using System.Net.Sockets;
using Easydict.TranslationService;
using Easydict.TranslationService.Services.Auth;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Views;

/// <summary>
/// "Sign in with OrcaRouter" (PKCE loopback SSO): opens the browser, waits for the redirect on
/// a 127.0.0.1 listener, verifies the CSRF <c>state</c>, exchanges the code for an API key,
/// persists it and refreshes the model catalog.
/// </summary>
public sealed partial class SettingsPage
{
    private static readonly TimeSpan OrcaRouterSignInTimeout = TimeSpan.FromMinutes(5);

    // Owned by OnSignInWithOrcaRouter(): only that method creates and disposes it.
    // Non-null means a sign-in is in flight; other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _orcaRouterSignInCts;

    private void ApplyOrcaRouterSignInLocalization(LocalizationService loc)
    {
        // While a sign-in is waiting the button reads "Cancel"; don't clobber that.
        if (_orcaRouterSignInCts == null)
        {
            SignInWithOrcaRouterButton.Content = loc.GetString("SignInWithOrcaRouter");
        }
    }

    private void TeardownOrcaRouterSignIn()
    {
        try { _orcaRouterSignInCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async void OnSignInWithOrcaRouter(object sender, RoutedEventArgs e)
    {
        if (_isUnloaded || _isTornDown) return;

        var inFlight = _orcaRouterSignInCts;
        if (inFlight != null)
        {
            // Second click while waiting acts as Cancel; never start a second listener.
            try { inFlight.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }

        var loc = LocalizationService.Instance;
        using var cts = new CancellationTokenSource();
        _orcaRouterSignInCts = cts;
        SignInWithOrcaRouterButton.Content = loc.GetString("Cancel");

        try
        {
            await RunOrcaRouterSignInAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OrcaRouterSignIn] Unexpected failure: {ex}");
            if (!_isUnloaded)
            {
                SetOrcaRouterSignInStatus(string.Format(loc.GetString("OrcaRouterSignInFailed"), ex.Message));
            }
        }
        finally
        {
            _orcaRouterSignInCts = null;
            if (!_isUnloaded)
            {
                SignInWithOrcaRouterButton.Content = loc.GetString("SignInWithOrcaRouter");
            }
        }
    }

    private async Task RunOrcaRouterSignInAsync(CancellationToken userCancellation)
    {
        var loc = LocalizationService.Instance;

        using var timeoutCts = new CancellationTokenSource(OrcaRouterSignInTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            userCancellation, timeoutCts.Token, _lifetimeCts.Token);

        var session = PkceSession.Create();

        LoopbackCallbackListener listener;
        try
        {
            listener = new LoopbackCallbackListener();
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"[OrcaRouterSignIn] Loopback listener failed: {ex.Message}");
            SetOrcaRouterSignInStatus(string.Format(loc.GetString("OrcaRouterSignInFailed"), ex.Message));
            return;
        }

        using (listener)
        {
            var authorizeUri = OrcaRouterAuthClient.BuildAuthorizeUri(
                listener.CallbackUrl, session.CodeChallenge, session.State);

            if (!await TryLaunchUriAsync(authorizeUri, LaunchUriAsync))
            {
                if (!_isUnloaded)
                {
                    SetOrcaRouterSignInStatus(loc.GetString("OrcaRouterSignInBrowserFailed"));
                }
                return;
            }

            if (_isUnloaded) return;
            SetOrcaRouterSignInStatus(loc.GetString("OrcaRouterSignInWaiting"));

            LoopbackCallbackResult callback;
            try
            {
                callback = await listener.WaitForCallbackAsync(session.State, linked.Token);
            }
            catch (OperationCanceledException)
            {
                ReportOrcaRouterSignInCancelled(loc, timeoutCts);
                return;
            }

            if (_isUnloaded) return;

            if (!callback.IsSuccess)
            {
                var reason = callback.ErrorDescription ?? callback.Error ?? "unknown";
                SetOrcaRouterSignInStatus(string.Format(loc.GetString("OrcaRouterSignInFailed"), reason));
                return;
            }

            string apiKey;
            try
            {
                using var handle = TranslationManagerService.Instance.AcquireHandle();
                var client = new OrcaRouterAuthClient(handle.Manager.SharedHttpClient);
                apiKey = await client.ExchangeCodeForApiKeyAsync(callback.Code!, session.CodeVerifier, linked.Token);
            }
            catch (OperationCanceledException)
            {
                ReportOrcaRouterSignInCancelled(loc, timeoutCts);
                return;
            }
            catch (TranslationException ex)
            {
                Debug.WriteLine($"[OrcaRouterSignIn] Key exchange failed: {ex.Message}");
                if (!_isUnloaded)
                {
                    SetOrcaRouterSignInStatus(string.Format(loc.GetString("OrcaRouterSignInFailed"), ex.Message));
                }
                return;
            }

            if (_isUnloaded) return;

            // Persist first so OnSettingChanged's SameSecret() sees no diff when the box updates.
            _settings.OrcaRouterApiKey = apiKey;
            OrcaRouterKeyBox.Password = apiKey;
            _settings.Save();
            TranslationManagerService.Instance.ReconfigureServices();

            SetOrcaRouterSignInStatus(loc.GetString("OrcaRouterSignInSucceeded"));

            _hasRefreshedOrcaRouterModels = true;
            await RefreshCatalogModelsAsync(
                "orcarouter",
                OrcaRouterModelCombo,
                RefreshOrcaRouterModelsButton,
                "orcarouter/free",
                forceRefresh: true,
                showErrorDialog: false,
                FetchOrcaRouterModelsAsync);
        }
    }

    private void ReportOrcaRouterSignInCancelled(LocalizationService loc, CancellationTokenSource timeoutCts)
    {
        if (_isUnloaded || _lifetimeCts.IsCancellationRequested) return;

        SetOrcaRouterSignInStatus(loc.GetString(
            timeoutCts.IsCancellationRequested ? "OrcaRouterSignInTimedOut" : "OrcaRouterSignInCancelled"));
    }

    private void SetOrcaRouterSignInStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            OrcaRouterSignInStatusText.Text = string.Empty;
            OrcaRouterSignInStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        OrcaRouterSignInStatusText.Text = text;
        OrcaRouterSignInStatusText.Visibility = Visibility.Visible;
    }
}
