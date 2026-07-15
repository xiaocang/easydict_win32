using System.Runtime.CompilerServices;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using FluentAssertions;
using Microsoft.Windows.AI;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Unit tests for the Phi Silica translation provider.
/// Uses a fake <see cref="IWindowsLanguageModelClient"/> so the suite can run
/// on any machine — Copilot+ NPU is not required.
/// </summary>
public class PhiSilicaTranslationServiceTests
{
    [Fact]
    public void ServiceId_IsWindowsLocalAi()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.ServiceId.Should().Be("windows-local-ai");
    }

    [Fact]
    public void DisplayName_IsPhiSilica()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.DisplayName.Should().Be("Phi Silica");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void PreparationHint_ServiceDisabledHResult_PointsToUpdateServices()
    {
        var hint = WindowsLanguageModelClient.GetPreparationHintForHResult(unchecked((int)0x80070422));

        hint.Should().Contain("Windows update");
        hint.Should().Contain("Delivery Optimization");
    }

    [Fact]
    public void PreparationHint_PackageResourceInUseHResult_PointsToRestart()
    {
        var hint = WindowsLanguageModelClient.GetPreparationHintForHResult(unchecked((int)0x80073D02));

        hint.Should().Contain("package");
        hint.Should().Contain("restart");
    }

    [Theory]
    [InlineData("10.0.26200.0", null, true)]
    [InlineData("10.0.26200.0", 7308, true)]
    [InlineData("10.0.26200.0", 7309, false)]
    [InlineData("10.0.26200.7309", null, false)]
    [InlineData("10.0.26100.9999", null, true)]
    public void BaselineDiagnostics_UsesWindows11_25H2_26200_7309_AsMinimum(
        string osBuild,
        int? ubr,
        bool expectedBelowMinimum)
    {
        WindowsAIBaselineDiagnostics.IsBelowMinimumOsBaseline(osBuild, ubr)
            .Should()
            .Be(expectedBelowMinimum);
    }

    [Fact]
    public void GetReadyState_BelowMinimumOsBaseline_DoesNotProbeLanguageModel()
    {
        var probeCount = 0;
        var client = new WindowsLanguageModelClient(
            () => new WindowsLanguageModelClient.WindowsBuildInfo("19045", 0),
            () =>
            {
                probeCount++;
                throw new InvalidOperationException("LanguageModel must not be probed.");
            });

        client.GetReadyState().Should().Be(WindowsAIReadyState.UnsupportedWindowsAIBaseline);
        probeCount.Should().Be(0);
    }

    [Fact]
    public void GetHealthFingerprint_BelowMinimumOsBaseline_DoesNotProbeLanguageModel()
    {
        var probeCount = 0;
        var client = new WindowsLanguageModelClient(
            () => new WindowsLanguageModelClient.WindowsBuildInfo("19045", 0),
            () =>
            {
                probeCount++;
                throw new InvalidOperationException("LanguageModel must not be probed.");
            });

        var fingerprint = client.GetHealthFingerprint();

        fingerprint.WindowsAppSdkVersion.Should().Be("not-probed");
        fingerprint.ComponentMarker.Should().Be("Microsoft.Windows.AI.Text; readyState=not-probed");
        fingerprint.PhiSilicaAiComponentsPresent.Should().BeNull();
        probeCount.Should().Be(0);
    }

    [Fact]
    public void GetReadyState_MissingRegistryBuild_StillProbesLanguageModel()
    {
        var probeCount = 0;
        var client = new WindowsLanguageModelClient(
            () => new WindowsLanguageModelClient.WindowsBuildInfo(null, null),
            () =>
            {
                probeCount++;
                return AIFeatureReadyState.NotReady;
            });

        client.GetReadyState().Should().Be(WindowsAIReadyState.NotReady);
        probeCount.Should().Be(1);
    }

    [Fact]
    public void GetHealthFingerprint_MissingRegistryBuild_StillProbesLanguageModel()
    {
        var probeCount = 0;
        var client = new WindowsLanguageModelClient(
            () => new WindowsLanguageModelClient.WindowsBuildInfo(null, null),
            () =>
            {
                probeCount++;
                return AIFeatureReadyState.NotReady;
            });

        var fingerprint = client.GetHealthFingerprint();

        fingerprint.WindowsAppSdkVersion.Should().NotBe("not-probed");
        fingerprint.ComponentMarker.Should().EndWith("readyState=NotReady");
        probeCount.Should().Be(1);
    }

    [Fact]
    public void Constructor_NullBuildInfoProvider_Throws()
    {
        var action = () => new WindowsLanguageModelClient(
            null!,
            () => AIFeatureReadyState.NotReady);

        action.Should()
            .Throw<ArgumentNullException>()
            .WithParameterName("buildInfoProvider");
    }

    [Fact]
    public void Constructor_NullReadyStateProvider_Throws()
    {
        var action = () => new WindowsLanguageModelClient(
            () => new WindowsLanguageModelClient.WindowsBuildInfo(null, null),
            null!);

        action.Should()
            .Throw<ArgumentNullException>()
            .WithParameterName("readyStateProvider");
    }

    [Fact]
    public void MapReadyStateToStatus_UnsupportedBaseline_UsesDedicatedResource()
    {
        var status = PhiSilicaTranslationService.MapReadyStateToStatus(
            WindowsAIReadyState.UnsupportedWindowsAIBaseline);

        status.State.Should().Be(LocalModelState.NotCompatible);
        status.ResourceKey.Should().Be("WindowsLocalAI_Status_UnsupportedWindowsAIBaseline");
    }

    [Fact]
    public void PreparationFailure_UnactivatedAndComponentsMissing_UsesUnsupportedBaseline()
    {
        var fingerprint = new WindowsAIHealthFingerprint(
            OsBuild: "10.0.26200.7309",
            Ubr: null,
            WindowsAppSdkVersion: "2.0.1",
            ProcessArchitecture: "Arm64",
            BackendName: "PhiSilica",
            ComponentMarker: "Fake",
            WindowsActivated: false,
            PhiSilicaAiComponentsPresent: false);

        var status = PhiSilicaTranslationService.CreatePreparationFailureStatus(
            "Windows is not activated and Phi Silica AI Components are missing or incomplete.",
            fingerprint);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("WindowsLocalAI_Status_UnsupportedWindowsAIBaseline");
    }

    [Fact]
    public void RuntimeExceptionMessage_UnspecifiedFailure_IncludesHResultAndHint()
    {
        var exception = new System.Runtime.InteropServices.COMException(
            "Unspecified error\r\n\r\nUnspecified error\r\n",
            unchecked((int)0x80004005));

        var message = WindowsLanguageModelClient.CreateRuntimeExceptionMessage("generate", exception);

        message.Should().Contain("operation=generate");
        message.Should().Contain("hResult=0x80004005");
        message.Should().Contain("message=Unspecified error;");
        message.Should().NotContain("Unspecified error Unspecified error");
        message.Should().Contain("model session or first inference failed");
        message.Should().Contain("Foundry Local");
    }

    [Fact]
    public void RuntimeExceptionMessage_ModelSessionPsResultFailure_PointsToSessionInitialization()
    {
        var exception = new System.Runtime.InteropServices.COMException(
            "Unspecified error\r\nModel session initialization or inference failed: Unknown PsResult: -967 CallContext:[\\LoadModelAndInitializeSession]",
            unchecked((int)0x80004005));

        var message = WindowsLanguageModelClient.CreateRuntimeExceptionMessage("stream", exception);

        message.Should().Contain("operation=stream");
        message.Should().Contain("hResult=0x80004005");
        message.Should().Contain("Unknown PsResult: -967");
        message.Should().Contain("initializing the Phi Silica model session");
        message.Should().Contain("Foundry Local");
    }

    [Fact]
    public void FormatFullWindowsBuild_UsesRegistryBuildAndUbr()
    {
        WindowsLanguageModelClient.FormatFullWindowsBuild(
                "26200",
                7309,
                new Version(10, 0, 26200, 0))
            .Should().Be("10.0.26200.7309");
    }

    [Fact]
    public void FormatFullWindowsBuild_FallsBackToEnvironmentVersionWhenRegistryBuildMissing()
    {
        WindowsLanguageModelClient.FormatFullWindowsBuild(
                null,
                null,
                new Version(10, 0, 26100, 1))
            .Should().Be("10.0.26100.1");
    }

    [Fact]
    public void RuntimeFailureDetail_AppendsFingerprintWithoutRepeatingExistingDiagnostics()
    {
        var fingerprint = new WindowsAIHealthFingerprint(
            OsBuild: "10.0.26200.7309",
            Ubr: 7309,
            WindowsAppSdkVersion: "2.0.184.54419",
            ProcessArchitecture: "Arm64",
            BackendName: "PhiSilica",
            ComponentMarker: "Microsoft.Windows.AI.Text.Projection; readyState=Ready",
            WindowsActivated: null,
            PhiSilicaAiComponentsPresent: true);

        var detail = PhiSilicaTranslationService.FormatRuntimeFailureDetail(
            "Windows AI runtime failed while running Phi Silica: operation=warmup; hResult=0x80004005; osBuild=10.0.26200.7309; processArch=Arm64",
            fingerprint);

        detail.Should().Contain("operation=warmup");
        detail.Should().Contain("ubr=7309");
        detail.Should().Contain("windowsAppSdk=2.0.184.54419");
        detail.Should().Contain("component=Microsoft.Windows.AI.Text.Projection; readyState=Ready");
        detail.Should().Contain("phiSilicaAiComponentsPresent=True");
        CountOccurrences(detail, "osBuild=").Should().Be(1);
        CountOccurrences(detail, "processArch=").Should().Be(1);
    }

    [Fact]
    public void IsConfigured_IsTrue()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_TargetAuto_ReturnsFalse()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.SupportsLanguagePair(Language.English, Language.Auto).Should().BeFalse();
    }

    [Fact]
    public void SupportsLanguagePair_TargetReal_ReturnsTrue()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        service.SupportsLanguagePair(Language.Auto, Language.SimplifiedChinese).Should().BeTrue();
    }

    [Fact]
    public void GetStatus_ReadyPackageBeforeWarmup_IsNotReadyYet()
    {
        var service = new PhiSilicaTranslationService(new FakeClient
        {
            ReadyState = WindowsAIReadyState.Ready,
        });

        var status = service.GetStatus();

        status.State.Should().Be(LocalModelState.NeedsPreparation);
        status.ResourceKey.Should().Be("WindowsLocalAI_Status_WarmupRequired");
    }

    [Fact]
    public async Task PrepareAsync_ReadyPackage_RunsWarmupBeforeReportingReady()
    {
        var client = new FakeClient
        {
            ReadyState = WindowsAIReadyState.Ready,
        };
        var service = new PhiSilicaTranslationService(client);

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Ready);
        client.WarmUpCallCount.Should().Be(1);
        service.GetStatus().State.Should().Be(LocalModelState.Ready);
    }

    [Fact]
    public async Task PrepareAsync_WarmupFailure_MarksRuntimeUnhealthy()
    {
        var service = new PhiSilicaTranslationService(new FakeClient
        {
            ReadyState = WindowsAIReadyState.Ready,
            WarmUpFailure = new WindowsLanguageModelException(
                WindowsAIResponseStatus.Error,
                "Windows AI runtime failed while running Phi Silica: operation=warmup; hResult=0x80004005"),
        });

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("WindowsLocalAI_Status_RuntimeUnhealthy");
        service.GetStatus().State.Should().Be(LocalModelState.Failed);
        service.GetStatus().ResourceKey.Should().Be("WindowsLocalAI_Status_RuntimeUnhealthy");
    }

    [Fact]
    public async Task PrepareAsync_AfterWarmupFailure_AllowsManualRetry()
    {
        var client = new FakeClient
        {
            ReadyState = WindowsAIReadyState.Ready,
            WarmUpFailure = new WindowsLanguageModelException(
                WindowsAIResponseStatus.Error,
                "first warmup failed"),
        };
        var service = new PhiSilicaTranslationService(client);

        var failed = await service.PrepareAsync(CancellationToken.None);
        client.WarmUpFailure = null;
        var recovered = await service.PrepareAsync(CancellationToken.None);

        failed.State.Should().Be(LocalModelState.Failed);
        recovered.State.Should().Be(LocalModelState.Ready);
        client.WarmUpCallCount.Should().Be(2);
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_ThrowsInvalidResponse()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        var request = new TranslationRequest { Text = "   ", ToLanguage = Language.English };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
        ex.Which.ServiceId.Should().Be("windows-local-ai");
    }

    [Fact]
    public async Task TranslateAsync_TargetAuto_ThrowsUnsupportedLanguage()
    {
        var service = new PhiSilicaTranslationService(new FakeClient());
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.Auto };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.UnsupportedLanguage);
    }

    [Fact]
    public async Task TranslateAsync_ModelNeedsPreparation_DoesNotPrepareImplicitly()
    {
        var client = new FakeClient
        {
            ReadyState = WindowsAIReadyState.NotReady,
        };
        var service = new PhiSilicaTranslationService(client);
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.SimplifiedChinese };

        var act = async () => await service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.LocalModelNeedsPreparation);
        client.EnsureReadyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PrepareAsync_NotReadyAfterAttempt_ReturnsFailedStatus()
    {
        var client = new FakeClient
        {
            ReadyState = WindowsAIReadyState.NotReady,
        };
        var service = new PhiSilicaTranslationService(client);

        var status = await service.PrepareAsync(CancellationToken.None);

        status.State.Should().Be(LocalModelState.Failed);
        status.ResourceKey.Should().Be("WindowsLocalAI_Status_PrepareFailed");
        status.DetailMessage.Should().Contain("still not ready");
        client.EnsureReadyCallCount.Should().Be(1);
    }

    [Fact]
    public async Task TranslateAsync_ModelNotCompatible_ThrowsServiceUnavailable()
    {
        var client = new FakeClient
        {
            ReadyState = WindowsAIReadyState.NotCompatibleWithSystemHardware,
        };
        var service = new PhiSilicaTranslationService(client);
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
        var service = new PhiSilicaTranslationService(client);
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
        var service = new PhiSilicaTranslationService(client);
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
        var service = new PhiSilicaTranslationService(client);
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese,
        };

        var result = await service.TranslateAsync(request);

        result.TranslatedText.Should().Be("你好");
        result.OriginalText.Should().Be("Hello");
        result.ServiceName.Should().Be("Phi Silica");
        result.TargetLanguage.Should().Be(Language.SimplifiedChinese);
    }

    [Fact]
    public async Task TranslateStreamAsync_YieldsChunksInOrder()
    {
        var client = new FakeClient
        {
            StreamChunks = new[] { "你", "好", "，", "世界" },
        };
        var service = new PhiSilicaTranslationService(client);
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
        var service = new PhiSilicaTranslationService(client);
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

        var prompt = PhiSilicaTranslationService.BuildTranslationPrompt(request);

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

        var prompt = PhiSilicaTranslationService.BuildTranslationPrompt(request);

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

        var prompt = PhiSilicaTranslationService.BuildTranslationPrompt(request);

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

        var prompt = PhiSilicaTranslationService.BuildTranslationPrompt(request);

        prompt.Should().NotContain("Additional user instruction");
    }

    [Fact]
    public void BuildGrammarCorrectionPrompt_UsesSharedExplanationFormat()
    {
        var request = new GrammarCorrectionRequest
        {
            Text = "He go to school.",
            Language = Language.English,
            IncludeExplanations = true,
        };

        var prompt = PhiSilicaTranslationService.BuildGrammarCorrectionPrompt(request);

        prompt.Should().Contain("First output the fully corrected text");
        prompt.Should().Contain("\"---\"");
        prompt.Should().Contain("NEVER put \"---\" before the corrected text");
        prompt.Should().Contain("He go to school.");
        prompt.Should().NotContain("[CORRECTED]");
        prompt.Should().NotContain("[EXPLANATION]");
    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private sealed class FakeClient : IWindowsLanguageModelClient
    {
        public WindowsAIReadyState ReadyState { get; set; } = WindowsAIReadyState.Ready;

        public WindowsAIResponse GenerateResponse { get; set; } =
            new(WindowsAIResponseStatus.Complete, "ok");

        public IReadOnlyList<string> StreamChunks { get; set; } = Array.Empty<string>();

        public Exception? WarmUpFailure { get; set; }

        public string? LastPrompt { get; private set; }
        public int EnsureReadyCallCount { get; private set; }
        public int WarmUpCallCount { get; private set; }
        public string OsBuild { get; set; } = "10.0.26200.7309";
        public int? Ubr { get; set; }
        public bool? WindowsActivated { get; set; } = true;
        public bool? PhiSilicaAiComponentsPresent { get; set; } = true;

        public WindowsAIReadyState GetReadyState() => ReadyState;

        public WindowsAIHealthFingerprint GetHealthFingerprint() => new(
            OsBuild: OsBuild,
            Ubr: Ubr,
            WindowsAppSdkVersion: "2.0.1",
            ProcessArchitecture: "Arm64",
            BackendName: "PhiSilica",
            ComponentMarker: "Fake",
            WindowsActivated: WindowsActivated,
            PhiSilicaAiComponentsPresent: PhiSilicaAiComponentsPresent);

        public Task<WindowsAIReadyState> EnsureReadyAsync(
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            EnsureReadyCallCount++;
            progress?.Report(100);
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

        public Task WarmUpAsync(
            string prompt,
            WindowsAIGenerationOptions options,
            CancellationToken cancellationToken)
        {
            WarmUpCallCount++;
            if (WarmUpFailure is not null)
            {
                throw WarmUpFailure;
            }

            return Task.CompletedTask;
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


