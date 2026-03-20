using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class QueryOutcomeSummaryTests
{
    [Fact]
    public void From_WithSuccessAndNeutral_CountsOnlySuccess()
    {
        var summary = QueryOutcomeSummary.From(
        [
            QueryExecutionOutcome.Success,
            QueryExecutionOutcome.Neutral
        ]);

        summary.SuccessCount.Should().Be(1);
        summary.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void From_WithOnlyNeutral_DoesNotCountFailure()
    {
        var summary = QueryOutcomeSummary.From(
        [
            QueryExecutionOutcome.Neutral
        ]);

        summary.SuccessCount.Should().Be(0);
        summary.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void From_WithError_CountsError()
    {
        var summary = QueryOutcomeSummary.From(
        [
            QueryExecutionOutcome.Error,
            QueryExecutionOutcome.Neutral
        ]);

        summary.SuccessCount.Should().Be(0);
        summary.ErrorCount.Should().Be(1);
    }
}
