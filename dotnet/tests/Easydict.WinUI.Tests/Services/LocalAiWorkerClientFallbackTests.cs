using System.Runtime.CompilerServices;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.Workers;
using FluentAssertions;
using Xunit;
using SidecarClientType = Easydict.SidecarClient.SidecarClient;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class LocalAiWorkerClientFallbackTests
{
    [Fact]
    public async Task TranslateAsync_FallsBackToInProcService_WhenWorkerStartFails()
    {
        var fallback = new FallbackLocalAiService();
        using var client = CreateClient(fallback);

        var result = await client.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        });

        result.TranslatedText.Should().Be("fallback:hello");
        fallback.TranslateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task TranslateStreamAsync_FallsBackToInProcService_WhenWorkerStartFails()
    {
        var fallback = new FallbackLocalAiService();
        using var client = CreateClient(fallback);

        var chunks = new List<string>();
        await foreach (var chunk in client.TranslateStreamAsync(new TranslationRequest
                       {
                           Text = "hello",
                           FromLanguage = Language.English,
                           ToLanguage = Language.SimplifiedChinese,
                       }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().Equal("fallback", "-stream");
        fallback.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CorrectGrammarStreamAsync_FallsBackToInProcService_WhenWorkerStartFails()
    {
        var fallback = new FallbackLocalAiService();
        using var client = CreateClient(fallback);

        var chunks = new List<string>();
        await foreach (var chunk in client.CorrectGrammarStreamAsync(new GrammarCorrectionRequest
                       {
                           Text = "I has a apple.",
                           Language = Language.English,
                       }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().Equal("grammar", "-fallback");
        fallback.GrammarCallCount.Should().Be(1);
    }

    [Fact]
    public async Task PrepareAsync_FallsBackToInProcProvider_WhenWorkerStartFails()
    {
        var fallback = new FallbackLocalAiService();
        using var client = CreateClient(fallback);

        var status = await client.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        status.ResourceKey.Should().Be("FallbackReady");
        fallback.PrepareCallCount.Should().Be(1);
    }

    [Fact]
    public async Task TranslateStreamAsync_DoesNotFallbackInProc_WhenProviderIsOpenVino()
    {
        var originalProvider = SettingsService.Instance.LocalAIProvider;
        try
        {
            SettingsService.Instance.LocalAIProvider = "OpenVINO";
            var fallback = new FallbackLocalAiService();
            var spawnCount = 0;
            using var client = new LocalAiWorkerClient(
                SettingsService.Instance,
                fallback,
                fallback,
                fallback,
                _ =>
                {
                    spawnCount++;
                    return Task.FromException<SidecarClientType>(new WorkerStartFailedException("missing worker"));
                });

            var act = async () =>
            {
                await foreach (var _ in client.TranslateStreamAsync(new TranslationRequest
                               {
                                   Text = "hello",
                                   FromLanguage = Language.English,
                                   ToLanguage = Language.SimplifiedChinese,
                               }))
                {
                }
            };

            await act.Should().ThrowAsync<WorkerStartFailedException>();
            spawnCount.Should().Be(1);
            fallback.StreamCallCount.Should().Be(0);
        }
        finally
        {
            SettingsService.Instance.LocalAIProvider = originalProvider;
        }
    }

    [Fact]
    public async Task TranslateAsync_DoesNotFallbackInProc_WhenProviderIsOpenVino()
    {
        var originalProvider = SettingsService.Instance.LocalAIProvider;
        try
        {
            SettingsService.Instance.LocalAIProvider = "OpenVINO";
            var fallback = new FallbackLocalAiService();
            var spawnCount = 0;
            using var client = new LocalAiWorkerClient(
                SettingsService.Instance,
                fallback,
                fallback,
                fallback,
                _ =>
                {
                    spawnCount++;
                    return Task.FromException<SidecarClientType>(new WorkerStartFailedException("missing worker"));
                });

            var act = () => client.TranslateAsync(new TranslationRequest
            {
                Text = "hello",
                FromLanguage = Language.English,
                ToLanguage = Language.SimplifiedChinese,
            });

            await act.Should().ThrowAsync<WorkerStartFailedException>();
            spawnCount.Should().Be(1);
            fallback.TranslateCallCount.Should().Be(0);
        }
        finally
        {
            SettingsService.Instance.LocalAIProvider = originalProvider;
        }
    }

    [Fact]
    public void CanFallbackToInProc_ReturnsTrue_WhenWorkerProcessExitsUnexpectedly()
    {
        LocalAiWorkerClient.CanFallbackToInProc(new SidecarProcessExitedException(unchecked((int)0xC0000409)))
            .Should().BeTrue();
    }

    private static LocalAiWorkerClient CreateClient(FallbackLocalAiService fallback)
    {
        SettingsService.Instance.LocalAIProvider = LocalAiProviderModes.WindowsAI;
        return new LocalAiWorkerClient(
            SettingsService.Instance,
            fallback,
            fallback,
            fallback,
            _ => Task.FromException<SidecarClientType>(new WorkerStartFailedException("missing worker")));
    }

    private sealed class FallbackLocalAiService :
        IStreamTranslationService,
        IGrammarCorrectionService,
        ILocalModelProvider
    {
        public int TranslateCallCount { get; private set; }
        public int StreamCallCount { get; private set; }
        public int GrammarCallCount { get; private set; }
        public int PrepareCallCount { get; private set; }

        public string ServiceId => LocalAiWorkerClient.ServiceIdValue;
        public string DisplayName => "Fallback Local AI";
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public bool IsStreaming => true;
        public IReadOnlyList<Language> SupportedLanguages { get; } =
            Enum.GetValues<Language>().Where(language => language != Language.Auto).ToArray();

        public event EventHandler<LocalModelStatus>? StatusChanged;

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(Language.Auto);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            TranslateCallCount++;
            return Task.FromResult(new TranslationResult
            {
                TranslatedText = $"fallback:{request.Text}",
                OriginalText = request.Text,
                DetectedLanguage = request.FromLanguage,
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName,
            });
        }

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            StreamCallCount++;
            await Task.Yield();
            yield return "fallback";
            yield return "-stream";
        }

        public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
            GrammarCorrectionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            GrammarCallCount++;
            await Task.Yield();
            yield return "grammar";
            yield return "-fallback";
        }

        public bool SupportsGrammarCorrection(Language language) => true;

        public LocalModelStatus GetStatus() => new(LocalModelState.Ready, "FallbackReady");

        public Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            StatusChanged?.Invoke(this, new LocalModelStatus(LocalModelState.Ready, "FallbackReady"));
            return Task.FromResult(new LocalModelStatus(LocalModelState.Ready, "FallbackReady"));
        }
    }
}
