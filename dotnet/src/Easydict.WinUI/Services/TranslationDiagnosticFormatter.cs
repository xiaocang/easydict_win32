using System.Globalization;
using System.Text;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

internal static class TranslationDiagnosticFormatter
{
    public static string Format(TranslationDiagnosticSnapshot snapshot, Func<string, string> localize)
    {
        var text = new StringBuilder();
        text.AppendLine($"{localize("DiagnosticTotal")}: {snapshot.TotalMs.ToString("F0", CultureInfo.CurrentCulture)} ms");
        text.AppendLine(localize("DiagnosticStatus" + snapshot.Status));
        text.AppendLine();
        text.AppendLine(localize("DiagnosticColumns"));
        foreach (var phase in snapshot.Timings)
        {
            text.AppendLine($"{phase.StartedMs,8:F0} ms  {phase.DurationMs,8:F0} ms  "
                + localize("DiagnosticStage" + phase.Stage)
                + (phase.IsRunning ? $" ({localize("DiagnosticStatusRunning")})" : ""));
        }
        text.AppendLine();
        text.Append(localize("DiagnosticTimingNote"));
        return text.ToString();
    }
}
