using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

public class AutomaticTranslationRouterTests
{
    [Fact]
    public async Task StreamingTranslationApiWithoutPromptSupportStillWaitsForDetection()
    {
        var pending = new TaskCompletionSource<Language>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new AutomaticTranslationRouter(new(Language.English, Language.Japanese),
            () => pending.Task, _ => Language.English);
        using var client = new HttpClient();
        var request = router.CreateRequestAsync(new DoubaoService(client), Request, default);
        request.IsCompleted.Should().BeFalse();
        pending.SetResult(Language.French);
        (await request).AutomaticDirection.Should().BeNull();
        (await request).ToLanguage.Should().Be(Language.English);
    }

    private static readonly TranslationRequest Request = new()
    {
        Text = "bonjour", FromLanguage = Language.Auto, ToLanguage = Language.English
    };

    [Fact]
    public async Task LlmStartsWhileTraditionalServicesSharePendingDetection()
    {
        var pending = new TaskCompletionSource<Language>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var router = new AutomaticTranslationRouter(
            new(Language.English, Language.SimplifiedChinese),
            () => { calls++; return pending.Task; },
            source => source == Language.English ? Language.SimplifiedChinese : Language.English);
        using var client = new HttpClient();
        var traditional = new GoogleTranslateService(client);
        var first = router.CreateRequestAsync(traditional, Request, default);
        var second = router.CreateRequestAsync(traditional, Request, default);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();

        ITranslationService llm = new ProfiledStreamingTranslationService(
            new OpenAIService(client), "profile-test", "Test", "test", 1);
        var immediate = router.CreateRequestAsync(llm, Request, default);
        immediate.IsCompletedSuccessfully.Should().BeTrue();
        (await immediate).AutomaticDirection.Should().Be(new AutomaticLanguageDirection(Language.English, Language.SimplifiedChinese));
        (await immediate).ToLanguage.Should().Be(Language.Auto);
        calls.Should().Be(1);

        pending.SetResult(Language.English);
        (await first).ToLanguage.Should().Be(Language.SimplifiedChinese);
        (await second).FromLanguage.Should().Be(Language.English);
    }

    [Fact]
    public async Task LlmOnlyQueryDoesNotStartDetection()
    {
        var router = new AutomaticTranslationRouter(new(Language.English, Language.Japanese),
            () => throw new InvalidOperationException("Online detection must not start"), _ => Language.English);
        using var client = new HttpClient();
        var request = await router.CreateRequestAsync(new GeminiService(client), Request, default);
        request.AutomaticDirection.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelledQueryDoesNotStartDetection()
    {
        var router = new AutomaticTranslationRouter(new(Language.English, Language.Japanese),
            () => throw new InvalidOperationException("Online detection must not start"), _ => Language.English);
        using var client = new HttpClient();
        var action = () => router.CreateRequestAsync(new GoogleTranslateService(client), Request, new CancellationToken(true));
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(Language.Auto, Language.English)]
    [InlineData(Language.English, Language.English)]
    public async Task InvalidPreferencePairFallsBackToHostDetection(Language first, Language second)
    {
        var router = new AutomaticTranslationRouter(new(first, second),
            () => Task.FromResult(Language.French), _ => Language.English);
        using var client = new HttpClient();
        var request = await router.CreateRequestAsync(new GeminiService(client), Request, default);
        request.AutomaticDirection.Should().BeNull();
        request.FromLanguage.Should().Be(Language.French);
        request.ToLanguage.Should().Be(Language.English);
    }
}
