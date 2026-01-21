using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for SettingsService.
/// Note: SettingsService is a singleton that loads from file on construction.
/// These tests verify property behavior and defaults using the singleton instance.
/// </summary>
[Trait("Category", "WinUI")]
public class SettingsServiceTests
{
    private readonly SettingsService _settings;

    public SettingsServiceTests()
    {
        _settings = SettingsService.Instance;
    }

    [Fact]
    public void Instance_ReturnsSameInstance()
    {
        var instance1 = SettingsService.Instance;
        var instance2 = SettingsService.Instance;

        instance1.Should().BeSameAs(instance2);
    }

    [Fact]
    public void DefaultService_HasDefaultValue()
    {
        // DefaultService should have a valid default
        _settings.DefaultService.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DefaultService_CanBeSet()
    {
        var original = _settings.DefaultService;
        try
        {
            _settings.DefaultService = "deepl";
            _settings.DefaultService.Should().Be("deepl");
        }
        finally
        {
            _settings.DefaultService = original;
        }
    }

    [Fact]
    public void TargetLanguage_HasDefaultValue()
    {
        _settings.TargetLanguage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void FirstLanguage_HasDefaultValue()
    {
        _settings.FirstLanguage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SecondLanguage_HasDefaultValue()
    {
        _settings.SecondLanguage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void FirstAndSecondLanguage_AreDifferent()
    {
        // FirstLanguage and SecondLanguage should not be the same
        _settings.FirstLanguage.Should().NotBe(_settings.SecondLanguage);
    }

    [Fact]
    public void AutoSelectTargetLanguage_CanBeAccessed()
    {
        // Just verify it's accessible
        var value = _settings.AutoSelectTargetLanguage;
        (value == true || value == false).Should().BeTrue();
    }

    [Fact]
    public void DeepLUseFreeApi_HasDefaultValue()
    {
        // Default should be true (use free API)
        _settings.DeepLUseFreeApi.Should().BeTrue();
    }

    [Fact]
    public void OpenAIEndpoint_HasDefaultValue()
    {
        _settings.OpenAIEndpoint.Should().Contain("api.openai.com");
    }

    [Fact]
    public void OpenAIModel_HasDefaultValue()
    {
        _settings.OpenAIModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OpenAITemperature_IsInValidRange()
    {
        _settings.OpenAITemperature.Should().BeGreaterOrEqualTo(0.0);
        _settings.OpenAITemperature.Should().BeLessOrEqualTo(2.0);
    }

    [Fact]
    public void OllamaEndpoint_HasDefaultValue()
    {
        _settings.OllamaEndpoint.Should().Contain("localhost:11434");
    }

    [Fact]
    public void BuiltInAIModel_HasDefaultValue()
    {
        _settings.BuiltInAIModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DeepSeekModel_HasDefaultValue()
    {
        _settings.DeepSeekModel.Should().Be("deepseek-chat");
    }

    [Fact]
    public void GroqModel_HasDefaultValue()
    {
        _settings.GroqModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ZhipuModel_HasDefaultValue()
    {
        _settings.ZhipuModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GitHubModelsModel_HasDefaultValue()
    {
        _settings.GitHubModelsModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GeminiModel_HasDefaultValue()
    {
        _settings.GeminiModel.Should().Contain("gemini");
    }

    [Fact]
    public void ShowWindowHotkey_HasDefaultValue()
    {
        _settings.ShowWindowHotkey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TranslateSelectionHotkey_HasDefaultValue()
    {
        _settings.TranslateSelectionHotkey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ShowMiniWindowHotkey_HasDefaultValue()
    {
        _settings.ShowMiniWindowHotkey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ShowFixedWindowHotkey_HasDefaultValue()
    {
        _settings.ShowFixedWindowHotkey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ProxyBypassLocal_DefaultsToTrue()
    {
        // Should default to true for Ollama compatibility
        _settings.ProxyBypassLocal.Should().BeTrue();
    }

    [Fact]
    public void EnableDpiAwareness_DefaultsToTrue()
    {
        _settings.EnableDpiAwareness.Should().BeTrue();
    }

    [Fact]
    public void WindowWidthDips_IsPositive()
    {
        _settings.WindowWidthDips.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WindowHeightDips_IsPositive()
    {
        _settings.WindowHeightDips.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WindowWidth_MapsToWindowWidthDips()
    {
        // Legacy property should map to new property
        var original = _settings.WindowWidthDips;
        try
        {
            _settings.WindowWidth = 800;
            _settings.WindowWidthDips.Should().Be(800);
            _settings.WindowWidth.Should().Be(800);
        }
        finally
        {
            _settings.WindowWidthDips = original;
        }
    }

    [Fact]
    public void WindowHeight_MapsToWindowHeightDips()
    {
        // Legacy property should map to new property
        var original = _settings.WindowHeightDips;
        try
        {
            _settings.WindowHeight = 600;
            _settings.WindowHeightDips.Should().Be(600);
            _settings.WindowHeight.Should().Be(600);
        }
        finally
        {
            _settings.WindowHeightDips = original;
        }
    }

    [Fact]
    public void MiniWindowEnabledServices_HasDefaultValue()
    {
        _settings.MiniWindowEnabledServices.Should().NotBeNull();
        _settings.MiniWindowEnabledServices.Should().NotBeEmpty();
    }

    [Fact]
    public void FixedWindowEnabledServices_HasDefaultValue()
    {
        _settings.FixedWindowEnabledServices.Should().NotBeNull();
        _settings.FixedWindowEnabledServices.Should().NotBeEmpty();
    }

    [Fact]
    public void MiniWindowDimensions_ArePositive()
    {
        _settings.MiniWindowWidthDips.Should().BeGreaterThan(0);
        _settings.MiniWindowHeightDips.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FixedWindowDimensions_ArePositive()
    {
        _settings.FixedWindowWidthDips.Should().BeGreaterThan(0);
        _settings.FixedWindowHeightDips.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ApiKeyProperties_AcceptNullValues()
    {
        // API key properties should accept null (not configured)
        var originalDeepL = _settings.DeepLApiKey;
        var originalOpenAI = _settings.OpenAIApiKey;
        var originalGemini = _settings.GeminiApiKey;

        try
        {
            _settings.DeepLApiKey = null;
            _settings.OpenAIApiKey = null;
            _settings.GeminiApiKey = null;

            _settings.DeepLApiKey.Should().BeNull();
            _settings.OpenAIApiKey.Should().BeNull();
            _settings.GeminiApiKey.Should().BeNull();
        }
        finally
        {
            _settings.DeepLApiKey = originalDeepL;
            _settings.OpenAIApiKey = originalOpenAI;
            _settings.GeminiApiKey = originalGemini;
        }
    }

    [Fact]
    public void BooleanSettings_CanBeAccessed()
    {
        // Verify all boolean settings can be accessed without error
        // Since they're booleans, they can only be true or false
        _ = _settings.MinimizeToTray;
        _ = _settings.ClipboardMonitoring;
        _ = _settings.AutoTranslate;
        _ = _settings.AlwaysOnTop;
        _ = _settings.ProxyEnabled;
        _ = _settings.MiniWindowAutoClose;
        _ = _settings.MiniWindowIsPinned;
        // If we got here without exception, the test passes
        true.Should().BeTrue();
    }
}
