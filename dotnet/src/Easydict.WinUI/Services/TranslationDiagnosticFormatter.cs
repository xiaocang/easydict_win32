using System.Globalization;
using System.Text;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

internal static class TranslationDiagnosticFormatter
{
    private const int WaterfallWidth = 40;
    private const int BarColumn = 22;

    public static string Format(TranslationDiagnosticSnapshot snapshot, Func<string, string> localize)
    {
        var text = new StringBuilder();
        text.AppendLine($"{localize("DiagnosticTotal")}: {ToMs(snapshot.TotalMs)} ms");
        text.AppendLine(localize("DiagnosticStatus" + snapshot.Status));
        text.AppendLine();
        text.AppendLine(localize("DiagnosticColumns"));
        AppendWaterfall(text, snapshot, localize);
        text.AppendLine();
        text.Append(localize("DiagnosticTimingNote"));
        return text.ToString();
    }

    /// <summary>
    /// Draws one ASCII row per stage starting at its offset, so overlapping stages show up as
    /// overlapping bars instead of durations that look additive.
    /// </summary>
    private static void AppendWaterfall(
        StringBuilder text, TranslationDiagnosticSnapshot snapshot, Func<string, string> localize)
    {
        // A phase that outlives the frozen total (best-effort work after completion) must still
        // fit: scale to the furthest point any measured phase reaches.
        var total = Math.Max(
            Math.Max(snapshot.TotalMs, 1),
            snapshot.Timings.Select(phase => phase.StartedMs + phase.DurationMs).DefaultIfEmpty(0).Max());
        foreach (var phase in snapshot.Timings)
        {
            text.AppendLine($"{ToMs(phase.StartedMs),8} ms  {ToMs(phase.DurationMs),8} ms  "
                + RenderBar(phase, total) + "  "
                + localize("DiagnosticStage" + phase.Stage)
                + (phase.IsRunning ? $" ({localize("DiagnosticStatusRunning")})" : ""));
        }

        // Plain time axis: the bar column starts at position 22 and spans WaterfallWidth chars.
        var end = " " + ToMs(total) + " ms";
        text.AppendLine($"{new string(' ', BarColumn)}{new string('-', WaterfallWidth)}");
        text.AppendLine($"{new string(' ', BarColumn)}0 ms{new string(' ', Math.Max(WaterfallWidth - 4 - end.Length, 1))}{end}");
    }

    private static string RenderBar(TranslationDiagnosticTiming phase, double total)
    {
        var start = Math.Clamp(
            (int)Math.Round(phase.StartedMs / total * WaterfallWidth), 0, WaterfallWidth - 1);
        var length = Math.Clamp(
            (int)Math.Round(Math.Max(phase.DurationMs, 0) / total * WaterfallWidth), 1, WaterfallWidth - start);

        var bar = new StringBuilder(WaterfallWidth);
        bar.Append(' ', start);
        bar.Append(phase.IsRunning ? '~' : '#', length);
        return bar.ToString().PadRight(WaterfallWidth);
    }

    private static string ToMs(double value) => value.ToString("F0", CultureInfo.CurrentCulture);
}
