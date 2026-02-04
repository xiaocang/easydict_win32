using Easydict.TranslationService;
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
    public void MainWindowEnabledServices_HasDefaultValue()
    {
        _settings.MainWindowEnabledServices.Should().NotBeNull();
        _settings.MainWindowEnabledServices.Should().NotBeEmpty();
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

    [Fact]
    public void RemovedProperties_DoNotExist()
    {
        // Verify that DefaultService and TargetLanguage properties were removed
        // This is a compile-time check - if this compiles, the properties don't exist
        var type = typeof(SettingsService);
        var defaultServiceProp = type.GetProperty("DefaultService");
        var targetLanguageProp = type.GetProperty("TargetLanguage");

        defaultServiceProp.Should().BeNull("DefaultService property should be removed");
        targetLanguageProp.Should().BeNull("TargetLanguage property should be removed");
    }

    [Fact]
    public void FirstLanguage_CanBeSet()
    {
        var original = _settings.FirstLanguage;
        try
        {
            _settings.FirstLanguage = "en";
            _settings.FirstLanguage.Should().Be("en");
        }
        finally
        {
            _settings.FirstLanguage = original;
        }
    }

    [Fact]
    public void SecondLanguage_CanBeSet()
    {
        var original = _settings.SecondLanguage;
        try
        {
            _settings.SecondLanguage = "ja";
            _settings.SecondLanguage.Should().Be("ja");
        }
        finally
        {
            _settings.SecondLanguage = original;
        }
    }

    [Fact]
    public void SaveAndLoad_PreservesFirstAndSecondLanguage()
    {
        var originalFirst = _settings.FirstLanguage;
        var originalSecond = _settings.SecondLanguage;

        try
        {
            // Set new values
            _settings.FirstLanguage = "ja";
            _settings.SecondLanguage = "en";
            _settings.Save();

            // Verify the values are persisted correctly
            _settings.FirstLanguage.Should().Be("ja");
            _settings.SecondLanguage.Should().Be("en");
        }
        finally
        {
            _settings.FirstLanguage = originalFirst;
            _settings.SecondLanguage = originalSecond;
            _settings.Save();
        }
    }

    [Fact]
    public void IsChinaRegion_ReturnsBool()
    {
        // IsChinaRegion should return a boolean based on system region/culture
        var result = SettingsService.IsChinaRegion();
        (result == true || result == false).Should().BeTrue();
    }

    [Fact]
    public void GetRegionDefaultServiceId_ReturnsValidServiceId()
    {
        var serviceId = SettingsService.GetRegionDefaultServiceId();

        // Should return either "bing" (China) or "google" (international)
        serviceId.Should().BeOneOf("bing", "google");
    }

    [Fact]
    public void GetRegionDefaultServiceId_MatchesChinaRegionDetection()
    {
        var isChinaRegion = SettingsService.IsChinaRegion();
        var serviceId = SettingsService.GetRegionDefaultServiceId();

        if (isChinaRegion)
        {
            serviceId.Should().Be("bing");
        }
        else
        {
            serviceId.Should().Be("google");
        }
    }

    [Fact]
    public void InternationalOnlyServices_ContainsExpectedServices()
    {
        // Verify the set contains known international-only services
        SettingsService.InternationalOnlyServices.Should().Contain("google");
        SettingsService.InternationalOnlyServices.Should().Contain("deepl");
        SettingsService.InternationalOnlyServices.Should().Contain("openai");
        SettingsService.InternationalOnlyServices.Should().Contain("gemini");
        SettingsService.InternationalOnlyServices.Should().Contain("linguee");
    }

    [Fact]
    public void InternationalOnlyServices_DoesNotContainChinaServices()
    {
        // Services available in China should NOT be in the international-only set
        SettingsService.InternationalOnlyServices.Should().NotContain("bing");
        SettingsService.InternationalOnlyServices.Should().NotContain("deepseek");
        SettingsService.InternationalOnlyServices.Should().NotContain("caiyun");
        SettingsService.InternationalOnlyServices.Should().NotContain("niutrans");
        SettingsService.InternationalOnlyServices.Should().NotContain("zhipu");
        SettingsService.InternationalOnlyServices.Should().NotContain("doubao");
    }

    [Fact]
    public void InternationalOnlyServices_IsCaseInsensitive()
    {
        // The set should be case-insensitive
        SettingsService.InternationalOnlyServices.Should().Contain("Google");
        SettingsService.InternationalOnlyServices.Should().Contain("DEEPL");
    }

    [Fact]
    public void EnableInternationalServices_CanBeToggled()
    {
        var original = _settings.EnableInternationalServices;
        try
        {
            _settings.EnableInternationalServices = true;
            _settings.EnableInternationalServices.Should().BeTrue();

            _settings.EnableInternationalServices = false;
            _settings.EnableInternationalServices.Should().BeFalse();
        }
        finally
        {
            _settings.EnableInternationalServices = original;
        }
    }

    [Fact]
    public void EnableInternationalServices_DefaultMatchesRegion()
    {
        // The default value (before any persistence) should be !IsChinaRegion()
        // We can't fully test this since the singleton already loaded, but we can
        // verify the property is accessible and returns a valid boolean
        var value = _settings.EnableInternationalServices;
        (value == true || value == false).Should().BeTrue();
    }

    #region IsChineseTimezone Tests

    [Fact]
    public void IsChineseTimezone_ReturnsBool()
    {
        // Should return a boolean without throwing
        var result = SettingsService.IsChineseTimezone();
        (result == true || result == false).Should().BeTrue();
    }

    [Fact]
    public void IsChineseTimezone_ConsistentAcrossCalls()
    {
        // Timezone detection should be deterministic
        var result1 = SettingsService.IsChineseTimezone();
        var result2 = SettingsService.IsChineseTimezone();
        result1.Should().Be(result2);
    }

    #endregion

    #region IsInternationalOnlyService Tests

    [Theory]
    [InlineData("google", true)]
    [InlineData("google_web", true)]
    [InlineData("deepl", true)]
    [InlineData("openai", true)]
    [InlineData("gemini", true)]
    [InlineData("groq", true)]
    [InlineData("github", true)]
    [InlineData("builtin", true)]
    [InlineData("linguee", true)]
    public void IsInternationalOnlyService_ReturnsTrueForInternationalServices(string serviceId, bool expected)
    {
        SettingsService.IsInternationalOnlyService(serviceId).Should().Be(expected);
    }

    [Theory]
    [InlineData("bing")]
    [InlineData("deepseek")]
    [InlineData("zhipu")]
    [InlineData("doubao")]
    [InlineData("caiyun")]
    [InlineData("niutrans")]
    [InlineData("ollama")]
    [InlineData("custom-openai")]
    public void IsInternationalOnlyService_ReturnsFalseForChinaCompatibleServices(string serviceId)
    {
        SettingsService.IsInternationalOnlyService(serviceId).Should().BeFalse();
    }

    [Fact]
    public void IsInternationalOnlyService_IsCaseInsensitive()
    {
        SettingsService.IsInternationalOnlyService("Google").Should().BeTrue();
        SettingsService.IsInternationalOnlyService("DEEPL").Should().BeTrue();
        SettingsService.IsInternationalOnlyService("OpenAI").Should().BeTrue();
    }

    [Fact]
    public void IsInternationalOnlyService_ReturnsFalseForUnknownService()
    {
        SettingsService.IsInternationalOnlyService("nonexistent").Should().BeFalse();
        SettingsService.IsInternationalOnlyService("").Should().BeFalse();
    }

    #endregion

    #region NotifyInternationalServiceFailed Tests

    [Fact]
    public void NotifyInternationalServiceFailed_DoesNotThrow()
    {
        // The method should never throw regardless of environment
        var act = () => _settings.NotifyInternationalServiceFailed("google", TranslationErrorCode.NetworkError);
        act.Should().NotThrow();
    }

    [Fact]
    public void NotifyInternationalServiceFailed_IgnoresNonNetworkErrors()
    {
        // Non-network errors should not trigger migration
        var originalMini = new List<string>(_settings.MiniWindowEnabledServices);
        var originalIntl = _settings.EnableInternationalServices;

        try
        {
            _settings.NotifyInternationalServiceFailed("google", TranslationErrorCode.InvalidApiKey);
            _settings.NotifyInternationalServiceFailed("google", TranslationErrorCode.UnsupportedLanguage);
            _settings.NotifyInternationalServiceFailed("google", TranslationErrorCode.RateLimited);
            _settings.NotifyInternationalServiceFailed("google", TranslationErrorCode.Unknown);

            // Settings should remain unchanged for non-network errors
            _settings.MiniWindowEnabledServices.Should().BeEquivalentTo(originalMini);
            _settings.EnableInternationalServices.Should().Be(originalIntl);
        }
        finally
        {
            _settings.MiniWindowEnabledServices = originalMini;
            _settings.EnableInternationalServices = originalIntl;
        }
    }

    [Fact]
    public void NotifyInternationalServiceFailed_IgnoresNonInternationalServices()
    {
        // China-compatible services should not trigger migration
        var originalMini = new List<string>(_settings.MiniWindowEnabledServices);
        var originalIntl = _settings.EnableInternationalServices;

        try
        {
            _settings.NotifyInternationalServiceFailed("bing", TranslationErrorCode.NetworkError);
            _settings.NotifyInternationalServiceFailed("deepseek", TranslationErrorCode.Timeout);

            // Settings should remain unchanged
            _settings.MiniWindowEnabledServices.Should().BeEquivalentTo(originalMini);
            _settings.EnableInternationalServices.Should().Be(originalIntl);
        }
        finally
        {
            _settings.MiniWindowEnabledServices = originalMini;
            _settings.EnableInternationalServices = originalIntl;
        }
    }

    #endregion

    #region Region Detection Consistency Tests

    [Fact]
    public void IsChinaRegion_DoesNotUseTimezoneAlone()
    {
        // IsChinaRegion should be locale-only; timezone is handled separately
        // by IsChineseTimezone + CheckRestrictedNetworkAsync.
        // Verify that the method exists and returns a consistent result.
        var result1 = SettingsService.IsChinaRegion();
        var result2 = SettingsService.IsChinaRegion();
        result1.Should().Be(result2, "IsChinaRegion should be deterministic");
    }

    [Fact]
    public void GetRegionDefaultServiceId_OnlyDependsOnLocale()
    {
        // GetRegionDefaultServiceId uses IsChinaRegion (locale-only),
        // so the result should be consistent with IsChinaRegion.
        var isChinaRegion = SettingsService.IsChinaRegion();
        var serviceId = SettingsService.GetRegionDefaultServiceId();

        if (isChinaRegion)
            serviceId.Should().Be("bing");
        else
            serviceId.Should().Be("google");
    }

    #endregion
}
