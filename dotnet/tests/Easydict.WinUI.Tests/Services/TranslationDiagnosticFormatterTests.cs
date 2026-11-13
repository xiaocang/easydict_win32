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
    public void FormatKeepsStagesInMeasurementOrderWithStartAndDuration()
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
        preparation.Should().Contain("0 ms").And.Contain("100 ms");
        headers.Should().Contain("100 ms").And.Contain("400 ms");
    }

    [Fact]
    public void TextOutputHasNoArtworkOnlyRows()
    {
        var snapshot = new TranslationDiagnosticSnapshot(500, TranslationDiagnosticStatus.Completed,
            [new(TranslationDiagnosticStage.FirstOutput, 0, 200, false)]);
        var text = TranslationDiagnosticFormatter.Format(snapshot, key => key);
        text.Should().NotContain("#").And.NotContain("~");
    }
}
