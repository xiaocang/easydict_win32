using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
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
    [InlineData(OcrEngineType.WindowsNative, typeof(WindowsOcrService))]
    [InlineData(OcrEngineType.Ollama, typeof(OllamaOcrService))]
    [InlineData(OcrEngineType.CustomApi, typeof(CustomApiOcrService))]
    public void Create_ReturnsImplementationMatchingSelectedEngine(
        OcrEngineType engine, System.Type expected)
    {
        var original = _settings.OcrEngine;
        try
        {
            _settings.OcrEngine = engine;

            var svc = OcrServiceFactory.Create();

            svc.Should().BeOfType(expected);
        }
        finally
        {
            _settings.OcrEngine = original;
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
    [InlineData(OcrEngineType.WindowsNative, typeof(WindowsOcrService))]
    [InlineData(OcrEngineType.Ollama, typeof(OllamaOcrService))]
    [InlineData(OcrEngineType.CustomApi, typeof(CustomApiOcrService))]
    public void Create_WithOptions_UsesProvidedEngineIndependentOfSavedSetting(
        OcrEngineType engine, System.Type expected)
    {
        var original = _settings.OcrEngine;
        try
        {
            _settings.OcrEngine = OcrEngineType.WindowsNative;
            var options = new OcrServiceOptions(engine, null, null, null, null);

            var svc = OcrServiceFactory.Create(options);

            svc.Should().BeOfType(expected);
        }
        finally
        {
            _settings.OcrEngine = original;
        }
    }

    [Fact]
    public void Create_WithOptions_DefaultsToWindowsNative_ForUnknownEngine()
    {
        var options = new OcrServiceOptions((OcrEngineType)99, null, null, null, null);

        var svc = OcrServiceFactory.Create(options);

        svc.Should().BeOfType<WindowsOcrService>();
    }
}
