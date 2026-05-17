using System.Net;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class FoundryLocalRecoveryCoordinatorTests
{
    [Fact]
    public async Task StartAndRetryAsync_WhenFoundryWasStoppedAndRetryCompletes_ClearsTranslatingState()
    {
        var serviceResult = CreateStoppedFoundryResult();
        var runtime = new StoppedFoundryRuntimeController("http://127.0.0.1:5273/openai/status");
        var foundryService = new FoundryLocalService(
            new HttpClient(new ReadyFoundryEndpointHandler()),
            runtime);
        foundryService.Configure(endpoint: "", model: "test-model");
        var refreshStatuses = new List<string>();
        var prepareCalls = 0;
        var retryCalls = 0;

        await FoundryLocalRecoveryCoordinator.StartAndRetryAsync(
            serviceResult,
            async ct =>
            {
                prepareCalls++;
                return await foundryService.PrepareAsync(ct);
            },
            async (result, _) =>
            {
                retryCalls++;

                result.IsLoading = true;
                result.IsStreaming = true;
                result.StreamingText = "Spot";
                result.MarkQueried();
                await Task.Yield();

                result.IsStreaming = false;
                result.StreamingText = "";
                result.Result = new TranslationResult
                {
                    OriginalText = "\u62bd\u6d4b\u8bd5",
                    TranslatedText = "Spot test",
                    DetectedLanguage = Language.SimplifiedChinese,
                    TargetLanguage = Language.English,
                    ServiceName = "Windows Local AI",
                    TimingMs = 42
                };
                // Intentionally leave IsLoading=true here. The coordinator must clear it
                // after the started Foundry retry completes, otherwise the row stays on Translating.
            },
            result => refreshStatuses.Add(result.StatusText),
            CreateRecoveryException);

        prepareCalls.Should().Be(1);
        retryCalls.Should().Be(1);
        runtime.StartCalls.Should().Be(1);
        runtime.LoadedModels.Should().Equal("test-model");
        serviceResult.Error.Should().BeNull();
        serviceResult.Result?.TranslatedText.Should().Be("Spot test");
        serviceResult.IsStreaming.Should().BeFalse();
        serviceResult.IsLoading.Should().BeFalse();
        serviceResult.StatusText.Should().Be("42ms");
        serviceResult.HasQueried.Should().BeTrue();
        refreshStatuses.Should().Contain("Translating...");
        refreshStatuses.Last().Should().Be("42ms");
    }

    [Fact]
    public async Task StartAndRetryAsync_WhenRetryFailsAfterStartingStoppedFoundry_ClearsLoadingAndSurfacesError()
    {
        var serviceResult = CreateStoppedFoundryResult();
        var prepareCalls = 0;
        var retryCalls = 0;

        await FoundryLocalRecoveryCoordinator.StartAndRetryAsync(
            serviceResult,
            _ =>
            {
                prepareCalls++;
                return Task.FromResult(new LocalModelStatus(LocalModelState.Ready, "FoundryLocal_Status_Ready"));
            },
            (result, _) =>
            {
                retryCalls++;
                result.IsLoading = true;
                result.MarkQueried();
                throw new TranslationException("Foundry Local refused the retry connection")
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = FoundryLocalService.ServiceIdValue,
                    RecoveryAction = FoundryLocalResources.StartRecoveryAction,
                    DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
                };
            },
            _ => { },
            CreateRecoveryException);

        prepareCalls.Should().Be(1);
        retryCalls.Should().Be(1);
        serviceResult.IsLoading.Should().BeFalse();
        serviceResult.IsStreaming.Should().BeFalse();
        serviceResult.Error.Should().NotBeNull();
        serviceResult.Error!.RecoveryAction.Should().Be(FoundryLocalResources.StartRecoveryAction);
        serviceResult.StatusText.Should().Be("Error");
    }

    private static ServiceQueryResult CreateStoppedFoundryResult()
    {
        return new ServiceQueryResult
        {
            ServiceId = "windows-local-ai",
            ServiceDisplayName = "Windows Local AI",
            Error = new TranslationException("Foundry Local is not running")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = FoundryLocalService.ServiceIdValue,
                RecoveryAction = FoundryLocalResources.StartRecoveryAction,
                DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
            }
        };
    }

    private static TranslationException CreateRecoveryException(LocalModelStatus status)
    {
        return new TranslationException(status.ResourceKey)
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = FoundryLocalService.ServiceIdValue,
            RecoveryAction = FoundryLocalResources.StartRecoveryAction,
            DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
        };
    }

    private sealed class StoppedFoundryRuntimeController : IFoundryLocalRuntimeController
    {
        private readonly string _readyEndpoint;
        private FoundryLocalRuntimeStatus _status;

        public StoppedFoundryRuntimeController(string readyEndpoint)
        {
            _readyEndpoint = readyEndpoint;
            _status = new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning);
        }

        public int StartCalls { get; private set; }

        public List<string> LoadedModels { get; } = [];

        public Task<FoundryLocalRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_status);
        }

        public Task StartServiceAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            _status = new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.Running, _readyEndpoint);
            return Task.CompletedTask;
        }

        public Task LoadModelAsync(string model, CancellationToken cancellationToken)
        {
            LoadedModels.Add(model);
            _status = new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.Running, _readyEndpoint);
            return Task.CompletedTask;
        }

        public Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_status.Endpoint);
        }
    }

    private sealed class ReadyFoundryEndpointHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"data":[{"id":"test-model"}],"object":"list"}
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
