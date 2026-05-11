using System.Runtime.CompilerServices;
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
    private readonly string _tempDir;
    private readonly ModelDownloadService _downloader;

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

        using var svc = new OpenVINOTranslationService(_downloader, tokenizer, engine);
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

        using var svc = new OpenVINOTranslationService(_downloader, tokenizer, engine);
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

        using var svc = new OpenVINOTranslationService(_downloader, tokenizer, engine);
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

    // ── Fakes ───────────────────────────────────────────────────────────

    private OpenVINOTranslationService NewService()
    {
        var tokenizer = new FakeTokenizer();
        var engine = new FakeEngine();
        return new OpenVINOTranslationService(_downloader, tokenizer, engine);
    }

    private static ModelDownloadService NewDownloader(string cacheRoot)
    {
        // We never actually hit the network in tests; pass a dummy HttpClient.
        return new ModelDownloadService(new HttpClient(), cacheRoot);
    }

    private sealed class FakeTokenizer : INllbTokenizer
    {
        public Dictionary<int, string?> DecodedPieces { get; } = new();

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
            return string.Concat(tokenIds.Select(id => DecodedPieces.TryGetValue(id, out var p) ? p : ""));
        }

        public string? DecodeSingle(int tokenId)
        {
            return DecodedPieces.TryGetValue(tokenId, out var piece) ? piece : null;
        }

        public int GetLanguageTokenId(string floresCode) => floresCode.GetHashCode();
    }

    private sealed class FakeEngine : INllbInferenceEngine
    {
        public IReadOnlyList<int> TokenStream { get; set; } = Array.Empty<int>();

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
    }
}
