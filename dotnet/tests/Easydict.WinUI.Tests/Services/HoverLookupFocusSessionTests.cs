using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HoverLookupFocusSessionTests
{
    [Fact]
    public async Task SlowRecognition_RepeatsUntilResolved_ThenWaitsForSuccessBeforeQuerying()
    {
        var session = new HoverLookupFocusSession();
        for (var cycle = 0; cycle < 4; cycle++)
        {
            session.CompleteCycle().Should().Be(HoverLookupFocusState.Focusing);
            session.QueryReady.IsCompleted.Should().BeFalse();
        }

        session.Resolve(true);
        session.QueryReady.IsCompleted.Should().BeFalse();
        session.CompleteCycle().Should().Be(HoverLookupFocusState.Succeeded);
        session.QueryReady.IsCompleted.Should().BeFalse();
        session.CompleteSuccess();

        (await session.QueryReady).Should().BeTrue();
        session.State.Should().Be(HoverLookupFocusState.Querying);
    }

    [Fact]
    public async Task FastRecognition_StillFinishesFirstCycleAndSuccessAnimation()
    {
        var session = new HoverLookupFocusSession();
        session.Resolve(true);
        session.CompleteSuccess(); // An early animation completion cannot start the query.
        session.QueryReady.IsCompleted.Should().BeFalse();
        session.CompleteCycle().Should().Be(HoverLookupFocusState.Succeeded);
        session.CompleteSuccess();
        (await session.QueryReady).Should().BeTrue();
    }

    [Fact]
    public async Task RecognitionFailure_PlaysFailureAndNeverStartsQuery()
    {
        var session = new HoverLookupFocusSession();
        session.Resolve(false);
        session.CompleteCycle().Should().Be(HoverLookupFocusState.Failed);
        session.CompleteSuccess();
        session.QueryReady.IsCompleted.Should().BeFalse();
        session.CompleteFailure();
        (await session.QueryReady).Should().BeFalse();
        session.State.Should().Be(HoverLookupFocusState.Finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dismissal_DuringFocusOrSuccess_PreventsLateAnimationFromStartingQuery(bool duringSuccess)
    {
        var session = new HoverLookupFocusSession();
        if (duringSuccess)
        {
            session.Resolve(true);
            session.CompleteCycle();
        }
        session.Cancel();
        session.Resolve(true);
        session.CompleteCycle().Should().Be(HoverLookupFocusState.Cancelled);
        session.CompleteSuccess();
        session.CompleteFailure();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryReady);
    }

    [Fact]
    public void LateRecognitionAfterFailure_DoesNotReverseOutcome()
    {
        var session = new HoverLookupFocusSession();
        session.Resolve(false);
        session.Resolve(true);
        session.CompleteCycle().Should().Be(HoverLookupFocusState.Failed);
    }
}
