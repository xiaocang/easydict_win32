using Easydict.TranslationService.Services;
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
}
