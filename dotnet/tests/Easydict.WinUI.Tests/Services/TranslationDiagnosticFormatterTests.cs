using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class TranslationDiagnosticFormatterTests
{
    [Theory]
    [InlineData(TranslationDiagnosticStatus.Running)]
    [InlineData(TranslationDiagnosticStatus.Completed)]
    [InlineData(TranslationDiagnosticStatus.Failed)]
    [InlineData(TranslationDiagnosticStatus.Cancelled)]
    public void FormatShowsLocalizedPhasesAndStatusWithoutInventingUnmeasuredTimings(TranslationDiagnosticStatus status)
    {
        var snapshot = new TranslationDiagnosticSnapshot(450, status,
            [new(TranslationDiagnosticStage.FirstOutput, 30, 120, status == TranslationDiagnosticStatus.Running)]);
        var text = TranslationDiagnosticFormatter.Format(snapshot, key => "localized:" + key);
        text.Should().Contain("450").And.Contain("120");
        text.Should().Contain("localized:DiagnosticStatus" + status);
        text.Should().Contain("localized:DiagnosticStageFirstOutput").And.Contain("localized:DiagnosticTimingNote");
        text.Should().NotContain("DiagnosticStageResponseHeaders");
    }
}
