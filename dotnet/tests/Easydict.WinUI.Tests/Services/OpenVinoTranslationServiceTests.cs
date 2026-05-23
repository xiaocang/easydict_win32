using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using Easydict.OpenVINO.Inference;
using Easydict.OpenVINO.Models;
using Easydict.OpenVINO.Services;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Unit tests for the OpenVINO + NLLB-200 translation provider. Uses a fake
/// engine and tokenizer so the suite doesn't need the ~360 MB model bundle
/// or a working OpenVINO runtime.
/// </summary>
public class OpenVinoTranslationServiceTests : IDisposable
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string NllbInferenceEnginePath = Path.Combine(ProjectRoot, "src", "Easydict.OpenVINO", "Inference", "NllbInferenceEngine.cs");

    private readonly string _tempDir;
    private readonly ModelDownloadService _downloader;
    private readonly OpenVinoRuntimeDownloadService _runtimeDownloader;

    public OpenVinoTranslationServiceTests()
    {
        // Stand up a fake cache that IsModelInstalled() will accept — touch every
        // manifest file plus the completion sentinel.
        _tempDir = Path.Combine(Path.GetTempPath(), "EasydictOvTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var modelDir = Path.Combine(_tempDir, ModelManifest.CacheDirectoryName);
        Directory.CreateDirectory(modelDir);
        foreach (var f in ModelManifest.Files)
        {
            File.WriteAllText(Path.Combine(modelDir, f.LocalFileName), "stub");
        }
        File.WriteAllText(Path.Combine(modelDir, ModelManifest.CompletionSentinel), "stub");

        _downloader = NewDownloader(_tempDir);
        _runtimeDownloader = NewRuntimeDownloader(_tempDir);
        InstallRuntimeStub(_runtimeDownloader);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ServiceId_IsOpenVinoLocalAi()
    {
        using var svc = NewService();
        svc.ServiceId.Should().Be("openvino-local-ai");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        using var svc = NewService();
        svc.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void DefaultDevice_IsAuto()
    {
        using var svc = NewService();
        svc.Device.Should().Be(OpenVINODevice.Auto);
    }

    [Fact]
    public void OpenVinoDeviceMapping_AutoPrefersNpuBeforeGpuAndCpu()
    {
        OpenVINODevice.Auto.ToOpenVINOString().Should().Be("AUTO:NPU,GPU,CPU");
        OpenVINODevice.NPU.ToOpenVINOString().Should().Be("NPU");
        OpenVINODevice.GPU.ToOpenVINOString().Should().Be("GPU");
        OpenVINODevice.CPU.ToOpenVINOString().Should().Be("CPU");
    }

    [Fact]
    public void SentencePieceIdMapping_OffsetsContentIdsForNllbModelVocabulary()
    {
        NllbTokenizer.ToModelSentencePieceId(0).Should().Be(0);
        NllbTokenizer.ToModelSentencePieceId(1).Should().Be(1);
        NllbTokenizer.ToModelSentencePieceId(2).Should().Be(2);
        NllbTokenizer.ToModelSentencePieceId(3).Should().Be(4);
        NllbTokenizer.ToModelSentencePieceId(94123).Should().Be(94124);

        NllbTokenizer.ToTokenizerSentencePieceId(0).Should().Be(0);
        NllbTokenizer.ToTokenizerSentencePieceId(1).Should().Be(1);
        NllbTokenizer.ToTokenizerSentencePieceId(2).Should().Be(2);
        NllbTokenizer.ToTokenizerSentencePieceId(3).Should().Be(3);
        NllbTokenizer.ToTokenizerSentencePieceId(4).Should().Be(3);
        NllbTokenizer.ToTokenizerSentencePieceId(94124).Should().Be(94123);
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        using var svc = NewService();
        svc.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_TargetAuto_ReturnsFalse()
    {
        using var svc = NewService();
        svc.SupportsLanguagePair(Language.English, Language.Auto).Should().BeFalse();
    }

    [Fact]
    public void SupportsLanguagePair_TargetReal_ReturnsTrue()
    {
        using var svc = NewService();
        svc.SupportsLanguagePair(Language.Auto, Language.SimplifiedChinese).Should().BeTrue();
    }

    [Fact]
    public void GetStatus_WhenInstalled_ReturnsReady()
    {
        using var svc = NewService();
        var status = svc.GetStatus();
        status.State.Should().Be(LocalModelState.Ready);
        status.ResourceKey.Should().Be("OpenVINO_Status_Ready");
    }

    [Fact]
    public void GetStatus_WhenNotInstalled_ReturnsNeedsPreparation()
    {
        var freshTemp = Path.Combine(Path.GetTempPath(), "EasydictOvFresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(freshTemp);
            var downloader = NewDownloader(freshTemp);
            using var svc = new OpenVINOTranslationService(downloader);

            var status = svc.GetStatus();
            status.State.Should().Be(LocalModelState.NeedsPreparation);
            status.ResourceKey.Should().Be("OpenVINO_Status_NotDownloaded");
        }
        finally
        {
            try { Directory.Delete(freshTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_ThrowsInvalidResponse()
    {
        using var svc = NewService();
        var request = new TranslationRequest { Text = "  ", ToLanguage = Language.SimplifiedChinese };

        var act = async () => await svc.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task TranslateAsync_TargetAuto_ThrowsUnsupportedLanguage()
    {
        using var svc = NewService();
        var request = new TranslationRequest { Text = "Hello", ToLanguage = Language.Auto };

        var act = async () => await svc.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.UnsupportedLanguage);
    }

    [Fact]
    public async Task TranslateAsync_HappyPath_ConcatenatesDecodedTokens()
    {
        var tokenizer = new FakeTokenizer
        {
            DecodedPieces = { [100] = "你", [200] = "好", [300] = "，", [400] = "世界" },
        };
        var engine = new FakeEngine { TokenStream = new[] { 100, 200, 300, 400 } };

        using var svc = new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);
        var request = new TranslationRequest
        {
            Text = "Hello, world",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };

        var result = await svc.TranslateAsync(request);

        result.TranslatedText.Should().Be("你好，世界");
        result.OriginalText.Should().Be("Hello, world");
        result.TargetLanguage.Should().Be(Language.SimplifiedChinese);
        result.ServiceName.Should().Contain("OpenVINO");
    }

    [Fact]
    public async Task TranslateStreamAsync_YieldsEachDecodedTokenInOrder()
    {
        var tokenizer = new FakeTokenizer
        {
            DecodedPieces = { [100] = "Hello", [200] = " ", [300] = "world" },
        };
        var engine = new FakeEngine { TokenStream = new[] { 100, 200, 300 } };

        using var svc = new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);
        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English,
        };

        var collected = new List<string>();
        await foreach (var chunk in svc.TranslateStreamAsync(request))
        {
            collected.Add(chunk);
        }

        collected.Should().Equal("Hello", " ", "world");
    }

    [Fact]
    public async Task TranslateStreamAsync_DropsSpecialTokenChunks()
    {
        // FakeTokenizer returns null for ids it considers "special" — that should
        // not surface as an empty yield to the caller.
        var tokenizer = new FakeTokenizer
        {
            DecodedPieces = { [100] = "Hello", [999] = null!, [300] = "world" },
        };
        var engine = new FakeEngine { TokenStream = new[] { 100, 999, 300 } };

        using var svc = new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);
        var request = new TranslationRequest
        {
            Text = "test",
            ToLanguage = Language.English,
        };

        var collected = new List<string>();
        await foreach (var chunk in svc.TranslateStreamAsync(request))
        {
            collected.Add(chunk);
        }

        collected.Should().Equal("Hello", "world");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCumulativeDecodeToPreserveSentencePieceSpaces()
    {
        var tokenizer = new FakeTokenizer
        {
            FullDecodedText =
            {
                ["100"] = "-",
                ["100,200"] = "- How",
                ["100,200,300"] = "- How are",
                ["100,200,300,400"] = "- How are you?",
            },
        };
        var engine = new FakeEngine { TokenStream = new[] { 100, 200, 300, 400 } };

        using var svc = new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English,
        };

        var collected = new List<string>();
        await foreach (var chunk in svc.TranslateStreamAsync(request))
        {
            collected.Add(chunk);
        }

        collected.Should().Equal("-", " How", " are", " you?");
    }

    [Fact]
    public void GetStreamingDecodeDelta_ReturnsOnlyNewSuffix()
    {
        OpenVINOTranslationService.GetStreamingDecodeDelta("- How", "- How are")
            .Should()
            .Be(" are");
    }

    [Fact]
    public void OpenCompatibleSentencePieceModelStream_RewritesNllbNormalizerName()
    {
        var spmPath = Path.Combine(_tempDir, "sentencepiece-nmt-nfkc.model");
        File.WriteAllBytes(spmPath, Encoding.UTF8.GetBytes("prefix nmt_nfkc suffix"));

        using var stream = NllbTokenizer.OpenCompatibleSentencePieceModelStream(spmPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        reader.ReadToEnd().Should().Be("prefix identity suffix");
    }

    [Fact]
    public void TryRewriteUnsupportedNormalizerName_ReturnsFalseWhenNotNeeded()
    {
        var bytes = Encoding.UTF8.GetBytes("prefix identity suffix");

        NllbTokenizer.TryRewriteUnsupportedNormalizerName(bytes).Should().BeFalse();
        Encoding.UTF8.GetString(bytes).Should().Be("prefix identity suffix");
    }

    [Fact]
    public void NormalizeInputForNllbTokenizer_AppliesNfkc()
    {
        NllbTokenizer.NormalizeInputForNllbTokenizer("ＡＢＣ１２３")
            .Should()
            .Be("ABC123");
    }

    [Fact]
    public void IsOpenVinoRuntimeFailure_DetectsOutputNameMismatch()
    {
        var ex = new InvalidOperationException(
            "[OpenVINO-EP] Output names mismatch between OpenVINO and ONNX");

        NllbInferenceEngine.IsOpenVinoRuntimeFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void RecordOpenVinoEncoderRuntimeFailure_DisablesOnlyReportedDevice()
    {
        NllbInferenceEngine.ResetOpenVinoEncoderRuntimeFailuresForTests();
        try
        {
            var ex = new InvalidOperationException(
                "OpenVINOExecutionProvider failed: Output names mismatch between OpenVINO and ONNX");

            NllbInferenceEngine.RecordOpenVinoEncoderRuntimeFailure(OpenVINODevice.Auto, ex);

            NllbInferenceEngine.IsOpenVinoEncoderRuntimeDisabled(OpenVINODevice.Auto).Should().BeTrue();
            NllbInferenceEngine.IsOpenVinoEncoderRuntimeDisabled(OpenVINODevice.NPU).Should().BeFalse();
            NllbInferenceEngine.IsOpenVinoEncoderRuntimeDisabled(OpenVINODevice.CPU).Should().BeFalse();
        }
        finally
        {
            NllbInferenceEngine.ResetOpenVinoEncoderRuntimeFailuresForTests();
        }
    }

    [Fact]
    public void RuntimeFallback_DisposesPrimaryOpenVinoEncoderSession()
    {
        var source = File.ReadAllText(NllbInferenceEnginePath);

        source.Should().Contain("private void DisposePrimaryEncoderSession()",
            "the failed OpenVINO encoder session owns a large native heap and must have an explicit release path");
        source.Should().MatchRegex(
            @"RecordOpenVinoEncoderRuntimeFailure\(_encoderDevice, ex\);\s*_useEncoderCpuFallback = true;\s*DisposePrimaryEncoderSession\(\);",
            "runtime fallback should release the failed OpenVINO encoder before continuing on the CPU encoder");
    }

    [Fact]
    public void Configure_DeviceChangeDisposesIdleCachedEngine()
    {
        var tokenizer = new FakeTokenizer();
        var engine = new FakeEngine();
        using var svc = new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);

        svc.Configure(OpenVINODevice.NPU);

        engine.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Configure_DeviceChangeRetiresActiveEngineUntilStreamCompletes()
    {
        var tokenizer = new FakeTokenizer
        {
            DecodedPieces = { [100] = "Hello" },
        };
        var engine = new FakeEngine { TokenStream = new[] { 100 } };
        using var svc = new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English,
        };

        var enumerator = svc.TranslateStreamAsync(request).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync()).Should().BeTrue();

            svc.Configure(OpenVINODevice.NPU);

            engine.DisposeCount.Should().Be(0);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        engine.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeDownload_HashMismatchFailsWithoutCompletionSentinel()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var cacheRoot = Path.Combine(Path.GetTempPath(), "EasydictOvRuntimeBad-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not the expected nupkg")),
            }));
            using var service = new OpenVinoRuntimeDownloadService(new HttpClient(handler), cacheRoot);

            var act = () => service.DownloadAsync(null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*SHA-256 mismatch*");
            File.Exists(Path.Combine(
                service.NativeDirectory,
                OpenVinoRuntimeDownloadService.CompletionSentinel)).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(cacheRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RuntimeDownload_SkipsNetworkWhenRuntimeAlreadyInstalled()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var cacheRoot = Path.Combine(Path.GetTempPath(), "EasydictOvRuntimeReady-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new RecordingHttpMessageHandler((_, _) =>
                throw new InvalidOperationException("Network should not be used when runtime is installed."));
            using var service = new OpenVinoRuntimeDownloadService(new HttpClient(handler), cacheRoot);
            InstallRuntimeStub(service);

            await service.DownloadAsync(null, CancellationToken.None);

            service.IsRuntimeInstalled().Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(cacheRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureNativeDirectoryOnPath_DoesNotModifyPathUnlessOpenVinoEpIsExplicitlyEnabled()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalFlag = Environment.GetEnvironmentVariable(OpenVinoRuntimeDownloadService.EnableOpenVinoEpEnvironmentVariable);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "EasydictOvRuntimePath-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(OpenVinoRuntimeDownloadService.EnableOpenVinoEpEnvironmentVariable, null);
            using var service = NewRuntimeDownloader(cacheRoot);

            service.EnsureNativeDirectoryOnPath();

            Environment.GetEnvironmentVariable("PATH").Should().Be(originalPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable(OpenVinoRuntimeDownloadService.EnableOpenVinoEpEnvironmentVariable, originalFlag);
            try { Directory.Delete(cacheRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureNativeDirectoryOnPath_PrependsNativeDirectoryWhenOpenVinoEpIsExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalFlag = Environment.GetEnvironmentVariable(OpenVinoRuntimeDownloadService.EnableOpenVinoEpEnvironmentVariable);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "EasydictOvRuntimePath-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(OpenVinoRuntimeDownloadService.EnableOpenVinoEpEnvironmentVariable, "1");
            using var service = NewRuntimeDownloader(cacheRoot);

            service.EnsureNativeDirectoryOnPath();

            Environment.GetEnvironmentVariable("PATH").Should().StartWith(service.NativeDirectory + Path.PathSeparator);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable(OpenVinoRuntimeDownloadService.EnableOpenVinoEpEnvironmentVariable, originalFlag);
            try { Directory.Delete(cacheRoot, recursive: true); } catch { }
        }
    }

    // ── Fakes ───────────────────────────────────────────────────────────

    private OpenVINOTranslationService NewService()
    {
        var tokenizer = new FakeTokenizer();
        var engine = new FakeEngine();
        return new OpenVINOTranslationService(_downloader, _runtimeDownloader, tokenizer, engine);
    }

    private static ModelDownloadService NewDownloader(string cacheRoot)
    {
        // We never actually hit the network in tests; pass a dummy HttpClient.
        return new ModelDownloadService(new HttpClient(), cacheRoot);
    }

    private static OpenVinoRuntimeDownloadService NewRuntimeDownloader(string cacheRoot)
    {
        return new OpenVinoRuntimeDownloadService(new HttpClient(), cacheRoot);
    }

    private static void InstallRuntimeStub(OpenVinoRuntimeDownloadService runtimeDownloader)
    {
        Directory.CreateDirectory(runtimeDownloader.NativeDirectory);
        foreach (var f in OpenVinoRuntimeManifest.NativeFiles)
        {
            File.WriteAllText(Path.Combine(runtimeDownloader.NativeDirectory, f), "stub");
        }
        File.WriteAllText(
            Path.Combine(runtimeDownloader.NativeDirectory, OpenVinoRuntimeDownloadService.CompletionSentinel),
            "stub");
    }

    private sealed class FakeTokenizer : INllbTokenizer
    {
        public Dictionary<int, string?> DecodedPieces { get; } = new();
        public Dictionary<string, string> FullDecodedText { get; } = new();

        public int BosTokenId => 0;
        public int PadTokenId => 1;
        public int EosTokenId => 2;
        public int UnkTokenId => 3;

        public IReadOnlyList<int> EncodeSource(string text, string srcFloresCode)
        {
            // Trivial: prefix with a deterministic "language token id" + EOS.
            return new[] { srcFloresCode.GetHashCode(), 42, EosTokenId };
        }

        public string Decode(IReadOnlyList<int> tokenIds)
        {
            var key = string.Join(",", tokenIds);
            if (FullDecodedText.TryGetValue(key, out var decoded))
            {
                return decoded;
            }

            return string.Concat(tokenIds.Select(id => DecodedPieces.TryGetValue(id, out var p) ? p : ""));
        }

        public string? DecodeSingle(int tokenId)
        {
            return DecodedPieces.TryGetValue(tokenId, out var piece) ? piece : null;
        }

        public int GetLanguageTokenId(string floresCode) => floresCode.GetHashCode();
    }

    private sealed class FakeEngine : INllbInferenceEngine, IDisposable
    {
        public IReadOnlyList<int> TokenStream { get; set; } = Array.Empty<int>();
        public int DisposeCount { get; private set; }

        public async IAsyncEnumerable<int> GenerateAsync(
            IReadOnlyList<int> encoderInputIds,
            int forcedBosTokenId,
            int maxNewTokens,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var t in TokenStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return t;
            }
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
