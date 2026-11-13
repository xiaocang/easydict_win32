using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>One stage positioned on a 0..1 timeline. Ratios are layout weights, not pixels.</summary>
internal readonly record struct DiagnosticBar(double OffsetRatio, double LengthRatio, bool IsRunning);

/// <summary>
/// Converts measured stage ranges into layout ratios so the view can size bars with star
/// columns instead of measuring text widths. Pure logic: no XAML dependency, so it is unit testable.
/// </summary>
internal static class TranslationDiagnosticWaterfall
{
    /// <summary>
    /// Latest point any stage reaches. Best-effort work can outlive the frozen total, so the
    /// scale must cover it or the bar would be clipped.
    /// </summary>
    public static double ResolveScale(TranslationDiagnosticSnapshot snapshot)
    {
        var furthest = snapshot.Timings
            .Select(phase => phase.StartedMs + Math.Max(phase.DurationMs, 0))
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(Math.Max(furthest, snapshot.TotalMs), 1);
    }

    public static IReadOnlyList<DiagnosticBar> Build(TranslationDiagnosticSnapshot snapshot)
    {
        var scale = ResolveScale(snapshot);
        return snapshot.Timings.Select(phase => ToBar(phase, scale)).ToArray();
    }

    public static DiagnosticBar ToBar(TranslationDiagnosticTiming phase, double scale)
    {
        var offset = Math.Clamp(phase.StartedMs, 0, scale);
        var length = Math.Clamp(phase.DurationMs, 0, scale - offset);
        return new DiagnosticBar(offset / scale, length / scale, phase.IsRunning);
    }
}
