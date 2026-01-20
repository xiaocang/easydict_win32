using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Tests for TranslationManager service registration, caching, and streaming detection.
/// </summary>
public class TranslationManagerTests : IDisposable
{
    private readonly TranslationManager _manager;

    public TranslationManagerTests()
    {
        _manager = new TranslationManager();
    }

    [Fact]
    public void Constructor_RegistersDefaultServices()
    {
        // Assert default services are registered
        _manager.Services.Should().ContainKey("google");
        _manager.Services.Should().ContainKey("deepl");
        _manager.Services.Should().ContainKey("openai");
        _manager.Services.Should().ContainKey("ollama");
        _manager.Services.Should().ContainKey("builtin");
    }

    [Fact]
    public void Constructor_RegistersPhase2Services()
    {
        // Assert Phase 2 services are registered
        _manager.Services.Should().ContainKey("deepseek");
        _manager.Services.Should().ContainKey("groq");
        _manager.Services.Should().ContainKey("zhipu");
        _manager.Services.Should().ContainKey("github");
        _manager.Services.Should().ContainKey("custom-openai");
        _manager.Services.Should().ContainKey("gemini");
    }

    [Fact]
    public void DefaultServiceId_IsGoogle()
    {
        _manager.DefaultServiceId.Should().Be("google");
    }

    [Fact]
    public void DefaultServiceId_CanBeChanged()
    {
        _manager.DefaultServiceId = "deepl";
        _manager.DefaultServiceId.Should().Be("deepl");
    }

    [Fact]
    public void DefaultServiceId_ThrowsForUnknownService()
    {
        var action = () => _manager.DefaultServiceId = "unknown-service";
        action.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown service*");
    }

    [Fact]
    public void RegisterService_AddsNewService()
    {
        var mockService = new TestTranslationService("test-service", "Test Service");

        _manager.RegisterService(mockService);

        _manager.Services.Should().ContainKey("test-service");
        _manager.Services["test-service"].Should().BeSameAs(mockService);
    }

    [Fact]
    public void RegisterService_OverwritesExistingService()
    {
        var newGoogleService = new TestTranslationService("google", "New Google");

        _manager.RegisterService(newGoogleService);

        _manager.Services["google"].Should().BeSameAs(newGoogleService);
    }

    [Fact]
    public void ConfigureService_ConfiguresExistingService()
    {
        bool configured = false;

        _manager.ConfigureService("google", service =>
        {
            configured = true;
            service.Should().NotBeNull();
        });

        configured.Should().BeTrue();
    }

    [Fact]
    public void ConfigureService_DoesNothingForUnknownService()
    {
        bool configured = false;

        _manager.ConfigureService("unknown-service", service =>
        {
            configured = true;
        });

        configured.Should().BeFalse();
    }

    [Fact]
    public void IsStreamingService_ReturnsTrue_ForStreamingServices()
    {
        _manager.IsStreamingService("openai").Should().BeTrue();
        _manager.IsStreamingService("ollama").Should().BeTrue();
        _manager.IsStreamingService("builtin").Should().BeTrue();
        _manager.IsStreamingService("deepseek").Should().BeTrue();
        _manager.IsStreamingService("groq").Should().BeTrue();
        _manager.IsStreamingService("gemini").Should().BeTrue();
    }

    [Fact]
    public void IsStreamingService_ReturnsFalse_ForNonStreamingServices()
    {
        _manager.IsStreamingService("google").Should().BeFalse();
        _manager.IsStreamingService("deepl").Should().BeFalse();
    }

    [Fact]
    public void IsStreamingService_ReturnsFalse_ForUnknownService()
    {
        _manager.IsStreamingService("unknown-service").Should().BeFalse();
    }

    [Fact]
    public void GetStreamingService_ReturnsService_ForStreamingServices()
    {
        var service = _manager.GetStreamingService("openai");
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IStreamTranslationService>();
    }

    [Fact]
    public void GetStreamingService_ReturnsNull_ForNonStreamingServices()
    {
        var service = _manager.GetStreamingService("google");
        service.Should().BeNull();
    }

    [Fact]
    public void GetStreamingService_ReturnsNull_ForUnknownService()
    {
        var service = _manager.GetStreamingService("unknown-service");
        service.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_ThrowsForUnknownService()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        var action = () => _manager.TranslateAsync(request, serviceId: "unknown-service");

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.Message.Contains("Unknown service"));
    }

    [Fact]
    public void Services_ReturnsReadOnlyDictionary()
    {
        var services = _manager.Services;

        services.Should().NotBeNull();
        services.Should().HaveCountGreaterThan(5);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    /// <summary>
    /// Simple test implementation of ITranslationService for testing.
    /// </summary>
    private class TestTranslationService : ITranslationService
    {
        public TestTranslationService(string serviceId, string displayName)
        {
            ServiceId = serviceId;
            DisplayName = displayName;
        }

        public string ServiceId { get; }
        public string DisplayName { get; }
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English, Language.SimplifiedChinese };

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TranslationResult
            {
                TranslatedText = $"Translated: {request.Text}",
                OriginalText = request.Text,
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName
            });
        }

        public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Language.Auto);
        }
    }
}
