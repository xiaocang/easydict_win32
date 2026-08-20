using Easydict.TranslationService.Services;
using Easydict.SidecarClient.Protocol;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.Workers;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for OcrServiceFactory.
/// The factory reads <see cref="SettingsService.OcrEngine"/> and returns the corresponding
/// <see cref="IOcrService"/> implementation. Settings are mutated via try/finally to
/// avoid bleeding state into other tests in the SettingsService collection.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class OcrServiceFactoryTests
{
    private readonly SettingsService _settings = SettingsService.Instance;

    [Theory]
    [InlineData(OcrEngineType.WindowsNative, typeof(OcrWorkerClient))]
    [InlineData(OcrEngineType.Ollama, typeof(OllamaOcrService))]
    [InlineData(OcrEngineType.CustomApi, typeof(CustomApiOcrService))]
    [InlineData(OcrEngineType.PpOcrV6, typeof(OcrWorkerClient))]
    public void Create_ReturnsImplementationMatchingSelectedEngine(
        OcrEngineType engine, System.Type expected)
    {
        var original = _settings.OcrEngine;
        var originalUseWorker = _settings.UseOcrWorker;
        try
        {
            _settings.OcrEngine = engine;
            _settings.UseOcrWorker = true;

            var svc = OcrServiceFactory.Create();

            svc.Should().BeOfType(expected);
            (svc as IDisposable)?.Dispose();
        }
        finally
        {
            _settings.OcrEngine = original;
            _settings.UseOcrWorker = originalUseWorker;
        }
    }

    [Fact]
    public void Create_ReturnsInProcWindowsOcr_WhenWorkerDisabled()
    {
        var original = _settings.OcrEngine;
        var originalUseWorker = _settings.UseOcrWorker;
        try
        {
            _settings.OcrEngine = OcrEngineType.WindowsNative;
            _settings.UseOcrWorker = false;

            var svc = OcrServiceFactory.Create();

            svc.Should().BeOfType<WindowsOcrService>();
            (svc as IDisposable)?.Dispose();
        }
        finally
        {
            _settings.OcrEngine = original;
            _settings.UseOcrWorker = originalUseWorker;
        }
    }

    [Fact]
    public void Create_DefaultsToWindowsNative_ForUnknownEngine()
    {
        var original = _settings.OcrEngine;
        try
        {
            _settings.OcrEngine = (OcrEngineType)99;

            var svc = OcrServiceFactory.Create();

            svc.Should().BeOfType<WindowsOcrService>();
        }
        finally
        {
            _settings.OcrEngine = original;
        }
    }

    [Theory]
    [InlineData(OcrEngineType.WindowsNative, typeof(OcrWorkerClient))]
    [InlineData(OcrEngineType.Ollama, typeof(OllamaOcrService))]
    [InlineData(OcrEngineType.CustomApi, typeof(CustomApiOcrService))]
    [InlineData(OcrEngineType.PpOcrV6, typeof(OcrWorkerClient))]
    public void Create_WithOptions_UsesProvidedEngineIndependentOfSavedSetting(
        OcrEngineType engine, System.Type expected)
    {
        var original = _settings.OcrEngine;
        var originalUseWorker = _settings.UseOcrWorker;
        try
        {
            _settings.OcrEngine = OcrEngineType.WindowsNative;
            _settings.UseOcrWorker = true;
            var options = new OcrServiceOptions(engine, null, null, null, null);

            var svc = OcrServiceFactory.Create(options);

            svc.Should().BeOfType(expected);
            (svc as IDisposable)?.Dispose();
        }
        finally
        {
            _settings.OcrEngine = original;
            _settings.UseOcrWorker = originalUseWorker;
        }
    }

    [Fact]
    public void Create_WithOptions_DefaultsToWindowsNative_ForUnknownEngine()
    {
        var options = new OcrServiceOptions((OcrEngineType)99, null, null, null, null);

        var svc = OcrServiceFactory.Create(options);

        svc.Should().BeOfType<WindowsOcrService>();
    }

    [Fact]
    public void OcrServiceOptions_DefaultsToOllamaEndpoint_ForOllama()
    {
        var options = new OcrServiceOptions(OcrEngineType.Ollama, null, null, null, null);

        options.Endpoint.Should().Be(OcrServiceOptions.DefaultOllamaEndpoint);
        options.Model.Should().Be(OcrServiceOptions.DefaultOllamaModel);
    }

    [Fact]
    public void OcrServiceOptions_DefaultsToResponsesEndpoint_ForCustomApi()
    {
        var options = new OcrServiceOptions(OcrEngineType.CustomApi, null, null, null, null);

        options.Endpoint.Should().Be(OpenAIService.DefaultEndpoint);
        options.Model.Should().Be(OpenAIService.DefaultModel);
    }

    [Fact]
    public void OcrServiceOptions_DefaultsToSmallPpOcrV6Model()
    {
        var options = new OcrServiceOptions(OcrEngineType.PpOcrV6, null, null, null, null);

        options.Endpoint.Should().BeEmpty();
        options.Model.Should().Be(PpOcrV6ModelCatalog.SmallId);
    }

    [Fact]
    public void PpOcrV6Catalog_ContainsOfficialTiersAndSizes()
    {
        PpOcrV6ModelCatalog.Models.Should().ContainSingle(model => model.Id == PpOcrV6ModelCatalog.TinyId);
        PpOcrV6ModelCatalog.Models.Should().ContainSingle(model => model.Id == PpOcrV6ModelCatalog.SmallId);
        PpOcrV6ModelCatalog.Models.Should().ContainSingle(model => model.Id == PpOcrV6ModelCatalog.MediumId);
        PpOcrV6ModelCatalog.Get(PpOcrV6ModelCatalog.MediumId).DownloadSizeBytes.Should().Be(138_739_282);
        PpOcrV6ModelCatalog.Get(PpOcrV6ModelCatalog.TinyId).Languages.Should().NotContain("ja");
    }

    [Fact]
    public void PpOcrV6Catalog_ArtifactsMaintainStructuralInvariants()
    {
        foreach (var model in PpOcrV6ModelCatalog.Models)
        {
            model.DownloadSizeBytes.Should().Be(model.Artifacts.Sum(artifact => artifact.SizeBytes));
            model.Languages.Should().NotBeEmpty();
            model.Artifacts.Select(artifact => artifact.FileName)
                .Should().Equal(
                    PpOcrV6ModelStore.DetectorModelFileName,
                    PpOcrV6ModelStore.DetectorConfigFileName,
                    PpOcrV6ModelStore.RecognizerModelFileName,
                    PpOcrV6ModelStore.RecognizerConfigFileName);
            model.Artifacts.Should().OnlyContain(artifact => artifact.SizeBytes > 0);

            foreach (var artifact in model.Artifacts)
            {
                var isDetector = artifact.FileName.StartsWith("det.", StringComparison.Ordinal);
                var modelName = isDetector ? model.DetectorModelName : model.RecognizerModelName;
                var remoteName = artifact.FileName.EndsWith(".onnx", StringComparison.Ordinal)
                    ? "inference.onnx"
                    : "inference.yml";
                artifact.Url.Should().Contain($"/{modelName}_onnx/");
                artifact.Url.Should().EndWith($"/{remoteName}");
            }

            model.Artifacts.Select(artifact => artifact.Url).Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void OcrServiceOptions_UsesDedicatedPpOcrV6ModelSetting()
    {
        var originalEngine = _settings.OcrEngine;
        var originalModel = _settings.OcrModel;
        var originalPpModel = _settings.PpOcrV6ModelId;
        try
        {
            _settings.OcrEngine = OcrEngineType.PpOcrV6;
            _settings.OcrModel = "custom-api-model";
            _settings.PpOcrV6ModelId = PpOcrV6ModelCatalog.TinyId;

            OcrServiceOptions.FromSettings(_settings).Model.Should().Be(PpOcrV6ModelCatalog.TinyId);

            _settings.OcrEngine = OcrEngineType.CustomApi;
            OcrServiceOptions.FromSettings(_settings).Model.Should().Be("custom-api-model");
        }
        finally
        {
            _settings.OcrEngine = originalEngine;
            _settings.OcrModel = originalModel;
            _settings.PpOcrV6ModelId = originalPpModel;
        }
    }

    [Fact]
    public void PpOcrV6ModelStore_InvalidateCompletion_IgnoresMissingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Easydict", "ppocrv6-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PpOcrV6ModelStore(root);
            var act = () => store.InvalidateCompletion(PpOcrV6ModelCatalog.TinyId);

            act.Should().NotThrow();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("zh", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("sr", true)]
    [InlineData("sr-Latn", true)]
    [InlineData("ja", false)]
    public void PpOcrV6Catalog_SupportsBaseAndQualifiedLanguageTags(string languageTag, bool expected)
    {
        PpOcrV6ModelCatalog.SupportsLanguage(PpOcrV6ModelCatalog.TinyId, languageTag)
            .Should().Be(expected);
    }

    [Fact]
    public void PpOcrV6ModelStore_GetStateBySize_DetectsMissingAndWrongSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "Easydict", "ppocrv6-tests", Guid.NewGuid().ToString("N"));
        var store = new PpOcrV6ModelStore(root);
        var paths = store.GetPaths(PpOcrV6ModelCatalog.TinyId);
        try
        {
            store.GetStateBySize(PpOcrV6ModelCatalog.TinyId).Should().Be(PpOcrV6ModelState.Missing);
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(paths.CompletionSentinel, PpOcrV6ModelCatalog.TinyId);
            store.GetStateBySize(PpOcrV6ModelCatalog.TinyId).Should().Be(PpOcrV6ModelState.Invalid);

            foreach (var artifact in PpOcrV6ModelCatalog.Get(PpOcrV6ModelCatalog.TinyId).Artifacts)
            {
                using var stream = File.Create(Path.Combine(paths.Directory, artifact.FileName));
                stream.SetLength(artifact.SizeBytes);
                stream.Position = 0;
                stream.WriteByte(0xA5);
            }

            store.GetStateBySize(PpOcrV6ModelCatalog.TinyId).Should().Be(PpOcrV6ModelState.Installed);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void FormatEndpointForDiagnostics_KeepsKnownDefaultEndpoint()
    {
        var options = new OcrServiceOptions(OcrEngineType.Ollama, null, null, null, null);

        OcrTranslateService.FormatEndpointForDiagnostics(options)
            .Should().Be(OcrServiceOptions.DefaultOllamaEndpoint);
    }

    [Fact]
    public void FormatEndpointForDiagnostics_RedactsCustomEndpoint()
    {
        var options = new OcrServiceOptions(
            OcrEngineType.CustomApi,
            null,
            "https://example.test/v1/responses?api_key=secret",
            null,
            null);

        OcrTranslateService.FormatEndpointForDiagnostics(options)
            .Should().Be("<redacted>");
    }

    [Fact]
    public void ModelDownloadClient_DoesNotDisposeInjectedHttpClient()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        using (var downloadClient = new ModelDownloadClient(httpClient))
        {
        }

        var act = () => httpClient.DefaultRequestHeaders.Add("X-Test", "still-alive");

        act.Should().NotThrow();
    }

    [Fact]
    public void CreateProxyAwareHttpClient_UsesApiOcrTimeout_ByDefault()
    {
        using var client = OcrServiceFactory.CreateProxyAwareHttpClient(
            proxyEnabled: false,
            proxyUri: null,
            proxyBypassLocal: true);

        client.Timeout.Should().Be(OcrServiceFactory.ApiOcrRequestTimeout);
    }

    [Fact]
    public void CreateProxyAwareHandler_ConfiguresExplicitProxy_WhenEnabled()
    {
        using var handler = OcrServiceFactory.CreateProxyAwareHandler(
            proxyEnabled: true,
            proxyUri: "http://127.0.0.1:7890",
            proxyBypassLocal: true);

        handler.Proxy.Should().NotBeNull();
        handler.UseProxy.Should().BeTrue();
        handler.Proxy!.GetProxy(new Uri("https://api.openai.com/v1/responses"))
            .Should().Be(new Uri("http://127.0.0.1:7890/"));
    }

    [Fact]
    public void CreateProxyAwareHandler_BypassesLocalhost_WhenConfigured()
    {
        using var handler = OcrServiceFactory.CreateProxyAwareHandler(
            proxyEnabled: true,
            proxyUri: "http://127.0.0.1:7890",
            proxyBypassLocal: true);

        handler.Proxy.Should().NotBeNull();
        handler.Proxy!.IsBypassed(new Uri("http://localhost:11434/api/generate"))
            .Should().BeTrue();
        handler.Proxy.IsBypassed(new Uri("https://api.openai.com/v1/responses"))
            .Should().BeFalse();
    }

    [Fact]
    public void CreateProxyAwareHandler_DoesNotConfigureProxy_WhenDisabled()
    {
        using var handler = OcrServiceFactory.CreateProxyAwareHandler(
            proxyEnabled: false,
            proxyUri: "http://127.0.0.1:7890",
            proxyBypassLocal: true);

        handler.Proxy.Should().BeNull();
    }

    [Fact]
    public async Task PpOcrV6ModelDownloadService_RemoveAsync_RemovesModelDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Easydict", "ppocrv6-remove-tests", Guid.NewGuid().ToString("N"));
        var store = new PpOcrV6ModelStore(root);
        var paths = store.GetPaths(PpOcrV6ModelCatalog.TinyId);
        try
        {
            Directory.CreateDirectory(paths.Directory);
            File.WriteAllText(paths.CompletionSentinel, PpOcrV6ModelCatalog.TinyId);
            using var service = new PpOcrV6ModelDownloadService(
                new HttpClient(new RecordingHttpMessageHandler()), store);

            await service.RemoveAsync(PpOcrV6ModelCatalog.TinyId);

            Directory.Exists(paths.Directory).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PpOcrV6ModelDownloadService_RemoveAsync_HonorsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Easydict", "ppocrv6-remove-tests", Guid.NewGuid().ToString("N"));
        var store = new PpOcrV6ModelStore(root);
        var paths = store.GetPaths(PpOcrV6ModelCatalog.TinyId);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Directory.CreateDirectory(paths.Directory);
            using var service = new PpOcrV6ModelDownloadService(
                new HttpClient(new RecordingHttpMessageHandler()), store);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.RemoveAsync(PpOcrV6ModelCatalog.TinyId, cts.Token));

            Directory.Exists(paths.Directory).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
