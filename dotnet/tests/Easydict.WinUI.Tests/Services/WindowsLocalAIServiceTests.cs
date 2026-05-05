using System.Runtime.CompilerServices;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Unit tests for the Windows Local AI (Phi Silica) translation provider.
/// Uses a fake <see cref="IWindowsLanguageModelClient"/> so the suite can run
/// on any machine — Copilot+ NPU is not required.
/// </summary>
public class WindowsLocalAIServiceTests
{
    [Fact]
    public void ServiceId_IsWindowsLocalAi()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.ServiceId.Should().Be("windows-local-ai");
    }

    [Fact]
    public void DisplayName_IsWindowsLocalAI()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.DisplayName.Should().Be("Windows Local AI");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_TargetAuto_ReturnsFalse()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.SupportsLanguagePair(Language.English, Language.Auto).Should().BeFalse();
    }

    [Fact]
    public void SupportsLanguagePair_TargetReal_ReturnsTrue()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        service.SupportsLanguagePair(Language.Auto, Language.SimplifiedChinese).Should().BeTrue();
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_ThrowsInvalidResponse()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        var request = new TranslationRequest { Text = "   ", ToLanguage = Language.English };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
        ex.Which.ServiceId.Should().Be("windows-local-ai");
    }

    [Fact]
    public async Task TranslateAsync_TargetAuto_ThrowsUnsupportedLanguage()
    {
        var service = new WindowsLocalAIService(new FakeClient());
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.Auto };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.UnsupportedLanguage);
    }

    [Fact]
    public async Task TranslateAsync_ModelNotReadyAndCannotPrepare_ThrowsServiceUnavailable()
    {
        var client = new FakeClient
        {
            ReadyState = WindowsAIReadyState.NotCompatibleWithSystemHardware,
        };
        var service = new WindowsLocalAIService(client);
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.SimplifiedChinese };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        ex.Which.Message.Should().Contain("Copilot+");
    }

    [Fact]
    public async Task TranslateAsync_PromptLargerThanContext_MapsToTextTooLong()
    {
        var client = new FakeClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.PromptLargerThanContext,
                string.Empty,
                "context overflow"),
        };
        var service = new WindowsLocalAIService(client);
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.SimplifiedChinese };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.TextTooLong);
    }

    [Fact]
    public async Task TranslateAsync_BlockedByPolicy_MapsToServiceUnavailable()
    {
        var client = new FakeClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.BlockedByPolicy,
                string.Empty,
                "blocked"),
        };
        var service = new WindowsLocalAIService(client);
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.SimplifiedChinese };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
    }

    [Fact]
    public async Task TranslateAsync_OnComplete_ReturnsCleanedTranslation()
    {
        var client = new FakeClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.Complete,
                "Translation: \"你好\"\n"),
        };
        var service = new WindowsLocalAIService(client);
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese,
        };

        var result = await service.TranslateAsync(request);

        result.TranslatedText.Should().Be("你好");
        result.OriginalText.Should().Be("Hello");
        result.ServiceName.Should().Be("Windows Local AI");
        result.TargetLanguage.Should().Be(Language.SimplifiedChinese);
    }

    [Fact]
    public async Task TranslateStreamAsync_YieldsChunksInOrder()
    {
        var client = new FakeClient
        {
            StreamChunks = new[] { "你", "好", "，", "世界" },
        };
        var service = new WindowsLocalAIService(client);
        var request = new TranslationRequest
        {
            Text = "Hello, world",
            ToLanguage = Language.SimplifiedChinese,
        };

        var collected = new List<string>();
        await foreach (var chunk in service.TranslateStreamAsync(request))
        {
            collected.Add(chunk);
        }

        collected.Should().Equal("你", "好", "，", "世界");
    }

    [Fact]
    public async Task TranslateStreamAsync_DropsEmptyChunks()
    {
        var client = new FakeClient
        {
            StreamChunks = new[] { "Hello", string.Empty, " ", "world" },
        };
        var service = new WindowsLocalAIService(client);
        var request = new TranslationRequest
        {
            Text = "Test",
            ToLanguage = Language.English,
        };

        var collected = new List<string>();
        await foreach (var chunk in service.TranslateStreamAsync(request))
        {
            collected.Add(chunk);
        }

        // Empty strings filtered, " " is preserved (it's a real token)
        collected.Should().Equal("Hello", " ", "world");
    }

    [Fact]
    public void BuildTranslationPrompt_IncludesSourceAndTargetLanguage()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };

        var prompt = WindowsLocalAIService.BuildTranslationPrompt(request);

        prompt.Should().Contain("English");
        prompt.Should().Contain("Simplified Chinese");
        prompt.Should().Contain("Hello");
    }

    [Fact]
    public void BuildTranslationPrompt_AutoSource_UsesAutoDetectionPhrase()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.Japanese,
        };

        var prompt = WindowsLocalAIService.BuildTranslationPrompt(request);

        prompt.Should().Contain("auto-detected");
    }

    [Fact]
    public void BuildTranslationPrompt_PreservesCustomPrompt()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.French,
            CustomPrompt = "Use formal register and prefer Quebec spellings.",
        };

        var prompt = WindowsLocalAIService.BuildTranslationPrompt(request);

        prompt.Should().Contain("Additional user instruction");
        prompt.Should().Contain("Quebec spellings");
    }

    [Fact]
    public void BuildTranslationPrompt_NoCustomPrompt_DoesNotEmitInstructionHeader()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.German,
        };

        var prompt = WindowsLocalAIService.BuildTranslationPrompt(request);

        prompt.Should().NotContain("Additional user instruction");
    }

    private sealed class FakeClient : IWindowsLanguageModelClient
    {
        public WindowsAIReadyState ReadyState { get; set; } = WindowsAIReadyState.Ready;

        public WindowsAIResponse GenerateResponse { get; set; } =
            new(WindowsAIResponseStatus.Complete, "ok");

        public IReadOnlyList<string> StreamChunks { get; set; } = Array.Empty<string>();

        public string? LastPrompt { get; private set; }

        public WindowsAIReadyState GetReadyState() => ReadyState;

        public Task<WindowsAIReadyState> EnsureReadyAsync(CancellationToken cancellationToken)
            => Task.FromResult(ReadyState);

        public Task<WindowsAIResponse> GenerateAsync(
            string prompt,
            WindowsAIGenerationOptions options,
            CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return Task.FromResult(GenerateResponse);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string prompt,
            WindowsAIGenerationOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            foreach (var chunk in StreamChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }
}
