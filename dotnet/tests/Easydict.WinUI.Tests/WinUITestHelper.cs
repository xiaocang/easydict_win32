using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Tests;

/// <summary>
/// Helper for WinUI tests that require a real Window.
/// Probes <c>new Window()</c> once per test run and caches the result so that
/// tests can gracefully skip on headless CI runners where Window creation
/// throws <see cref="System.Runtime.InteropServices.COMException"/>.
/// </summary>
internal static class WinUITestHelper
{
    public const string SkipReason =
        "WinUI Window creation is not available (headless CI or missing display).";

    private static readonly Lazy<bool> _canCreateWindow = new(Probe);

    /// <summary>
    /// <c>true</c> when <c>new Window()</c> succeeds on this machine;
    /// <c>false</c> on headless CI runners.
    /// </summary>
    public static bool CanCreateWindow => _canCreateWindow.Value;

    private static bool Probe()
    {
        try
        {
            var window = new Window();
            window.Close();
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
