using System;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Easydict.WinUI.Services;

/// <summary>
/// Detects the WebView2 Runtime installed on the host and exposes capability flags
/// for features that require a recent runtime (e.g. drag-from-WebView2 source content,
/// which the WinAppSDK 2.x XAML WebView2 control wires through to CoreWebView2 only
/// when the underlying runtime is recent enough).
///
/// Cached for the process lifetime — runtime upgrades require an app restart.
/// </summary>
internal static class WebView2RuntimeService
{
    /// <summary>
    /// Minimum WebView2 Runtime version required for content-drag from the WebView2 control.
    /// Source: WinAppSDK 2.0.1 release notes (drag from WebView2 hosted in WinUI 3 XAML).
    /// </summary>
    public static readonly Version DragSupportMinimumVersion = new(144, 0, 3719, 11);

    private static readonly Lazy<RuntimeInfo> _info = new(Detect, isThreadSafe: true);

    public static bool IsRuntimeAvailable => _info.Value.IsAvailable;
    public static Version? RuntimeVersion => _info.Value.Version;
    public static bool SupportsContentDrag =>
        _info.Value.IsAvailable
        && _info.Value.Version != null
        && _info.Value.Version >= DragSupportMinimumVersion;

    private sealed record RuntimeInfo(bool IsAvailable, Version? Version);

    private static RuntimeInfo Detect()
    {
        try
        {
            var versionString = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(versionString))
            {
                Debug.WriteLine("[WebView2RuntimeService] Runtime not installed (empty version string).");
                return new RuntimeInfo(false, null);
            }

            // GetAvailableBrowserVersionString returns "144.0.3719.11" or "144.0.3719.11 (channel)".
            var head = versionString.Split(' ', 2)[0];
            if (Version.TryParse(head, out var version))
            {
                Debug.WriteLine($"[WebView2RuntimeService] Detected WebView2 Runtime {version}");
                return new RuntimeInfo(true, version);
            }

            Debug.WriteLine($"[WebView2RuntimeService] Unparseable version string: {versionString}");
            return new RuntimeInfo(true, null);
        }
        catch (Exception ex)
        {
            // CoreWebView2Environment.GetAvailableBrowserVersionString throws when the WebView2
            // Runtime is not installed. The exception type has changed across SDK versions
            // (WebView2RuntimeNotFoundException in some, plain Exception in others), so catch
            // broadly and treat any failure as "no runtime available".
            Debug.WriteLine($"[WebView2RuntimeService] Runtime detection failed: {ex.GetType().Name}: {ex.Message}");
            return new RuntimeInfo(false, null);
        }
    }
}
