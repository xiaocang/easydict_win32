using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class FoundryLocalServiceTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5273", "http://127.0.0.1:5273/v1/chat/completions")]
    [InlineData("http://127.0.0.1:5273/v1", "http://127.0.0.1:5273/v1/chat/completions")]
    [InlineData("http://127.0.0.1:5273/v1/chat/completions", "http://127.0.0.1:5273/v1/chat/completions")]
    [InlineData("http://127.0.0.1:5273/openai/status", "http://127.0.0.1:5273/v1/chat/completions")]
    public void NormalizeChatCompletionsEndpoint_AcceptsBaseOrV1Endpoint(
        string input,
        string expected)
    {
        FoundryLocalService.NormalizeChatCompletionsEndpoint(input).Should().Be(expected);
    }

    [Fact]
    public void TryExtractEndpoint_UsesLocalOpenAiCompatibleEndpoint()
    {
        const string status = """
            Foundry Local service is running.
            Model management service is running on http://127.0.0.1:5273/openai/status
            """;

        FoundryLocalCliEndpointResolver.TryExtractEndpoint(status)
            .Should()
            .Be("http://127.0.0.1:5273/v1/chat/completions");
    }

    [Fact]
    public void TryExtractEndpoint_PrefersLoopbackEndpointOverRemoteEndpoint()
    {
        const string status = """
            Remote endpoint: http://10.1.2.3:5273/openai/status
            Local endpoint: http://localhost:5273/openai/status
            """;

        FoundryLocalCliEndpointResolver.TryExtractEndpoint(status)
            .Should()
            .Be("http://localhost:5273/v1/chat/completions");
    }

    [Fact]
    public void NormalizeChatCompletionsEndpoint_StripsQueryAndFragmentFromLoadEndpoint()
    {
        FoundryLocalService.NormalizeChatCompletionsEndpoint(
                "http://127.0.0.1:5273/openai/load/qwen2.5?ttl=600#ready")
            .Should()
            .Be("http://127.0.0.1:5273/v1/chat/completions");
    }

    [Fact]
    public void ParseRuntimeStatus_DetectsServiceNotRunning()
    {
        const string status = """
            Model management service is not running!
            To start the service, run: foundry service start
            """;

        FoundryLocalCliEndpointResolver.ParseRuntimeStatus(status).State
            .Should()
            .Be(FoundryLocalRuntimeState.NotRunning);
    }

    [Fact]
    public void ParseRuntimeStatus_CleansDecorativeStatusPrefixFromDetail()
    {
        const string status = "\u61C4 Model management service is not running!\r\n"
            + "To start the service, run the following command: foundry service start";

        var runtimeStatus = FoundryLocalCliEndpointResolver.ParseRuntimeStatus(status);

        runtimeStatus.State.Should().Be(FoundryLocalRuntimeState.NotRunning);
        runtimeStatus.DetailMessage.Should().StartWith("Model management service is not running!");
        runtimeStatus.DetailMessage.Should().NotContain("\u61C4");
    }

    [Fact]
    public void ParseRuntimeStatus_DetectsMissingCliOutput()
    {
        const string status = "'foundry' is not recognized as an internal or external command.";

        FoundryLocalCliEndpointResolver.ParseRuntimeStatus(status).State
            .Should()
            .Be(FoundryLocalRuntimeState.NotInstalled);
    }

    [Fact]
    public void ParseRuntimeStatus_ExtractsReadyEndpoint()
    {
        const string status = "Model management service is running on http://127.0.0.1:5273/openai/status";

        var runtimeStatus = FoundryLocalCliEndpointResolver.ParseRuntimeStatus(status);

        runtimeStatus.State.Should().Be(FoundryLocalRuntimeState.Running);
        runtimeStatus.Endpoint.Should().Be("http://127.0.0.1:5273/v1/chat/completions");
    }

    [Fact]
    public void ParseRuntimeStatus_DetectsAlreadyRunningServiceStartOutput()
    {
        const string status = "🟢 Service is already running on http://127.0.0.1:12192/.";

        var runtimeStatus = FoundryLocalCliEndpointResolver.ParseRuntimeStatus(status);

        runtimeStatus.State.Should().Be(FoundryLocalRuntimeState.Running);
        runtimeStatus.Endpoint.Should().Be("http://127.0.0.1:12192/v1/chat/completions");
    }

    [Fact]
    public void TryExtractLatestEndpoint_UsesMostRecentFoundryLogEndpoint()
    {
        const string log = """
            2026-05-15 08:10:00 [INF] Found service endpoints: http://127.0.0.1:3968
            2026-05-15 08:37:35 [INF] Loading model: http://127.0.0.1:1587/openai/load/qwen2.5-0.5b-instruct-openvino-npu:4?ttl=600
            """;

        FoundryLocalCliEndpointResolver.TryExtractLatestEndpoint(log)
            .Should()
            .Be("http://127.0.0.1:1587/v1/chat/completions");
    }

    [Fact]
    public void TryExtractEndpointFromLogDirectory_ReadsLatestFoundryLog()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "foundry-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        try
        {
            var oldLog = Path.Combine(logDirectory, "foundry20260514.log");
            var newLog = Path.Combine(logDirectory, "foundry20260515.log");
            File.WriteAllText(oldLog, "Found service endpoints: http://127.0.0.1:3968");
            File.WriteAllText(newLog, "Found service endpoints: http://127.0.0.1:1587");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newLog, DateTime.UtcNow);

            FoundryLocalCliEndpointResolver.TryExtractEndpointFromLogDirectory(logDirectory)
                .Should()
                .Be("http://127.0.0.1:1587/v1/chat/completions");
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TranslateAsync_PostsToConfiguredFoundryLocalEndpoint()
    {
        var handler = new CapturingHandler();
        var service = new FoundryLocalService(new HttpClient(handler));
        service.Configure("http://127.0.0.1:5273/v1", "test-model");

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        result.TranslatedText.Should().Be("你好");
        handler.RequestUri.Should().Be("http://127.0.0.1:5273/v1/chat/completions");
        handler.RequestBody.Should().Contain("\"model\":\"test-model\"");
    }

    [Fact]
    public async Task TranslateAsync_ResolvesAliasToLoadedModelId()
    {
        var handler = new ModelResolvingHandler();
        var service = new FoundryLocalService(new HttpClient(handler));
        service.Configure("http://127.0.0.1:5273/v1", "qwen2.5-0.5b");

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        result.TranslatedText.Should().Be("你好");
        handler.PostBody.Should().Contain("\"model\":\"qwen2.5-0.5b-instruct-openvino-npu:4\"");
    }

    [Fact]
    public async Task TranslateAsync_RefreshesLoopbackEndpointAfterConnectionFailure()
    {
        var handler = new EndpointRefreshHandler();
        var service = new FoundryLocalService(
            new HttpClient(handler),
            new StaticEndpointResolver("http://127.0.0.1:1587/"));
        service.Configure("http://127.0.0.1:3968/v1", "test-model");

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        result.TranslatedText.Should().Be("你好");
        handler.PostUris.Should().Contain("http://127.0.0.1:3968/v1/chat/completions");
        handler.PostUris.Should().Contain("http://127.0.0.1:1587/v1/chat/completions");
    }

    [Fact]
    public async Task TranslateAsync_PersistentLoopbackConnectionFailure_ExposesStartRecoveryAction()
    {
        var handler = new AlwaysRefusingHandler();
        var service = new FoundryLocalService(
            new HttpClient(handler),
            new StaticEndpointResolver("http://127.0.0.1:3474/"));
        service.Configure("http://127.0.0.1:3474/v1", "test-model");

        var act = async () => await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        exception.Which.RecoveryAction.Should().Be(FoundryLocalResources.StartRecoveryAction);
        exception.Which.DocumentationUrl.Should().Be(FoundryLocalResources.InstallDocumentationUrl);
        exception.Which.Message.Should().Contain("not accepting connections");
        handler.PostUris.Should().HaveCount(2);
    }

    [Fact]
    public async Task TranslateAsync_LoopbackConnectionFailureUsesRuntimeNotRunningDetail()
    {
        var handler = new AlwaysRefusingHandler();
        var runtime = new FakeRuntimeController(new FoundryLocalRuntimeStatus(
            FoundryLocalRuntimeState.NotRunning,
            DetailMessage: "Model management service is not running!"));
        var service = new FoundryLocalService(new HttpClient(handler), runtime);
        service.Configure("http://127.0.0.1:9890/v1", "test-model");

        var act = async () => await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        exception.Which.RecoveryAction.Should().Be(FoundryLocalResources.StartRecoveryAction);
        exception.Which.DocumentationUrl.Should().Be(FoundryLocalResources.InstallDocumentationUrl);
        exception.Which.Message.Should().Contain("Model management service is not running!");
        handler.PostUris.Should().ContainSingle("http://127.0.0.1:9890/v1/chat/completions");
    }

    [Fact]
    public async Task TranslateAsync_LoopbackConnectionFailureUsesRuntimeNotInstalledRecoveryAction()
    {
        var service = new FoundryLocalService(
            new HttpClient(new AlwaysRefusingHandler()),
            new FakeRuntimeController(new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.NotInstalled,
                DetailMessage: "Foundry Local CLI is missing.")));
        service.Configure("http://127.0.0.1:9890/v1", "test-model");

        var act = async () => await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        exception.Which.RecoveryAction.Should().Be(FoundryLocalResources.InstallRecoveryAction);
        exception.Which.DocumentationUrl.Should().Be(FoundryLocalResources.InstallDocumentationUrl);
        exception.Which.Message.Should().Contain("CLI is not installed");
    }

    [Fact]
    public async Task TranslateAsync_NonLoopbackConnectionFailureDoesNotExposeFoundryRecoveryAction()
    {
        var service = new FoundryLocalService(new HttpClient(new AlwaysRefusingHandler()));
        service.Configure("http://192.0.2.10:5273/v1", "test-model");

        var act = async () => await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.NetworkError);
        exception.Which.RecoveryAction.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_ExplainsWhenFoundryCliIsMissing()
    {
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            new MissingCliResolver());
        service.Configure(endpoint: "", model: "test-model");

        var act = async () => await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.Message.Should().Contain("CLI is not installed");
        exception.Which.Message.Should().Contain(FoundryLocalResources.InstallDocumentationUrl);
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        exception.Which.RecoveryAction.Should().Be(FoundryLocalResources.InstallRecoveryAction);
        exception.Which.DocumentationUrl.Should().Be(FoundryLocalResources.InstallDocumentationUrl);
    }

    [Fact]
    public async Task StartServiceAsync_TimesOut_WhenFoundryServiceStartDoesNotExit()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "foundry-timeout-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var scriptPath = Path.Combine(tempDirectory, "foundry.cmd");
        File.WriteAllText(
            scriptPath,
            """
            @echo off
            if "%1"=="service" if "%2"=="start" goto loop
            echo polling
            exit /b 0
            :loop
            goto loop
            """,
            Encoding.ASCII);

        try
        {
            var resolver = new FoundryLocalCliEndpointResolver(
                scriptPath,
                statusCommandTimeout: TimeSpan.FromSeconds(2),
                startCommandTimeout: TimeSpan.FromMilliseconds(100));

            var act = async () => await resolver.StartServiceAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<FoundryLocalCliCommandException>();
            exception.Which.ExitCode.Should().Be(-2);
            exception.Which.Message.Should().Contain("Timed out");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartServiceAsync_Returns_WhenStatusReportsRunningBeforeStartProcessExits()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "foundry-start-running-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var scriptPath = Path.Combine(tempDirectory, "foundry.cmd");
        File.WriteAllText(
            scriptPath,
            """
            @echo off
            if "%1"=="service" if "%2"=="start" (
                ping -n 6 127.0.0.1 >nul
                exit /b 0
            )
            if "%1"=="service" if "%2"=="status" (
                echo Model management service is running on http://127.0.0.1:5273/openai/status
                exit /b 0
            )
            exit /b 1
            """,
            Encoding.ASCII);

        try
        {
            var resolver = new FoundryLocalCliEndpointResolver(
                scriptPath,
                startCommandTimeout: TimeSpan.FromSeconds(5));
            var stopwatch = Stopwatch.StartNew();

            await resolver.StartServiceAsync(CancellationToken.None);

            stopwatch.Stop();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_StartsServiceAndLoadsConfiguredModel_WhenRuntimeIsNotRunning()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status");
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        runtime.StartCalls.Should().Be(1);
        runtime.LoadedModels.Should().Equal("test-model");
        runtime.GetStatusCalls.Should().BeGreaterThanOrEqualTo(2);
        service.Endpoint.Should().Be("http://127.0.0.1:5273/v1/chat/completions");
    }

    [Fact]
    public async Task PrepareAsync_StartsServiceAndRefreshesEndpoint_WhenConfiguredLoopbackEndpointIsNotRunning()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status");
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "http://127.0.0.1:9890/v1", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        runtime.StartCalls.Should().Be(1);
        runtime.LoadedModels.Should().Equal("test-model");
        service.Endpoint.Should().Be("http://127.0.0.1:5273/v1/chat/completions");
    }

    [Fact]
    public async Task PrepareAsync_DoesNotUseRuntimeLifecycle_WhenConfiguredEndpointIsNotLoopback()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status");
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "http://192.0.2.10:5273/v1", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        runtime.GetStatusCalls.Should().Be(0);
        runtime.StartCalls.Should().Be(0);
        runtime.LoadedModels.Should().BeEmpty();
        service.Endpoint.Should().Be("http://192.0.2.10:5273/v1/chat/completions");
    }

    [Fact]
    public async Task PrepareAsync_WaitsForEndpointReadinessAfterModelLoad()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status");
        var handler = new DelayedModelsEndpointHandler(failuresBeforeReady: 1);
        var service = new FoundryLocalService(
            new HttpClient(handler),
            runtime);
        service.Configure(endpoint: "", model: "qwen2.5-0.5b");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        handler.ModelsGetCalls.Should().Be(2);
        runtime.LoadedModels.Should().Equal("qwen2.5-0.5b");
        service.Model.Should().Be("qwen2.5-0.5b-instruct-openvino-npu:4");
    }

    [Fact]
    public async Task PrepareAsync_RetriesModelsEndpointNonSuccessDuringRuntimeReadiness()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status");
        var handler = new NonSuccessThenReadyModelsEndpointHandler(HttpStatusCode.ServiceUnavailable);
        var service = new FoundryLocalService(new HttpClient(handler), runtime);
        service.Configure(endpoint: "", model: "qwen2.5-0.5b");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        handler.ModelsGetCalls.Should().Be(2);
        service.Model.Should().Be("qwen2.5-0.5b-instruct-openvino-npu:4");
    }

    [Fact]
    public async Task PrepareAsync_PublishesStartingLoadingAndReadyStatuses()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status");
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "", model: "test-model");
        var resourceKeys = new List<string>();
        service.StatusChanged += (_, status) => resourceKeys.Add(status.ResourceKey);

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        resourceKeys.Should().ContainInOrder(
            "FoundryLocal_Status_Starting",
            "FoundryLocal_Status_LoadingModel",
            "FoundryLocal_Status_Ready");
    }

    [Fact]
    public async Task PrepareAsync_ReportsFailure_WhenRuntimeIsRunningButNoEndpointCanBeResolved()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.Running,
                DetailMessage: "Foundry Local service is running."));
        var service = new FoundryLocalService(new HttpClient(new CapturingHandler()), runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("FoundryLocal_Status_NotRunning");
        status.DetailMessage.Should().Contain("service is not running");
        runtime.StartCalls.Should().Be(0);
        runtime.LoadedModels.Should().Equal("test-model");
    }

    [Fact]
    public async Task PrepareAsync_ReportsNotInstalled_WhenCliIsMissing()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotInstalled));
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.NotCompatible);
        status.ResourceKey.Should().Be("FoundryLocal_Status_NotInstalled");
        runtime.StartCalls.Should().Be(0);
        runtime.LoadedModels.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ReportsFailure_WhenModelLoadFails()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status")
        {
            LoadException = new FoundryLocalCliCommandException("model load test-model", 1, "load failed")
        };
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("FoundryLocal_Status_StartFailed");
        runtime.StartCalls.Should().Be(1);
        runtime.LoadedModels.Should().Equal("test-model");
    }

    [Fact]
    public async Task PrepareAsync_ReportsFailure_WhenServiceStartFails()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning),
            "http://127.0.0.1:5273/openai/status")
        {
            StartException = new FoundryLocalCliCommandException("service start", 1, "start failed")
        };
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("FoundryLocal_Status_StartFailed");
        runtime.StartCalls.Should().Be(1);
        runtime.LoadedModels.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckRuntimeStatusAsync_MapsNotRunningToNeedsPreparation()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning));
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.CheckRuntimeStatusAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.NeedsPreparation);
        status.ResourceKey.Should().Be("FoundryLocal_Status_NotRunning");
    }

    [Fact]
    public async Task CheckRuntimeStatusAsync_DoesNotUseRuntime_WhenConfiguredEndpointIsNotLoopback()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotInstalled));
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "http://192.0.2.10:5273/v1", model: "test-model");

        var status = await service.CheckRuntimeStatusAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        runtime.GetStatusCalls.Should().Be(0);
    }

    [Fact]
    public async Task CheckRuntimeStatusAsync_MapsConfiguredLoopbackEndpointNotRunningToNeedsPreparation()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning));
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            runtime);
        service.Configure(endpoint: "http://127.0.0.1:9890/v1", model: "test-model");

        var status = await service.CheckRuntimeStatusAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.NeedsPreparation);
        status.ResourceKey.Should().Be("FoundryLocal_Status_NotRunning");
    }

    [Fact]
    public async Task CheckRuntimeStatusAsync_MapsRunningWithoutEndpointToFailed()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.Running,
                DetailMessage: "Foundry Local service is running."));
        var service = new FoundryLocalService(new HttpClient(new CapturingHandler()), runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.CheckRuntimeStatusAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("FoundryLocal_Status_StartFailed");
        status.DetailMessage.Should().Contain("did not report a local endpoint");
    }

    [Fact]
    public async Task CheckRuntimeStatusAsync_UsesResolverFallbackWhenRunningStatusHasNoEndpoint()
    {
        var runtime = new FakeRuntimeController(
            new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.Running,
                DetailMessage: "Foundry Local service is running."))
        {
            ResolvedEndpoint = "http://127.0.0.1:5273/openai/status"
        };
        var service = new FoundryLocalService(new HttpClient(new CapturingHandler()), runtime);
        service.Configure(endpoint: "", model: "test-model");

        var status = await service.CheckRuntimeStatusAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        status.ResourceKey.Should().Be("FoundryLocal_Status_Ready");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            const string sse = """
                data: {"choices":[{"delta":{"content":"你"}}]}
                data: {"choices":[{"delta":{"content":"好"}}]}
                data: [DONE]

                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class EndpointRefreshHandler : HttpMessageHandler
    {
        public List<string> PostUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            PostUris.Add(uri);
            if (request.RequestUri?.Port == 3968)
            {
                throw new HttpRequestException("connection refused");
            }

            const string sse = """
                data: {"choices":[{"delta":{"content":"你好"}}]}
                data: [DONE]

                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class DelayedModelsEndpointHandler : HttpMessageHandler
    {
        private readonly int _failuresBeforeReady;

        public DelayedModelsEndpointHandler(int failuresBeforeReady)
        {
            _failuresBeforeReady = failuresBeforeReady;
        }

        public int ModelsGetCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                ModelsGetCalls++;
                if (ModelsGetCalls <= _failuresBeforeReady)
                {
                    throw new HttpRequestException("connection refused");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"data":[{"id":"qwen2.5-0.5b-instruct-openvino-npu:4"}],"object":"list"}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class NonSuccessThenReadyModelsEndpointHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _firstStatusCode;

        public NonSuccessThenReadyModelsEndpointHandler(HttpStatusCode firstStatusCode)
        {
            _firstStatusCode = firstStatusCode;
        }

        public int ModelsGetCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                ModelsGetCalls++;
                if (ModelsGetCalls == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(_firstStatusCode)
                    {
                        Content = new StringContent("not ready", Encoding.UTF8, "text/plain"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"data":[{"id":"qwen2.5-0.5b-instruct-openvino-npu:4"}],"object":"list"}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class AlwaysRefusingHandler : HttpMessageHandler
    {
        public List<string> PostUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostUris.Add(request.RequestUri?.ToString() ?? "");
            }

            throw new HttpRequestException("connection refused");
        }
    }

    private sealed class ModelResolvingHandler : HttpMessageHandler
    {
        public string? PostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"data":[
                          {"id":"qwen2.5-0.5b-instruct-generic-cpu:4"},
                          {"id":"qwen2.5-0.5b-instruct-openvino-npu:4"}
                        ],"object":"list"}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            PostBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            const string sse = """
                data: {"choices":[{"delta":{"content":"你好"}}]}
                data: [DONE]

                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class MissingCliResolver : IFoundryLocalEndpointResolver
    {
        public Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
        {
            throw new FoundryLocalCliNotFoundException(new Win32Exception());
        }
    }

    private sealed class StaticEndpointResolver : IFoundryLocalEndpointResolver
    {
        private readonly string _endpoint;

        public StaticEndpointResolver(string endpoint)
        {
            _endpoint = endpoint;
        }

        public Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(_endpoint);
        }
    }

    private sealed class FakeRuntimeController : IFoundryLocalRuntimeController
    {
        private readonly string? _readyEndpoint;
        private FoundryLocalRuntimeStatus _status;

        public FakeRuntimeController(
            FoundryLocalRuntimeStatus initialStatus,
            string? readyEndpoint = null)
        {
            _status = initialStatus;
            _readyEndpoint = readyEndpoint;
        }

        public int GetStatusCalls { get; private set; }

        public int StartCalls { get; private set; }

        public List<string> LoadedModels { get; } = [];

        public Exception? LoadException { get; init; }

        public Exception? StartException { get; init; }

        public string? ResolvedEndpoint { get; init; }

        public Task<FoundryLocalRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            GetStatusCalls++;
            return Task.FromResult(_status);
        }

        public Task StartServiceAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            if (StartException is not null)
            {
                throw StartException;
            }

            _status = new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.Running,
                _readyEndpoint);
            return Task.CompletedTask;
        }

        public Task LoadModelAsync(string model, CancellationToken cancellationToken)
        {
            LoadedModels.Add(model);
            if (LoadException is not null)
            {
                throw LoadException;
            }

            _status = new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.Running,
                _readyEndpoint);
            return Task.CompletedTask;
        }

        public Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ResolvedEndpoint ?? _status.Endpoint);
        }
    }
}
