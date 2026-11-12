using System.Globalization;
using System.Text;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Plain-text rendering of a query's timings. The dialog shows a visual waterfall; this text
/// stays because it is what Copy copies, what screen readers announce, and what tests assert on.
/// </summary>
internal static class TranslationDiagnosticFormatter
{
    public static string Format(TranslationDiagnosticSnapshot snapshot, Func<string, string> localize)
    {
        var text = new StringBuilder();
        text.AppendLine($"{localize("DiagnosticTotal")}: {ToMs(snapshot.TotalMs)} ms");
        text.AppendLine(localize("DiagnosticStatus" + snapshot.Status));
        text.AppendLine();
        text.AppendLine(localize("DiagnosticColumns"));
        foreach (var phase in snapshot.Timings)
        {
            text.AppendLine($"{ToMs(phase.StartedMs),8} ms  {ToMs(phase.DurationMs),8} ms  "
                + localize("DiagnosticStage" + phase.Stage)
                + (phase.IsRunning ? $" ({localize("DiagnosticStatusRunning")})" : ""));
        }
        text.AppendLine();
        text.Append(localize("DiagnosticTimingNote"));
        return text.ToString();
    }

    private static string ToMs(double value) => value.ToString("F0", CultureInfo.CurrentCulture);
}
