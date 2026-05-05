using System;
using System.Diagnostics;

namespace Easydict.WinUI.Services;

/// <summary>
/// Centralized feature-gate predicates used across the settings UI.
///
/// WinAppSDK 2.x adds <c>IXamlCondition</c> for XAML-time conditional
/// namespaces. These predicates are the source of truth — they can be:
///
/// 1. Read directly from code-behind (works on any SDK).
/// 2. Bound through <see cref="Microsoft.UI.Xaml.Visibility"/> via a converter (current pattern).
/// 3. Wired into an <c>IXamlCondition</c> implementation that evaluates at parse
///    time, so gated UI is removed from the visual tree entirely (preferred end
///    state — see TODO at the bottom).
///
/// Runtime-toggleable settings (e.g. <c>MouseSelectionTranslate</c>) belong in
/// <see cref="SettingsService"/> with a regular Binding; XAML conditions evaluate
/// once at parse time and cannot react to setting changes.
/// </summary>
internal static class EasydictConditions
{
    private static readonly Lazy<bool> _isPackaged = new(DetectPackaged, isThreadSafe: true);
    private static readonly Lazy<bool> _hasWindowsOcr = new(() =>
    {
        try { return new WindowsOcrService().IsAvailable; }
        catch (Exception ex) { Debug.WriteLine($"[EasydictConditions] HasWindowsOcr probe failed: {ex.Message}"); return false; }
    }, isThreadSafe: true);

    /// <summary>True when running as an MSIX-packaged app (vs portable EXE/ZIP).</summary>
    public static bool IsPackaged => _isPackaged.Value;

    /// <summary>True when Windows.Media.Ocr can create an engine from installed language packs.</summary>
    public static bool HasWindowsOcr => _hasWindowsOcr.Value;

    /// <summary>True when a recent enough WebView2 Runtime is installed.</summary>
    public static bool HasWebView2 => WebView2RuntimeService.IsRuntimeAvailable;

    /// <summary>
    /// True when this is a Microsoft Store build. Compile-time constant set via
    /// <c>&lt;DefineConstants&gt;STORE_BUILD&lt;/DefineConstants&gt;</c> in the Store CI csproj override.
    /// </summary>
    public static bool IsStoreBuild =>
#if STORE_BUILD
        true;
#else
        false;
#endif

    private static bool DetectPackaged()
    {
        try
        {
            // Package.Current throws InvalidOperationException for unpackaged Win32 apps.
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EasydictConditions] IsPackaged probe failed: {ex.Message}");
            return false;
        }
    }

    // TODO(WinAppSDK 2.0.1 IXamlCondition): expose these predicates to the XAML parser by
    // implementing IXamlCondition wrappers and registering them via the conditional-namespace
    // mechanism. Once wired:
    //
    //   xmlns:cond="conditional:Easydict.WinUI.Services.IXamlConditions"
    //   <StackPanel cond:if="{cond:HasWindowsOcr}"> ... </StackPanel>
    //
    // SettingsPage.xaml currently uses Visibility/Bindings to hide OCR / WebView2 / packaged-only
    // sections; that work continues to function and can be migrated incrementally.
}
