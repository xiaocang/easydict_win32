using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Captures screenshots during UI automation tests.
/// Saves to a configurable output directory (default: ./screenshots).
/// </summary>
public static class ScreenshotHelper
{
    private static string? _outputDir;

    /// <summary>
    /// Get or set the screenshot output directory.
    /// Defaults to SCREENSHOT_OUTPUT_DIR env var or ./screenshots.
    /// </summary>
    public static string OutputDir
    {
        get
        {
            if (_outputDir == null)
            {
                _outputDir = Environment.GetEnvironmentVariable("SCREENSHOT_OUTPUT_DIR")
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots");
                Directory.CreateDirectory(_outputDir);
            }
            return _outputDir;
        }
        set
        {
            _outputDir = value;
            Directory.CreateDirectory(_outputDir);
        }
    }

    /// <summary>
    /// Capture the entire screen and save with the given name.
    /// </summary>
    public static string CaptureScreen(string name)
    {
        var image = Capture.Screen();
        return SaveCapture(image, name);
    }

    /// <summary>
    /// Capture a specific window and save with the given name.
    /// </summary>
    public static string CaptureWindow(Window window, string name)
    {
        // Focus the window first to ensure it's visible
        window.SetForeground();
        Thread.Sleep(300); // Allow render

        var image = Capture.Element(window);
        return SaveCapture(image, name);
    }

    /// <summary>
    /// Capture a specific element and save with the given name.
    /// </summary>
    public static string CaptureElement(AutomationElement element, string name)
    {
        var image = Capture.Element(element);
        return SaveCapture(image, name);
    }

    private static string SaveCapture(CaptureImage capture, string name)
    {
        var sanitized = SanitizeFileName(name);
        var filePath = Path.Combine(OutputDir, $"{sanitized}.png");
        capture.ToFile(filePath);
        return filePath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }
}
