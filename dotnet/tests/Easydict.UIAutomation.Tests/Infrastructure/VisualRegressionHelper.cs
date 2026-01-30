using Codeuctivity.ImageSharpCompare;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Compares screenshots against baseline images for visual regression testing.
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
