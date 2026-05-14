using System.Runtime.CompilerServices;
using Easydict.OpenVINO.Inference;
using Easydict.OpenVINO.Models;
using Easydict.OpenVINO.Services;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Integration tests for the single user-facing local AI service. These tests
/// execute through TranslationManager so provider routing, service IDs, and
/// result normalization are covered together.
/// </summary>
[Trait("Category", "WinUI")]
public sealed class LocalAITranslationServiceIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ModelDownloadService _downloader;

    public LocalAITranslationServiceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EasydictLocalAiIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var modelDir = Path.Combine(_tempDir, ModelManifest.CacheDirectoryName);
        Directory.CreateDirectory(modelDir);
        foreach (var file in ModelManifest.Files)
        {
            File.WriteAllText(Path.Combine(modelDir, file.LocalFileName), "stub");
        }

        File.WriteAllText(Path.Combine(modelDir, ModelManifest.CompletionSentinel), "stub");
        _downloader = new ModelDownloadService(new HttpClient(), _tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task TranslationManager_UsesPhiSilicaBackend_WhenProviderIsWindowsAI()
    {
        var phiClient = new FakeWindowsAIClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.Complete,
                "Translation: \"你好\""),
        };
        var openVinoEngine = new FakeNllbEngine { TokenStream = [100, 200] };
        using var openVino = CreateOpenVino(openVinoEngine);
        var foundryLocal = new FakeFoundryLocalService();
        using var manager = CreateManager(
            new PhiSilicaTranslationService(phiClient),
            foundryLocal,
            openVino,
            LocalAIProviderMode.WindowsAI);
        var request = CreateRequest("Hello");

        var result = await manager.TranslateAsync(request, serviceId: "windows-local-ai");

        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Be("Windows Local AI");
        phiClient.LastPrompt.Should().Contain("Hello");
        foundryLocal.TranslateCallCount.Should().Be(0);
        openVinoEngine.GenerateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranslationManager_UsesFoundryLocalBackend_WhenProviderIsFoundryLocal()
    {
        var phiClient = new FakeWindowsAIClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.Complete,
                "should not be used"),
        };
        var foundryLocal = new FakeFoundryLocalService { ResponseText = "你好 from Foundry" };
        var openVinoEngine = new FakeNllbEngine { TokenStream = [100, 200] };
        using var openVino = CreateOpenVino(openVinoEngine);
        using var manager = CreateManager(
            new PhiSilicaTranslationService(phiClient),
            foundryLocal,
            openVino,
            LocalAIProviderMode.FoundryLocal);
        var request = CreateRequest("Hello");

        var result = await manager.TranslateAsync(request, serviceId: "windows-local-ai");

        result.TranslatedText.Should().Be("你好 from Foundry");
        result.ServiceName.Should().Be("Windows Local AI");
        phiClient.LastPrompt.Should().BeNull();
        foundryLocal.TranslateCallCount.Should().Be(1);
        openVinoEngine.GenerateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranslationManager_UsesOpenVinoBackend_WhenProviderIsOpenVINO()
    {
        var phiClient = new FakeWindowsAIClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.Complete,
                "should not be used"),
        };
        var openVinoEngine = new FakeNllbEngine { TokenStream = [100, 200] };
        using var openVino = CreateOpenVino(openVinoEngine);
        var foundryLocal = new FakeFoundryLocalService();
        using var manager = CreateManager(
            new PhiSilicaTranslationService(phiClient),
            foundryLocal,
            openVino,
            LocalAIProviderMode.OpenVINO);
        var request = CreateRequest("Hello");

        var result = await manager.TranslateAsync(request, serviceId: "windows-local-ai");

        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Be("Windows Local AI");
        phiClient.LastPrompt.Should().BeNull();
        foundryLocal.TranslateCallCount.Should().Be(0);
        openVinoEngine.GenerateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task TranslationManager_AutoProvider_FallsBackFromPhiSilicaToFoundryLocalBeforeOpenVino()
    {
        var phiClient = new FakeWindowsAIClient
        {
            GenerateResponse = new WindowsAIResponse(
                WindowsAIResponseStatus.BlockedByPolicy,
                string.Empty,
                "blocked by local policy"),
        };
        var foundryLocal = new FakeFoundryLocalService { ResponseText = "你好 from Foundry" };
        var openVinoEngine = new FakeNllbEngine { TokenStream = [100, 200] };
        using var openVino = CreateOpenVino(openVinoEngine);
        using var manager = CreateManager(
            new PhiSilicaTranslationService(phiClient),
            foundryLocal,
            openVino,
            LocalAIProviderMode.Auto);
        var request = CreateRequest("Hello");

        var result = await manager.TranslateAsync(request, serviceId: "windows-local-ai");

        result.TranslatedText.Should().Be("你好 from Foundry");
        result.ServiceName.Should().Be("Windows Local AI");
        phiClient.LastPrompt.Should().Contain("Hello");
        foundryLocal.TranslateCallCount.Should().Be(1);
        openVinoEngine.GenerateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranslationManager_AutoProvider_FallsBackFromFoundryLocalToOpenVino()
    {
        var phiClient = new FakeWindowsAIClient
        {
            ReadyState = WindowsAIReadyState.NotCompatibleWithSystemHardware,
        };
        var foundryLocal = new FakeFoundryLocalService
        {
            Failure = new TranslationException("Foundry Local is not running")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = "foundry-local",
            },
        };
        var openVinoEngine = new FakeNllbEngine { TokenStream = [100, 200] };
        using var openVino = CreateOpenVino(openVinoEngine);
        using var manager = CreateManager(
            new PhiSilicaTranslationService(phiClient),
            foundryLocal,
            openVino,
            LocalAIProviderMode.Auto);
        var request = CreateRequest("Hello");

        var result = await manager.TranslateAsync(request, serviceId: "windows-local-ai");

        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Be("Windows Local AI");
        phiClient.LastPrompt.Should().BeNull();
        foundryLocal.TranslateCallCount.Should().Be(1);
        openVinoEngine.GenerateCallCount.Should().Be(1);
    }

    private static TranslationRequest CreateRequest(string text)
    {
        return new TranslationRequest
        {
            Text = text,
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        };
    }

    private TranslationManager CreateManager(
        PhiSilicaTranslationService phiSilica,
        IStreamTranslationService foundryLocal,
        OpenVINOTranslationService openVino,
        LocalAIProviderMode mode)
    {
        var localAI = new LocalAITranslationService(phiSilica, foundryLocal, openVino);
        localAI.Configure(mode);

        var manager = new TranslationManager();
        manager.RegisterService(localAI);
        return manager;
    }

    private sealed class FakeFoundryLocalService : IStreamTranslationService, ILocalModelProvider
    {
        private static readonly IReadOnlyList<Language> _languages =
        [
            Language.English,
            Language.SimplifiedChinese,
            Language.TraditionalChinese,
            Language.Japanese,
            Language.Korean,
            Language.French,
            Language.German,
            Language.Spanish,
        ];

        public string ServiceId => "foundry-local";
        public string DisplayName => "Foundry Local";
        public bool RequiresApiKey => false;
        public bool IsConfigured { get; init; } = true;
        public bool IsStreaming => true;
        public IReadOnlyList<Language> SupportedLanguages => _languages;
        public string ResponseText { get; init; } = "Foundry result";
        public TranslationException? Failure { get; init; }
        public int TranslateCallCount { get; private set; }

        public event EventHandler<LocalModelStatus>? StatusChanged;

        public bool SupportsLanguagePair(Language from, Language to)
        {
            return to != Language.Auto
                && (from == Language.Auto || SupportedLanguages.Contains(from))
                && SupportedLanguages.Contains(to);
        }

        public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Language.Auto);
        }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            TranslateCallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = ResponseText,
                DetectedLanguage = request.FromLanguage,
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName,
            });
        }

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            TranslateCallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            await Task.Yield();
            yield return ResponseText;
        }

        public LocalModelStatus GetStatus()
        {
            return new LocalModelStatus(LocalModelState.Ready, "FoundryLocal_Status_Ready");
        }

        public Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
        {
            var status = GetStatus();
            StatusChanged?.Invoke(this, status);
            return Task.FromResult(status);
        }
    }

    private OpenVINOTranslationService CreateOpenVino(FakeNllbEngine engine)
    {
        var tokenizer = new FakeNllbTokenizer
        {
            DecodedPieces =
            {
                [100] = "你",
                [200] = "好",
            },
        };

        return new OpenVINOTranslationService(_downloader, tokenizer, engine);
    }

    private sealed class FakeWindowsAIClient : IWindowsLanguageModelClient
    {
        public WindowsAIReadyState ReadyState { get; set; } = WindowsAIReadyState.Ready;

        public WindowsAIResponse GenerateResponse { get; set; } =
            new(WindowsAIResponseStatus.Complete, "ok");

        public string? LastPrompt { get; private set; }

        public WindowsAIReadyState GetReadyState() => ReadyState;

        public Task<WindowsAIReadyState> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ReadyState);
        }

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
            var response = await GenerateAsync(prompt, options, cancellationToken);
            yield return response.Text;
        }
    }

    private sealed class FakeNllbTokenizer : INllbTokenizer
    {
        public Dictionary<int, string?> DecodedPieces { get; } = new();

        public int BosTokenId => 0;
        public int PadTokenId => 1;
        public int EosTokenId => 2;
        public int UnkTokenId => 3;

        public IReadOnlyList<int> EncodeSource(string text, string srcFloresCode)
        {
            return [srcFloresCode.GetHashCode(), 42, EosTokenId];
        }

        public string Decode(IReadOnlyList<int> tokenIds)
        {
            return string.Concat(tokenIds.Select(id => DecodedPieces.TryGetValue(id, out var piece) ? piece : ""));
        }

        public string? DecodeSingle(int tokenId)
        {
            return DecodedPieces.TryGetValue(tokenId, out var piece) ? piece : null;
        }

        public int GetLanguageTokenId(string floresCode) => floresCode.GetHashCode();
    }

    private sealed class FakeNllbEngine : INllbInferenceEngine
    {
        public IReadOnlyList<int> TokenStream { get; set; } = [];
        public int GenerateCallCount { get; private set; }

        public async IAsyncEnumerable<int> GenerateAsync(
            IReadOnlyList<int> encoderInputIds,
            int forcedBosTokenId,
            int maxNewTokens,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            GenerateCallCount++;
            foreach (var token in TokenStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return token;
            }
        }
    }
}
