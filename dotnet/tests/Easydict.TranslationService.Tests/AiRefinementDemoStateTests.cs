using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

public sealed class AiRefinementDemoStateTests
{
    [Fact]
    public async Task FeatureFlagsAreIndependentAndOrdinaryServicesHaveNoRefinementActions()
    {
        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: false,
            wordRefinementEnabled: true);

        state.CanShowContext.Should().BeFalse();
        state.CanShowWordRefinement.Should().BeTrue();

        using var ordinaryService = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: false,
            contextFeatureEnabled: true,
            wordRefinementEnabled: true);

        ordinaryService.CanShowContext.Should().BeFalse();
        ordinaryService.CanShowWordRefinement.Should().BeFalse();
        await ordinaryService.PrefetchCandidatesAsync();
        await ordinaryService.SelectWordAsync("space");
        ordinaryService.HasSelectedWord.Should().BeFalse();
    }

    [Fact]
    public async Task ContextChangeInvalidatesOldCandidatesAndKeepsConfirmedWording()
    {
        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: true,
            wordRefinementEnabled: true);

        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("space");
        var confirmedWording = state.SelectedCandidates[0];
        state.ApplyCandidate("space", confirmedWording).Should().BeTrue();
        state.TranslationText.Should().Contain(confirmedWording);

        state.ApplyContext("Use a formal tone.");
        state.TranslationText.Should().Contain(confirmedWording);
        state.TranslationText.Should().Contain("创意");

        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("cause");
        state.SelectedCandidates.Should().Contain("促使");
        state.ContextRevision.Should().Be(1);
    }

    [Fact]
    public async Task FormalContextOffersAResolvablePromptForConfirmedInformalWording()
    {
        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: true,
            wordRefinementEnabled: true);

        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("growth");
        state.SelectedCandidates.Should().Contain("蓬勃发展");
        state.ApplyCandidate("growth", "蓬勃发展").Should().BeTrue();

        state.ApplyContext("Use a formal tone.");
        state.ContextConflict.Should().Be(new AiRefinementDemoContextConflict("growth", "蓬勃发展", "稳步发展"));
        state.TranslationText.Should().Contain("蓬勃发展");

        state.ResolveContextConflict(useSuggestedWording: false).Should().BeTrue();
        state.HasContextConflict.Should().BeFalse();
        state.TranslationText.Should().Contain("蓬勃发展");

        state.ApplyContext("Use a formal tone.");
        state.HasContextConflict.Should().BeTrue();
        state.ResolveContextConflict(useSuggestedWording: true).Should().BeTrue();
        state.HasContextConflict.Should().BeFalse();
        state.TranslationText.Should().Contain("稳步发展");
        state.TranslationText.Should().NotContain("蓬勃发展");
    }

    [Theory]
    [InlineData("Use a formal tone.", true)]
    [InlineData("Use an informal tone.", false)]
    [InlineData("正式语气", true)]
    [InlineData("非正式语气", false)]
    public async Task ContextToneClassificationHandlesPositiveAndNegatedMarkers(string context, bool isFormal)
    {
        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: true,
            wordRefinementEnabled: true);

        await state.SelectWordAsync("growth");
        state.SelectedCandidates.Should().Contain("蓬勃发展");
        state.ApplyCandidate("growth", "蓬勃发展").Should().BeTrue();

        state.ApplyContext(context);
        state.TranslationText.Should().Contain(isFormal ? "宁静的工作空间" : "安静的空间");
        state.HasContextConflict.Should().Be(isFormal);

        await state.SelectWordAsync("space");
        state.SelectedCandidates.Should().Contain(isFormal ? "宁静的办公环境" : "宁静的工作环境");
        state.SelectedCandidates.Should().NotContain(isFormal ? "宁静的工作环境" : "宁静的办公环境");
    }

    [Fact]
    public async Task OlderPrefetchCannotPopulateCandidatesForANewerContext()
    {
        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: true,
            wordRefinementEnabled: true);

        var olderPrefetch = state.PrefetchCandidatesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(40));
        state.ApplyContext("Use a formal tone.");
        var currentPrefetch = state.PrefetchCandidatesAsync();

        await Task.WhenAll(olderPrefetch, currentPrefetch);
        await state.SelectWordAsync("space");

        state.SelectedCandidates.Should().Equal("宁静的办公环境", "安静的工作场所");
        state.ContextRevision.Should().Be(1);
    }

    [Fact]
    public async Task MissingPrefetchEntryLoadsCandidatesOnDemand()
    {
        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: false,
            wordRefinementEnabled: true);

        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("growth");

        state.SelectedCandidates.Should().Equal("蓬勃发展", "茁壮成长");
        state.IsSelectedWordLoading.Should().BeFalse();
    }
}
