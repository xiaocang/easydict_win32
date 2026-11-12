using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class TranslationDiagnosticWaterfallTests
{
    [Fact]
    public void OverlappingStagesKeepTheirOwnOffsetAndLengthOnTheSharedScale()
    {
        var snapshot = new TranslationDiagnosticSnapshot(1000, TranslationDiagnosticStatus.Completed,
        [
            new(TranslationDiagnosticStage.QueryPreparation, 0, 100, false),
            new(TranslationDiagnosticStage.ResponseHeaders, 100, 400, false),
        ]);

        var bars = TranslationDiagnosticWaterfall.Build(snapshot);

        bars[0].OffsetRatio.Should().BeApproximately(0, 0.0001);
        bars[0].LengthRatio.Should().BeApproximately(0.1, 0.0001);
        bars[1].OffsetRatio.Should().BeApproximately(0.1, 0.0001);
        bars[1].LengthRatio.Should().BeApproximately(0.4, 0.0001);
    }

    [Fact]
    public void ScaleCoversAPhaseThatOutlivesTheFrozenTotal()
    {
        var snapshot = new TranslationDiagnosticSnapshot(1000, TranslationDiagnosticStatus.Completed,
            [new(TranslationDiagnosticStage.PhoneticEnrichment, 900, 300, true)]);

        var bar = TranslationDiagnosticWaterfall.Build(snapshot).Single();

        TranslationDiagnosticWaterfall.ResolveScale(snapshot).Should().Be(1200);
        (bar.OffsetRatio + bar.LengthRatio).Should().BeApproximately(1, 0.0001);
        bar.IsRunning.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-20, 5)]
    public void DegenerateTimingsStayInsideTheTrack(double startedMs, double durationMs)
    {
        var snapshot = new TranslationDiagnosticSnapshot(0, TranslationDiagnosticStatus.Running,
            [new(TranslationDiagnosticStage.FirstOutput, startedMs, durationMs, true)]);

        var bar = TranslationDiagnosticWaterfall.Build(snapshot).Single();

        bar.OffsetRatio.Should().BeGreaterThanOrEqualTo(0);
        (bar.OffsetRatio + bar.LengthRatio).Should().BeLessThanOrEqualTo(1);
    }
}
