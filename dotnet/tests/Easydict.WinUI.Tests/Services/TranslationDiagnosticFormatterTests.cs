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

    [Fact]
    public void FormatDrawsBarsAtEachStageOffsetSoOverlapIsVisible()
    {
        var snapshot = new TranslationDiagnosticSnapshot(1000, TranslationDiagnosticStatus.Completed,
        [
            new(TranslationDiagnosticStage.QueryPreparation, 0, 100, false),
            new(TranslationDiagnosticStage.ResponseHeaders, 100, 400, false),
        ]);
        var lines = TranslationDiagnosticFormatter.Format(snapshot, key => key)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        var preparation = lines.Single(line => line.EndsWith("DiagnosticStageQueryPreparation"));
        var headers = lines.Single(line => line.EndsWith("DiagnosticStageResponseHeaders"));

        // Both rows align in the bar column; the later stage starts to the right of the earlier one.
        preparation.IndexOf('#').Should().BeLessThan(headers.IndexOf('#'));
        headers.IndexOf('#').Should().Be(preparation.LastIndexOf('#') + 1);
        headers.Count(c => c == '#').Should().BeGreaterThan(preparation.Count(c => c == '#'));
    }

    [Fact]
    public void RunningStageUsesADistinctBarMarker()
    {
        var snapshot = new TranslationDiagnosticSnapshot(500, TranslationDiagnosticStatus.Running,
            [new(TranslationDiagnosticStage.FirstOutput, 0, 200, true)]);
        var line = TranslationDiagnosticFormatter.Format(snapshot, key => key)
            .Split(Environment.NewLine).Single(row => row.Contains("DiagnosticStageFirstOutput"));
        line.Should().Contain("~").And.NotContain("#");
    }
}
