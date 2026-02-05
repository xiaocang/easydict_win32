using Codeuctivity.ImageSharpCompare;
using SixLabors.ImageSharp;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Compares screenshots against baseline images for visual regression testing.
///
/// Baseline workflow:
/// 1. First run (no baseline): screenshot saved as candidate in screenshots/baseline-candidates/
/// 2. Human reviews candidates from CI artifacts
/// 3. Approved candidates are committed to Baselines/ directory
/// 4. Subsequent runs compare against committed baselines
/// 5. When UI changes intentionally, replace old baselines with new candidates
///
/// Threshold guidelines by screenshot type:
/// - Small icons (PopButton 30x30): use ThresholdSmallIcon (3%)
/// - Window structural elements: use ThresholdStructural (3%)
/// - Areas containing text: use ThresholdText (8%) — font anti-aliasing varies
/// - Full window screenshots: use DefaultThresholdPercent (5%)
/// - Full screen with multiple apps: use ThresholdFullScreen (10%)
/// </summary>
public static class VisualRegressionHelper
{
    private static string? _baselineDir;

    /// <summary>
    /// Directory containing baseline screenshots.
    /// Defaults to BASELINE_DIR env var or ./Baselines relative to test assembly.
    /// </summary>
    public static string BaselineDir
    {
        get
        {
            if (_baselineDir == null)
            {
                _baselineDir = Environment.GetEnvironmentVariable("BASELINE_DIR")
                    ?? Path.Combine(AppContext.BaseDirectory, "Baselines");
            }
            return _baselineDir;
        }
        set => _baselineDir = value;
    }

    /// <summary>
    /// Default pixel error percentage threshold (5%).
    /// UI rendering may vary slightly across machines/DPI settings.
    /// </summary>
    public const double DefaultThresholdPercent = 5.0;

    /// <summary>
    /// Tight threshold for small icons like the PopButton (30x30).
    /// A single pixel difference has a higher percentage impact on small images.
    /// </summary>
    public const double ThresholdSmallIcon = 3.0;

    /// <summary>
    /// Tight threshold for structural UI elements (buttons, borders, layout).
    /// These should be pixel-stable across renders at the same DPI.
    /// </summary>
    public const double ThresholdStructural = 3.0;

    /// <summary>
    /// Relaxed threshold for areas containing rendered text.
    /// Font anti-aliasing (ClearType) varies across machines and Windows versions.
    /// </summary>
    public const double ThresholdText = 8.0;

    /// <summary>
    /// Most relaxed threshold for full-screen captures with multiple applications.
    /// Taskbar, desktop background, and other apps introduce significant variance.
    /// </summary>
    public const double ThresholdFullScreen = 10.0;

    /// <summary>
    /// Compare a screenshot against its baseline.
    /// Returns null if no baseline exists (first run).
    /// </summary>
    public static VisualComparisonResult? CompareWithBaseline(
        string screenshotPath,
        string baselineName,
        double thresholdPercent = DefaultThresholdPercent)
    {
        var baselinePath = Path.Combine(BaselineDir, $"{baselineName}.png");

        if (!File.Exists(baselinePath))
        {
            // No baseline yet - copy current screenshot as baseline candidate
            var candidateDir = Path.Combine(ScreenshotHelper.OutputDir, "baseline-candidates");
            Directory.CreateDirectory(candidateDir);
            File.Copy(screenshotPath, Path.Combine(candidateDir, $"{baselineName}.png"), overwrite: true);

            return null; // No comparison possible
        }

        var diff = ImageSharpCompare.CalcDiff(screenshotPath, baselinePath);
        var pixelErrorPercent = diff.PixelErrorPercentage;

        // Generate diff image if threshold exceeded
        string? diffImagePath = null;
        if (pixelErrorPercent > thresholdPercent)
        {
            diffImagePath = Path.Combine(ScreenshotHelper.OutputDir, $"{baselineName}_diff.png");
            using var diffImage = ImageSharpCompare.CalcDiffMaskImage(screenshotPath, baselinePath);
            diffImage.Save(diffImagePath);
        }

        return new VisualComparisonResult
        {
            BaselinePath = baselinePath,
            ActualPath = screenshotPath,
            DiffImagePath = diffImagePath,
            PixelErrorPercent = pixelErrorPercent,
            ThresholdPercent = thresholdPercent,
            Passed = pixelErrorPercent <= thresholdPercent
        };
    }
}

public record VisualComparisonResult
{
    public required string BaselinePath { get; init; }
    public required string ActualPath { get; init; }
    public string? DiffImagePath { get; init; }
    public required double PixelErrorPercent { get; init; }
    public required double ThresholdPercent { get; init; }
    public required bool Passed { get; init; }

    public override string ToString() =>
        $"Visual comparison: {PixelErrorPercent:F2}% pixel error (threshold: {ThresholdPercent:F2}%) - {(Passed ? "PASSED" : "FAILED")}";
}
