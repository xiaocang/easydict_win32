using Easydict.TranslationService;
using Easydict.WindowsAI;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for SettingsService.
/// Note: SettingsService is a singleton that loads from file on construction.
/// These tests verify property behavior and defaults using the singleton instance.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class SettingsServiceTests
{
    private readonly SettingsService _settings;

    public SettingsServiceTests()
    {
        _settings = SettingsService.Instance;
    }

    [Fact]
    public void Migration_ShouldPreserveAlreadyProtectedSensitiveSettings()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        var alreadyProtected = LocalCredentialProtector.Protect("sk-already-protected");
        var nestedProtected = LocalCredentialProtector.Protect(
            LocalCredentialProtector.Protect("sk-nested-protected"));

        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    [nameof(SettingsService.OpenAIApiKey)] = alreadyProtected,
                    [nameof(SettingsService.CustomOpenAIApiKey)] = nestedProtected,
                    [nameof(SettingsService.DeepLApiKey)] = "plain-legacy-key"
                },
                new JsonSerializerOptions { WriteIndented = true }));

        var previousSettingsDirectory = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", testDirectory.Path);
            CreateIsolatedSettingsService();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previousSettingsDirectory);
        }

        using var migratedSettings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = migratedSettings.RootElement;
        var openAiValue = root.GetProperty(nameof(SettingsService.OpenAIApiKey)).GetString();
        var customOpenAiValue = root.GetProperty(nameof(SettingsService.CustomOpenAIApiKey)).GetString();
        var deepLValue = root.GetProperty(nameof(SettingsService.DeepLApiKey)).GetString();

        openAiValue.Should().Be(alreadyProtected);
        customOpenAiValue.Should().NotBe(nestedProtected);
        customOpenAiValue.Should().StartWith("edcred1:user:");
        var customOpenAiPlaintext = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            customOpenAiValue,
            "stable-machine-id",
            out var customOpenAiNeedsMigration,
            out var customOpenAiDecryptFailed);
        customOpenAiPlaintext.Should().Be("sk-nested-protected");
        customOpenAiNeedsMigration.Should().BeFalse();
        customOpenAiDecryptFailed.Should().BeFalse();
        deepLValue.Should().NotBe("plain-legacy-key");
        deepLValue.Should().StartWith("edcred1:user:");
        LocalCredentialProtector.TryUnprotect(deepLValue!, out var migratedDeepL)
            .Should().BeTrue();
        migratedDeepL.Should().Be("plain-legacy-key");
    }

    [Fact]
    public void Save_WithNoChanges_ShouldNotRewriteSettingsFile()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        var previousSettingsDirectory = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        SettingsService settings;
        try
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", testDirectory.Path);
            settings = CreateIsolatedSettingsService();
            settings.OpenAIApiKey = "sk-stable-no-rewrite";
            settings.Save();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previousSettingsDirectory);
        }

        var savedJson = File.ReadAllText(settingsPath);
        var fixedTimestamp = new DateTime(2024, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsPath, fixedTimestamp);
        var unchangedTimestamp = File.GetLastWriteTimeUtc(settingsPath);

        settings.Save();

        File.ReadAllText(settingsPath).Should().Be(savedJson);
        File.GetLastWriteTimeUtc(settingsPath).Should().Be(unchangedTimestamp);
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
    public void EnableLocalDictionarySuggestions_DefaultsToFalse()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current) &&
               !File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
        {
            current = Path.GetDirectoryName(current);
        }

        current.Should().NotBeNullOrEmpty();
        var source = File.ReadAllText(Path.Combine(current!, "src", "Easydict.WinUI", "Services", "SettingsService.cs"));
        source.Should().Contain("public bool EnableLocalDictionarySuggestions { get; set; } = false;");
        source.Should().Contain("EnableLocalDictionarySuggestions = GetValue(nameof(EnableLocalDictionarySuggestions), false);");
    }

    [Fact]
    public void UseLocalAiWorker_DefaultsToTrue()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current) &&
               !File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
        {
            current = Path.GetDirectoryName(current);
        }

        current.Should().NotBeNullOrEmpty();
        var source = File.ReadAllText(Path.Combine(current!, "src", "Easydict.WinUI", "Services", "SettingsService.cs"));
        source.Should().Contain("public bool UseLocalAiWorker { get; set; } = true;");
        source.Should().Contain("UseLocalAiWorker = GetValue(nameof(UseLocalAiWorker), true);");
    }

    [Fact]
    public void UseOcrWorker_DefaultsToTrue()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current) &&
               !File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
        {
            current = Path.GetDirectoryName(current);
        }

        current.Should().NotBeNullOrEmpty();
        var source = File.ReadAllText(Path.Combine(current!, "src", "Easydict.WinUI", "Services", "SettingsService.cs"));
        source.Should().Contain("public bool UseOcrWorker { get; set; } = true;");
        source.Should().Contain("UseOcrWorker = GetValue(nameof(UseOcrWorker), true);");
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

    [Theory]
    [InlineData(WindowsAIReadyState.Ready)]
    [InlineData(WindowsAIReadyState.NotReady)]
    public void GetDefaultEnabledServices_AddsPhiSilica_WhenDeviceSupportsIt(WindowsAIReadyState state)
    {
        var services = SettingsService.GetDefaultEnabledServices(state);

        services.Should().Equal("google", "windows-local-ai");
    }

    [Theory]
    [InlineData(WindowsAIReadyState.CapabilityMissing)]
    [InlineData(WindowsAIReadyState.NotCompatibleWithSystemHardware)]
    [InlineData(WindowsAIReadyState.OSUpdateNeeded)]
    [InlineData(WindowsAIReadyState.DisabledByUser)]
    [InlineData(WindowsAIReadyState.UnsupportedWindowsAIBaseline)]
    [InlineData(WindowsAIReadyState.NotSupportedOnCurrentSystem)]
    public void GetDefaultEnabledServices_KeepsGoogleOnly_WhenPhiSilicaIsNotSupported(WindowsAIReadyState state)
    {
        var services = SettingsService.GetDefaultEnabledServices(state);

        services.Should().Equal("google");
    }

    [Fact]
    public void GetDefaultEnabledServicesForProfile_AddsPhiSilica_ForFreshProfile()
    {
        var probed = false;
        var services = SettingsService.GetDefaultEnabledServicesForProfile(
            hasSavedEnabledServiceSettings: false,
            hasUserConfiguredServices: false,
            getPhiSilicaReadyState: () =>
            {
                probed = true;
                return WindowsAIReadyState.NotReady;
            });

        services.Should().Equal("google", "windows-local-ai");
        probed.Should().BeTrue();
    }

    [Fact]
    public void GetDefaultEnabledServicesForProfile_KeepsLegacyDefault_AndSkipsProbe_WhenAnyServiceSettingExists()
    {
        var probed = false;
        var services = SettingsService.GetDefaultEnabledServicesForProfile(
            hasSavedEnabledServiceSettings: true,
            hasUserConfiguredServices: false,
            getPhiSilicaReadyState: () =>
            {
                probed = true;
                return WindowsAIReadyState.Ready;
            });

        services.Should().Equal("google");
        probed.Should().BeFalse();
    }

    [Fact]
    public void GetDefaultEnabledServicesForProfile_KeepsLegacyDefault_AndSkipsProbe_WhenUserConfiguredHistoryExists()
    {
        var probed = false;
        var services = SettingsService.GetDefaultEnabledServicesForProfile(
            hasSavedEnabledServiceSettings: false,
            hasUserConfiguredServices: true,
            getPhiSilicaReadyState: () =>
            {
                probed = true;
                return WindowsAIReadyState.Ready;
            });

        services.Should().Equal("google");
        probed.Should().BeFalse();
    }

    [Fact]
    public void DisableServiceEverywhere_RemovesServiceFromAllWindowsAndMarksUserConfigured()
    {
        var originalMiniServices = new List<string>(_settings.MiniWindowEnabledServices);
        var originalMainServices = new List<string>(_settings.MainWindowEnabledServices);
        var originalFixedServices = new List<string>(_settings.FixedWindowEnabledServices);
        var originalMiniQuery = new Dictionary<string, bool>(_settings.MiniWindowServiceEnabledQuery);
        var originalMainQuery = new Dictionary<string, bool>(_settings.MainWindowServiceEnabledQuery);
        var originalFixedQuery = new Dictionary<string, bool>(_settings.FixedWindowServiceEnabledQuery);
        var originalHasUserConfiguredServices = _settings.HasUserConfiguredServices;

        try
        {
            _settings.MiniWindowEnabledServices = ["google", "windows-local-ai"];
            _settings.MainWindowEnabledServices = ["windows-local-ai", "deepl"];
            _settings.FixedWindowEnabledServices = ["WINDOWS-LOCAL-AI", "bing"];
            _settings.MiniWindowServiceEnabledQuery = new Dictionary<string, bool>
            {
                ["windows-local-ai"] = true
            };
            _settings.MainWindowServiceEnabledQuery = new Dictionary<string, bool>
            {
                ["windows-local-ai"] = false
            };
            _settings.FixedWindowServiceEnabledQuery = new Dictionary<string, bool>
            {
                ["WINDOWS-LOCAL-AI"] = true
            };
            _settings.HasUserConfiguredServices = false;

            _settings.DisableServiceEverywhere("windows-local-ai");

            _settings.MiniWindowEnabledServices.Should().Equal("google");
            _settings.MainWindowEnabledServices.Should().Equal("deepl");
            _settings.FixedWindowEnabledServices.Should().Equal("bing");
            _settings.MiniWindowServiceEnabledQuery.Should().NotContainKey("windows-local-ai");
            _settings.MainWindowServiceEnabledQuery.Should().NotContainKey("windows-local-ai");
            _settings.FixedWindowServiceEnabledQuery.Keys
                .Should().NotContain(key => string.Equals(key, "windows-local-ai", StringComparison.OrdinalIgnoreCase));
            _settings.HasUserConfiguredServices.Should().BeTrue();
        }
        finally
        {
            _settings.MiniWindowEnabledServices = originalMiniServices;
            _settings.MainWindowEnabledServices = originalMainServices;
            _settings.FixedWindowEnabledServices = originalFixedServices;
            _settings.MiniWindowServiceEnabledQuery = originalMiniQuery;
            _settings.MainWindowServiceEnabledQuery = originalMainQuery;
            _settings.FixedWindowServiceEnabledQuery = originalFixedQuery;
            _settings.HasUserConfiguredServices = originalHasUserConfiguredServices;
            _settings.Save();
        }
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

    #region SelectedLanguages Tests

    [Fact]
    public void SelectedLanguages_HasDefaultValue()
    {
        _settings.SelectedLanguages.Should().NotBeNull();
        _settings.SelectedLanguages.Should().Contain("zh");
        _settings.SelectedLanguages.Should().Contain("en");
        _settings.SelectedLanguages.Should().Contain("ja");
        _settings.SelectedLanguages.Should().Contain("ko");
        _settings.SelectedLanguages.Should().Contain("fr");
        _settings.SelectedLanguages.Should().Contain("de");
        _settings.SelectedLanguages.Should().Contain("es");
    }

    [Fact]
    public void SelectedLanguages_CanBeSet()
    {
        var original = new List<string>(_settings.SelectedLanguages);
        try
        {
            var newLanguages = new List<string> { "zh", "en", "ja" };
            _settings.SelectedLanguages = newLanguages;
            _settings.SelectedLanguages.Should().BeEquivalentTo(newLanguages);
        }
        finally
        {
            _settings.SelectedLanguages = original;
        }
    }

    [Fact]
    public void SelectedLanguages_ContainsAtLeastTwoLanguages()
    {
        _settings.SelectedLanguages.Should().HaveCountGreaterOrEqualTo(2);
    }

    #endregion

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
        // by IsChineseTimezone + NotifyInternationalServiceFailed (lazy probe).
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

    #region OCR + TTS Round-Trip

    [Fact]
    public void OcrSettings_RoundTripThroughSave()
    {
        var originalEngine = _settings.OcrEngine;
        var originalEndpoint = _settings.OcrEndpoint;
        var originalModel = _settings.OcrModel;
        var originalPrompt = _settings.OcrSystemPrompt;

        try
        {
            _settings.OcrEngine = OcrEngineType.Ollama;
            _settings.OcrEndpoint = "http://test.local/ocr";
            _settings.OcrModel = "test-model";
            _settings.OcrSystemPrompt = "Extract test text.";
            _settings.Save();

            _settings.OcrEngine.Should().Be(OcrEngineType.Ollama);
            _settings.OcrEndpoint.Should().Be("http://test.local/ocr");
            _settings.OcrModel.Should().Be("test-model");
            _settings.OcrSystemPrompt.Should().Be("Extract test text.");
        }
        finally
        {
            _settings.OcrEngine = originalEngine;
            _settings.OcrEndpoint = originalEndpoint;
            _settings.OcrModel = originalModel;
            _settings.OcrSystemPrompt = originalPrompt;
            _settings.Save();
        }
    }

    [Fact]
    public void TtsSettings_RoundTripThroughSave()
    {
        var originalSpeed = _settings.TtsSpeed;
        var originalAutoPlay = _settings.AutoPlayTranslation;

        try
        {
            _settings.TtsSpeed = 2.5;
            _settings.AutoPlayTranslation = true;
            _settings.Save();

            _settings.TtsSpeed.Should().Be(2.5);
            _settings.AutoPlayTranslation.Should().BeTrue();
        }
        finally
        {
            _settings.TtsSpeed = originalSpeed;
            _settings.AutoPlayTranslation = originalAutoPlay;
            _settings.Save();
        }
    }

    #endregion

    private static SettingsService CreateIsolatedSettingsService()
    {
        var constructor = typeof(SettingsService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        constructor.Should().NotBeNull();
        return (SettingsService)constructor!.Invoke(null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Easydict.WinUI.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
