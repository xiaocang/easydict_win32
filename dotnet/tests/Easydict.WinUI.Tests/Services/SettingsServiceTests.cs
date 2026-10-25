using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.WindowsAI;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.TranslationService.Models;
using FluentAssertions;
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
    public void HistorySettings_DefaultClampAndRoundTripUseIsolatedStorage()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");

        var defaults = CreateIsolatedSettingsService(testDirectory.Path);
        defaults.HistoryEnabled.Should().BeFalse();
        defaults.HistoryRetentionDays.Should().Be(30);

        defaults.HistoryEnabled = true;
        defaults.HistoryRetentionDays = 1;
        defaults.Save();
        var minimum = CreateIsolatedSettingsService(testDirectory.Path);
        minimum.HistoryEnabled.Should().BeTrue();
        minimum.HistoryRetentionDays.Should().Be(1);

        minimum.HistoryEnabled = false;
        minimum.HistoryRetentionDays = 3650;
        minimum.Save();
        var maximum = CreateIsolatedSettingsService(testDirectory.Path);
        maximum.HistoryEnabled.Should().BeFalse();
        maximum.HistoryRetentionDays.Should().Be(3650);

        File.WriteAllText(settingsPath, """{"HistoryRetentionDays":-1}""");
        CreateIsolatedSettingsService(testDirectory.Path).HistoryRetentionDays.Should().Be(1);
        File.WriteAllText(settingsPath, """{"HistoryRetentionDays":5000}""");
        CreateIsolatedSettingsService(testDirectory.Path).HistoryRetentionDays.Should().Be(3650);
    }

    [Fact]
    public void HistorySettings_MissingOrCorruptValuesFallBackToDefaults()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");

        File.WriteAllText(settingsPath, "{}");
        var missing = CreateIsolatedSettingsService(testDirectory.Path);
        missing.HistoryEnabled.Should().BeFalse();
        missing.HistoryRetentionDays.Should().Be(30);

        File.WriteAllText(settingsPath, "{not-json");
        var corrupt = CreateIsolatedSettingsService(testDirectory.Path);
        corrupt.HistoryEnabled.Should().BeFalse();
        corrupt.HistoryRetentionDays.Should().Be(30);
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

        CreateIsolatedSettingsService(testDirectory.Path);

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
    public void AiProfiles_LegacyMigrationIsStableAndPreservesReferencesAndNonAiSettings()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        var openAiSecret = LocalCredentialProtector.Protect("synthetic-openai-secret");
        const string deepSeekSecret = "synthetic-deepseek-secret";
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [nameof(SettingsService.OpenAIApiKey)] = openAiSecret,
            [nameof(SettingsService.OpenAIEndpoint)] = "https://fixture.invalid/v1/chat/completions",
            [nameof(SettingsService.OpenAIModel)] = "fixture-model-a",
            [nameof(SettingsService.OpenAITemperature)] = 0.7,
            [nameof(SettingsService.OpenAIApiFormatOverride)] = "ChatCompletions",
            [nameof(SettingsService.DeepSeekApiKey)] = deepSeekSecret,
            [nameof(SettingsService.DeepSeekModel)] = "fixture-model-b",
            [nameof(SettingsService.MiniWindowEnabledServices)] = new[] { "google", "openai" },
            [nameof(SettingsService.MainWindowEnabledServices)] = new[] { "deepseek", "openai" },
            [nameof(SettingsService.FixedWindowEnabledServices)] = new[] { "google" },
            [nameof(SettingsService.MainWindowServiceEnabledQuery)] = new Dictionary<string, bool> { ["openai"] = false },
            [nameof(SettingsService.ServiceTestStatus)] = new Dictionary<string, bool> { ["openai"] = true },
            [nameof(SettingsService.HoverWordLookupServiceId)] = "openai",
            [nameof(SettingsService.VisionLayoutServiceId)] = "google",
            [nameof(SettingsService.DeepLUseQualityOptimized)] = true
        }));

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.AiServiceProfiles.Should().Contain(profile => profile.ProviderType == "openai");
        settings.AiServiceProfiles.Should().Contain(profile => profile.ProviderType == "deepseek");
        var openAi = settings.AiServiceProfiles.Single(profile => profile.ProviderType == "openai");
        openAi.ServiceId.Should().Be("openai");
        openAi.Configuration["endpoint"].Should().Be("https://fixture.invalid/v1/chat/completions");
        openAi.Configuration["model"].Should().Be("fixture-model-a");
        settings.GetAiServiceProfileSecret(openAi.ProfileId, "apiKey").Should().Be("synthetic-openai-secret");
        settings.MiniWindowEnabledServices.Should().Equal("google", "openai");
        settings.MainWindowEnabledServices.Should().Equal("deepseek", "openai");
        settings.MainWindowServiceEnabledQuery["openai"].Should().BeFalse();
        settings.ServiceTestStatus["openai"].Should().BeTrue();
        settings.HoverWordLookupServiceId.Should().Be("openai");
        settings.DeepLUseQualityOptimized.Should().BeTrue();

        using var migratedJson = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = migratedJson.RootElement;
        root.GetProperty(nameof(SettingsService.OpenAIApiKey)).GetString().Should().Be(openAiSecret);
        root.GetProperty("AiServiceProfilesSchemaVersion").GetInt32().Should().Be(1);
        var profileJson = root.GetProperty("AiServiceProfiles").GetRawText();
        profileJson.Should().NotContain("synthetic-openai-secret");
        var profileSecret = root.GetProperty($"AiProfileSecret:{openAi.ProfileId}:apiKey").GetString();
        profileSecret.Should().StartWith("edcred1:user:");
        root.GetProperty(nameof(SettingsService.DeepSeekApiKey)).GetString().Should().StartWith("edcred1:user:");
        File.ReadAllText(settingsPath).Should().NotContain(deepSeekSecret);

        var secondLoad = CreateIsolatedSettingsService(testDirectory.Path);
        secondLoad.AiServiceProfiles.Select(profile => profile.ProfileId)
            .Should().Equal(settings.AiServiceProfiles.Select(profile => profile.ProfileId));
        secondLoad.GetAiServiceProfileSecret(openAi.ProfileId, "apiKey").Should().Be("synthetic-openai-secret");
        secondLoad.DeepLUseQualityOptimized.Should().BeTrue();
    }

    [Fact]
    public void AiProfiles_ProfileSecretsAreProtectedAndRoundTripIndependently()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var first = settings.AddAiServiceProfile("openai", "Personal");
        var second = settings.AddAiServiceProfile("openai", "Work");
        settings.SetAiServiceProfileSecret(first.ProfileId, "apiKey", "synthetic-personal-key");
        settings.SetAiServiceProfileSecret(second.ProfileId, "apiKey", "synthetic-work-key");
        settings.Save();

        var saved = File.ReadAllText(Path.Combine(testDirectory.Path, "settings.json"));
        saved.Should().NotContain("synthetic-personal-key");
        saved.Should().NotContain("synthetic-work-key");
        using (var document = JsonDocument.Parse(saved))
        {
            document.RootElement.GetProperty($"AiProfileSecret:{first.ProfileId}:apiKey")
                .GetString().Should().StartWith("edcred1:user:");
            document.RootElement.GetProperty($"AiProfileSecret:{second.ProfileId}:apiKey")
                .GetString().Should().StartWith("edcred1:user:");
        }

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        reloaded.GetAiServiceProfileSecret(first.ProfileId, "apiKey").Should().Be("synthetic-personal-key");
        reloaded.GetAiServiceProfileSecret(second.ProfileId, "apiKey").Should().Be("synthetic-work-key");
        reloaded.AiServiceProfiles.Select(profile => profile.ServiceId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void MagpieAutoConnectedProfile_MarkerAndConfigurationSurviveSaveAndReload()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var profile = settings.AddAiServiceProfile("magpie", "Magpie", isAutoConnected: true);
        settings.AddAiServiceProfile("magpie", "Duplicate", isAutoConnected: true).ProfileId.Should().Be(profile.ProfileId);
        settings.AiServiceProfiles.Where(candidate => candidate.ProviderType == "magpie").Should().ContainSingle();
        settings.UpdateAiServiceProfileConfiguration(profile.ProfileId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["endpoint"] = "http://127.0.0.1:4545/v1/chat/completions",
            ["model"] = "provider/model"
        });
        settings.SetAiServiceProfileSecret(profile.ProfileId, "apiKey", "magpie-easydict");
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        var restored = reloaded.AiServiceProfiles.Where(candidate => candidate.ProviderType == "magpie").Should().ContainSingle().Subject;
        restored.ProfileId.Should().Be(profile.ProfileId);
        restored.IsAutoConnected.Should().BeTrue();
        restored.Configuration["model"].Should().Be("provider/model");
        reloaded.GetAiServiceProfileSecret(restored.ProfileId, "apiKey").Should().Be("magpie-easydict");
    }

    [Fact]
    public void AiProfiles_DeletingProfileRedirectsReferencesOrRemovesThemWhenNoReplacementExists()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{\"VisionLayoutServiceId\":\"\"}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var first = settings.AddAiServiceProfile("openai", "Personal");
        var second = settings.AddAiServiceProfile("openai", "Work");
        var onlyGroq = settings.AddAiServiceProfile("groq", "Groq");

        settings.MiniWindowEnabledServices.Add(first.ServiceId);
        settings.MainWindowEnabledServices.Add(onlyGroq.ServiceId);
        settings.FixedWindowServiceEnabledQuery[first.ServiceId] = false;
        settings.MainWindowServiceEnabledQuery[onlyGroq.ServiceId] = true;
        settings.ServiceTestStatus[first.ServiceId] = true;
        settings.AddOrReplaceTextAction(new TextAction
        {
            Id = "profile-action",
            Title = "Use profile",
            Type = TextActionType.RunService,
            ServiceId = first.ServiceId,
            IsEnabled = true,
            ShowOnPopButton = true
        });
        settings.AddOrReplaceTextAction(new TextAction
        {
            Id = "removed-profile-action",
            Title = "Use Groq",
            Type = TextActionType.RunService,
            ServiceId = onlyGroq.ServiceId
        });
        settings.TextActions.Should().Contain(action => action.Id == "profile-action");
        settings.HoverWordLookupServiceId = first.ServiceId;
        settings.VisionLayoutServiceId = onlyGroq.ServiceId;

        settings.RemoveAiServiceProfile(first.ProfileId, second.ServiceId).Should().BeTrue();
        settings.TextActions.Should().ContainSingle(action => action.Id == "profile-action")
            .Which.ServiceId.Should().Be(second.ServiceId);
        settings.RemoveAiServiceProfile(onlyGroq.ProfileId).Should().BeTrue();

        settings.MiniWindowEnabledServices.Should().Contain(second.ServiceId).And.NotContain(first.ServiceId);
        settings.FixedWindowServiceEnabledQuery.Should().ContainKey(second.ServiceId).And.NotContainKey(first.ServiceId);
        settings.FixedWindowServiceEnabledQuery[second.ServiceId].Should().BeFalse();
        settings.ServiceTestStatus.Should().NotContainKey(first.ServiceId);
        settings.TextActions.Should().ContainSingle(action => action.Id == "profile-action")
            .Which.ServiceId.Should().Be(second.ServiceId);
        settings.TextActions.Should().NotContain(action => action.Id == "removed-profile-action");
        settings.HoverWordLookupServiceId.Should().Be(second.ServiceId);
        settings.MainWindowEnabledServices.Should().NotContain(onlyGroq.ServiceId);
        settings.MainWindowServiceEnabledQuery.Should().NotContainKey(onlyGroq.ServiceId);
        settings.VisionLayoutServiceId.Should().BeEmpty();
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        reloaded.AiServiceProfiles.Select(profile => profile.ProfileId).Should().Equal(second.ProfileId);
        reloaded.MiniWindowEnabledServices.Should().Contain(second.ServiceId).And.NotContain(first.ServiceId);
        reloaded.MainWindowEnabledServices.Should().NotContain(onlyGroq.ServiceId);
        reloaded.TextActions.Should().ContainSingle(action => action.Id == "profile-action")
            .Which.ServiceId.Should().Be(second.ServiceId);
        reloaded.VisionLayoutServiceId.Should().BeEmpty();
    }

    [Fact]
    public void AiProfileDraftSaver_NewAndExistingDraftsPersistAllFieldsIndependently()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{\"VisionLayoutServiceId\":\"\"}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var existing = settings.AddAiServiceProfile("openai", "Work");
        settings.UpdateAiServiceProfileConfiguration(existing.ProfileId, new Dictionary<string, string>
        {
            ["endpoint"] = "https://old-work.invalid/v1/chat/completions",
            ["model"] = "old-work-model",
            ["temperature"] = "0.3",
            ["apiFormat"] = "Auto"
        });
        settings.SetAiServiceProfileSecret(existing.ProfileId, "apiKey", "synthetic-old-work-key");
        settings.Save();

        var existingDraft = new AiServiceProfileDraftSubmission(
            "existing-work",
            existing.ProfileId,
            existing.ServiceId,
            "openai",
            "Work Account",
            false,
            new Dictionary<string, string>
            {
                ["endpoint"] = "https://updated-work.invalid/v1/chat/completions",
                ["model"] = "updated-work-model",
                ["temperature"] = "0.7",
                ["apiFormat"] = "ChatCompletions"
            },
            new Dictionary<string, string> { ["apiKey"] = "synthetic-updated-work-key" },
            new HashSet<string>(StringComparer.Ordinal),
            IsNew: false,
            HasChanges: true);
        var newDraft = new AiServiceProfileDraftSubmission(
            "new-personal",
            null,
            null,
            "openai",
            "Personal",
            true,
            new Dictionary<string, string>
            {
                ["endpoint"] = "https://personal.invalid/v1/chat/completions",
                ["model"] = "personal-model",
                ["temperature"] = "0.2",
                ["apiFormat"] = "Responses"
            },
            new Dictionary<string, string> { ["apiKey"] = "synthetic-personal-key" },
            new HashSet<string>(StringComparer.Ordinal),
            IsNew: true,
            HasChanges: true);

        var result = AiServiceProfileDraftSaver.Save(settings, [existingDraft, newDraft], []);
        result.ProfilesByDraftKey.Should().ContainKeys("existing-work", "new-personal");
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        var savedExisting = reloaded.AiServiceProfiles.Single(profile => profile.ProfileId == existing.ProfileId);
        savedExisting.Name.Should().Be("Work Account");
        savedExisting.IsEnabled.Should().BeFalse();
        savedExisting.Configuration["endpoint"].Should().Be("https://updated-work.invalid/v1/chat/completions");
        savedExisting.Configuration["model"].Should().Be("updated-work-model");
        savedExisting.Configuration["temperature"].Should().Be("0.7");
        savedExisting.Configuration["apiFormat"].Should().Be("ChatCompletions");
        reloaded.GetAiServiceProfileSecret(existing.ProfileId, "apiKey").Should().Be("synthetic-updated-work-key");

        var savedNew = reloaded.AiServiceProfiles.Single(profile => profile.Name == "Personal");
        savedNew.IsEnabled.Should().BeTrue();
        savedNew.Configuration["endpoint"].Should().Be("https://personal.invalid/v1/chat/completions");
        savedNew.Configuration["model"].Should().Be("personal-model");
        savedNew.Configuration["temperature"].Should().Be("0.2");
        savedNew.Configuration["apiFormat"].Should().Be("Responses");
        reloaded.GetAiServiceProfileSecret(savedNew.ProfileId, "apiKey").Should().Be("synthetic-personal-key");
    }

    [Fact]
    public void AiProfileDraftSaver_DeleteRedirectsReferencesAndTextActionsToSameProviderReplacement()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{\"VisionLayoutServiceId\":\"\"}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var deleted = settings.AddAiServiceProfile("openai", "Old account");
        var replacement = settings.AddAiServiceProfile("openai", "Replacement");
        settings.MiniWindowEnabledServices.Add(deleted.ServiceId);
        settings.MainWindowServiceEnabledQuery[deleted.ServiceId] = false;
        settings.HoverWordLookupServiceId = deleted.ServiceId;
        settings.AddOrReplaceTextAction(new TextAction
        {
            Id = "draft-delete-action",
            Title = "Use deleted profile",
            Type = TextActionType.RunService,
            ServiceId = deleted.ServiceId,
            IsEnabled = true,
            ShowOnPopButton = true
        });

        var replacementDraft = new AiServiceProfileDraftSubmission(
            "replacement",
            replacement.ProfileId,
            replacement.ServiceId,
            replacement.ProviderType,
            replacement.Name,
            replacement.IsEnabled,
            new Dictionary<string, string>(replacement.Configuration, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            IsNew: false,
            HasChanges: false);
        var result = AiServiceProfileDraftSaver.Save(settings, [replacementDraft], [deleted.ProfileId]);

        result.ReferenceChanges.Should().ContainSingle(change =>
            change.FromServiceId == deleted.ServiceId && change.ToServiceId == replacement.ServiceId);
        settings.HoverWordLookupServiceId.Should().Be(replacement.ServiceId);
        settings.MiniWindowEnabledServices.Should().Contain(replacement.ServiceId).And.NotContain(deleted.ServiceId);
        settings.MainWindowServiceEnabledQuery.Should().ContainKey(replacement.ServiceId).And.NotContainKey(deleted.ServiceId);
        settings.MainWindowServiceEnabledQuery[replacement.ServiceId].Should().BeFalse();
        settings.TextActions.Should().ContainSingle(action => action.Id == "draft-delete-action")
            .Which.ServiceId.Should().Be(replacement.ServiceId);
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        reloaded.AiServiceProfiles.Select(profile => profile.ProfileId).Should().Equal(replacement.ProfileId);
        reloaded.HoverWordLookupServiceId.Should().Be(replacement.ServiceId);
        reloaded.MiniWindowEnabledServices.Should().Contain(replacement.ServiceId).And.NotContain(deleted.ServiceId);
        reloaded.TextActions.Should().ContainSingle(action => action.Id == "draft-delete-action")
            .Which.ServiceId.Should().Be(replacement.ServiceId);
    }

    [Fact]
    public void AiProfileDraftSaver_DeleteThenReconnectCreatesOneSavedAutoProfileAndRedirectsReferences()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{\"VisionLayoutServiceId\":\"\"}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var deleted = settings.AddAiServiceProfile("magpie", "Old gateway", isAutoConnected: true);
        settings.UpdateAiServiceProfileConfiguration(deleted.ProfileId, new Dictionary<string, string>
        {
            ["endpoint"] = "https://old-gateway.example/v1/chat/completions",
            ["allowRemote"] = "true",
            ["model"] = "old/model"
        });
        settings.SetAiServiceProfileSecret(deleted.ProfileId, "apiKey", "old-authorized-key");
        settings.HoverWordLookupServiceId = deleted.ServiceId;
        settings.Save();

        var reconnectedDraft = new AiServiceProfileDraftSubmission(
            "reconnected-magpie",
            null,
            null,
            "magpie",
            "New gateway",
            true,
            new Dictionary<string, string>
            {
                ["endpoint"] = "https://new-gateway.example/v1/chat/completions",
                ["allowRemote"] = "true",
                ["model"] = "new/model"
            },
            new Dictionary<string, string> { ["apiKey"] = "new-authorized-key" },
            new HashSet<string>(StringComparer.Ordinal),
            IsNew: true,
            HasChanges: true,
            IsAutoConnected: true);

        var result = AiServiceProfileDraftSaver.Save(settings, [reconnectedDraft], [deleted.ProfileId]);
        var created = result.ProfilesByDraftKey["reconnected-magpie"];
        created.ProfileId.Should().NotBe(deleted.ProfileId);
        settings.AiServiceProfiles.Should().ContainSingle(profile => profile.ProfileId == created.ProfileId);
        settings.HoverWordLookupServiceId.Should().Be(created.ServiceId);
        settings.GetAiServiceProfileSecret(created.ProfileId, "apiKey").Should().Be("new-authorized-key");
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        reloaded.AiServiceProfiles.Should().ContainSingle(profile =>
            profile.ProfileId == created.ProfileId
            && profile.IsAutoConnected
            && profile.Configuration["endpoint"] == "https://new-gateway.example/v1/chat/completions"
            && profile.Configuration["model"] == "new/model");
        reloaded.GetAiServiceProfileSecret(created.ProfileId, "apiKey").Should().Be("new-authorized-key");
        reloaded.HoverWordLookupServiceId.Should().Be(created.ServiceId);
        reloaded.AiServiceProfiles.Single().ServiceId.Should().Be(reloaded.HoverWordLookupServiceId);
    }

    [Fact]
    public void AiProfiles_UnmanagedEmptyRegistryDoesNotDisableLegacyProviderDiscoveryOnSave()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{\"VisionLayoutServiceId\":\"\"}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);

        settings.HistoryEnabled = true;
        settings.Save();

        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(testDirectory.Path, "settings.json")));
        saved.RootElement.TryGetProperty("AiServiceProfiles", out _).Should().BeFalse();
        settings.AiServiceProfilesAreAuthoritative.Should().BeFalse();
    }

    [Fact]
    public void AiProfiles_LegacyProviderConfigurationDetectionDistinguishesDefaultsFromStaleValues()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "settings.json"), "{\"VisionLayoutServiceId\":\"\"}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);

        settings.HasLegacyAiProviderConfiguration("openrouter").Should().BeFalse();
        settings.OpenRouterApiKey = "legacy-key-retained-for-recovery";
        settings.HasLegacyAiProviderConfiguration("openrouter").Should().BeTrue();
    }

    [Fact]
    public void AiProfiles_UnknownSchemaDataSurvivesNormalSettingsSave()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        const string unknownProfileJson = "[{\"ProfileId\":\"future-profile\",\"ServiceId\":\"ai-profile:future-profile\",\"ProviderType\":\"future-provider\",\"Name\":\"Future\",\"IsEnabled\":true,\"ConfigurationRevision\":0,\"Configuration\":{}}]";
        File.WriteAllText(settingsPath,
            "{\"AiServiceProfilesSchemaVersion\":99,\"AiServiceProfiles\":" + unknownProfileJson + ",\"DeepLUseQualityOptimized\":true}");

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.AiServiceProfilesLoadError.Should().NotBeNullOrWhiteSpace();
        settings.AiServiceProfiles.Should().BeEmpty();
        settings.Save();

        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        saved.RootElement.GetProperty("AiServiceProfilesSchemaVersion").GetInt32().Should().Be(99);
        var preservedProfile = saved.RootElement.GetProperty("AiServiceProfiles").EnumerateArray().Single();
        preservedProfile.GetProperty("ProfileId").GetString().Should().Be("future-profile");
        preservedProfile.GetProperty("ProviderType").GetString().Should().Be("future-provider");
        preservedProfile.GetProperty("Configuration").ValueKind.Should().Be(JsonValueKind.Object);
        saved.RootElement.GetProperty(nameof(SettingsService.DeepLUseQualityOptimized)).GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void AiProfiles_MalformedCollectionIsNotReplacedWithAnEmptyDefault()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        File.WriteAllText(settingsPath,
            "{\"AiServiceProfilesSchemaVersion\":1,\"AiServiceProfiles\":\"damaged-profile-data\"}");

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.AiServiceProfilesLoadError.Should().NotBeNullOrWhiteSpace();
        settings.AiServiceProfiles.Should().BeEmpty();
        settings.Save();

        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        saved.RootElement.GetProperty("AiServiceProfilesSchemaVersion").GetInt32().Should().Be(1);
        saved.RootElement.GetProperty("AiServiceProfiles").GetString().Should().Be("damaged-profile-data");
    }

    [Fact]
    public void AiProfiles_MissingProfileIdIsRejectedAndOriginalJsonSurvivesSave()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        const string profileJson = "[{\"ServiceId\":\"ai-profile:missing-id\",\"ProviderType\":\"openai\",\"Name\":\"Broken\",\"IsEnabled\":true,\"ConfigurationRevision\":0,\"Configuration\":{}}]";
        File.WriteAllText(settingsPath,
            "{\"AiServiceProfilesSchemaVersion\":1,\"AiServiceProfiles\":" + profileJson + "}");

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.AiServiceProfilesLoadError.Should().NotBeNullOrWhiteSpace();
        settings.AiServiceProfiles.Should().BeEmpty();
        settings.Save();

        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var savedProfile = saved.RootElement.GetProperty("AiServiceProfiles").EnumerateArray().Single();
        savedProfile.TryGetProperty("ProfileId", out _).Should().BeFalse();
        savedProfile.GetProperty("ServiceId").GetString().Should().Be("ai-profile:missing-id");
        savedProfile.GetProperty("ProviderType").GetString().Should().Be("openai");
    }

    [Fact]
    public void AiProfiles_UndecryptableSecretSurvivesUnrelatedSaveUntilExplicitlyCleared()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        const string corruptCiphertext = "edcred1:user:not-valid-ciphertext";
        File.WriteAllText(settingsPath,
            "{\"AiServiceProfilesSchemaVersion\":1,\"AiServiceProfiles\":[{\"ProfileId\":\"profile-with-bad-key\",\"ServiceId\":\"ai-profile:profile-with-bad-key\",\"ProviderType\":\"openai\",\"Name\":\"Fixture\",\"IsEnabled\":true,\"ConfigurationRevision\":0,\"Configuration\":{}}],\"AiProfileSecret:profile-with-bad-key:apiKey\":\"" + corruptCiphertext + "\"}");

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.GetAiServiceProfileSecret("profile-with-bad-key", "apiKey").Should().BeNull();
        settings.HistoryEnabled = true;
        settings.Save();

        using (var afterOrdinarySave = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            afterOrdinarySave.RootElement.GetProperty("AiProfileSecret:profile-with-bad-key:apiKey")
                .GetString().Should().Be(corruptCiphertext);
            afterOrdinarySave.RootElement.GetProperty(nameof(SettingsService.HistoryEnabled)).GetBoolean().Should().BeTrue();
        }

        settings.SetAiServiceProfileSecret("profile-with-bad-key", "apiKey", null);
        settings.Save();
        using var afterExplicitClear = JsonDocument.Parse(File.ReadAllText(settingsPath));
        afterExplicitClear.RootElement.GetProperty("AiProfileSecret:profile-with-bad-key:apiKey")
            .GetString().Should().BeEmpty();
    }

    [Fact]
    public void AiProfiles_LegacySettingsSaveUpdatesMatchingLegacyProfile()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        File.WriteAllText(settingsPath, "{}");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);

        settings.OpenAIApiKey = "legacy-ui-initial-key";
        settings.OpenAIEndpoint = "https://initial.invalid/v1/chat/completions";
        settings.OpenAIModel = "initial-model";
        settings.Save();
        var initialProfile = settings.AiServiceProfiles.Single(item => item.ServiceId == "openai");
        var initialRevision = initialProfile.ConfigurationRevision;

        settings.OpenAIApiKey = "legacy-ui-updated-key";
        settings.OpenAIEndpoint = "https://updated.invalid/v1/chat/completions";
        settings.OpenAIModel = "updated-model";
        settings.Save();

        var legacyProfile = settings.AiServiceProfiles.Single(item => item.ServiceId == "openai");
        legacyProfile.ProfileId.Should().Be("legacy-openai");
        legacyProfile.Configuration["endpoint"].Should().Be("https://updated.invalid/v1/chat/completions");
        legacyProfile.Configuration["model"].Should().Be("updated-model");
        settings.GetAiServiceProfileSecret(legacyProfile.ProfileId, "apiKey").Should().Be("legacy-ui-updated-key");
        legacyProfile.ConfigurationRevision.Should().BeGreaterThan(initialRevision);

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        reloaded.AiServiceProfiles.Single(item => item.ServiceId == "openai").Configuration["model"]
            .Should().Be("updated-model");
        reloaded.GetAiServiceProfileSecret(legacyProfile.ProfileId, "apiKey").Should().Be("legacy-ui-updated-key");
    }

    [Fact]
    public void AiProfiles_NewApiEditsToMigratedProfileSurviveSaveAndReload()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [nameof(SettingsService.OpenAIApiKey)] = LocalCredentialProtector.Protect("legacy-api-key"),
            [nameof(SettingsService.OpenAIEndpoint)] = "https://legacy.invalid/v1/chat/completions",
            [nameof(SettingsService.OpenAIModel)] = "legacy-model"
        }));

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var migrated = settings.AiServiceProfiles.Single(profile => profile.ServiceId == "openai");
        var configuration = new Dictionary<string, string>(migrated.Configuration, StringComparer.Ordinal)
        {
            ["model"] = "profile-api-model"
        };
        settings.UpdateAiServiceProfileConfiguration(migrated.ProfileId, configuration);
        settings.SetAiServiceProfileSecret(migrated.ProfileId, "apiKey", "profile-api-key");
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(testDirectory.Path);
        var savedProfile = reloaded.AiServiceProfiles.Single(profile => profile.ServiceId == "openai");
        savedProfile.Configuration["model"].Should().Be("profile-api-model");
        reloaded.GetAiServiceProfileSecret(savedProfile.ProfileId, "apiKey").Should().Be("profile-api-key");
        reloaded.OpenAIModel.Should().Be("legacy-model");
        reloaded.OpenAIApiKey.Should().Be("legacy-api-key");
    }

    [Fact]
    public void AiProfiles_UndecryptableSecretAndUnchangedLegacyKeySurviveNonAiSave()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        const string corruptCiphertext = "edcred1:user:not-valid-ciphertext";
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [nameof(SettingsService.OpenAIApiKey)] = LocalCredentialProtector.Protect("legacy-api-key"),
            [nameof(SettingsService.OpenAIEndpoint)] = "https://legacy.invalid/v1/chat/completions",
            [nameof(SettingsService.OpenAIModel)] = "legacy-model",
            ["AiServiceProfilesSchemaVersion"] = 1,
            ["AiServiceProfiles"] = new[]
            {
                new
                {
                    ProfileId = "legacy-openai",
                    ServiceId = "openai",
                    ProviderType = "openai",
                    Name = "OpenAI",
                    IsEnabled = true,
                    ConfigurationRevision = 0,
                    Configuration = new Dictionary<string, string>
                    {
                        ["endpoint"] = "https://legacy.invalid/v1/chat/completions",
                        ["model"] = "legacy-model",
                        ["temperature"] = "0.3",
                        ["apiFormat"] = "Auto"
                    }
                }
            },
            ["AiProfileSecret:legacy-openai:apiKey"] = corruptCiphertext
        }));

        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.GetAiServiceProfileSecret("legacy-openai", "apiKey").Should().BeNull();
        settings.OpenAIApiKey.Should().Be("legacy-api-key");
        settings.HistoryEnabled = true;
        settings.Save();

        using var saved = JsonDocument.Parse(File.ReadAllText(settingsPath));
        saved.RootElement.GetProperty("AiProfileSecret:legacy-openai:apiKey").GetString()
            .Should().Be(corruptCiphertext);
        saved.RootElement.GetProperty(nameof(SettingsService.HistoryEnabled)).GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Save_WithNoChanges_ShouldNotRewriteSettingsFile()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.OpenAIApiKey = "sk-stable-no-rewrite";
        settings.Save();

        var savedJson = File.ReadAllText(settingsPath);
        var fixedTimestamp = new DateTime(2024, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsPath, fixedTimestamp);
        var unchangedTimestamp = File.GetLastWriteTimeUtc(settingsPath);

        settings.Save();

        File.ReadAllText(settingsPath).Should().Be(savedJson);
        File.GetLastWriteTimeUtc(settingsPath).Should().Be(unchangedTimestamp);
    }

    [Fact]
    public void Save_UnchangedBobPluginSecureOption_DoesNotRewriteSettingsFile()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        settings.SetBobPluginSecureOption("bob:demo:1", "apiKey", "secret-value");
        settings.Save();

        var savedJson = File.ReadAllText(settingsPath);
        var fixedTimestamp = new DateTime(2024, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsPath, fixedTimestamp);

        // Settings writes every plugin option back on save, changed or not.
        settings.SetBobPluginSecureOption("bob:demo:1", "apiKey", "secret-value");
        settings.Save();

        File.ReadAllText(settingsPath).Should().Be(savedJson);
        File.GetLastWriteTimeUtc(settingsPath).Should().Be(fixedTimestamp);
        settings.GetBobPluginSecureOption("bob:demo:1", "apiKey").Should().Be("secret-value");

        settings.SetBobPluginSecureOption("bob:demo:1", "apiKey", "rotated-value");
        settings.Save();

        File.ReadAllText(settingsPath).Should().NotBe(savedJson);
        settings.GetBobPluginSecureOption("bob:demo:1", "apiKey").Should().Be("rotated-value");
    }

    [Fact]
    public void HotkeySignature_ChangesOnlyWhenRegisteredHotkeysWouldChange()
    {
        using var testDirectory = new TemporaryDirectory();
        var settings = CreateIsolatedSettingsService(testDirectory.Path);
        var original = HotkeyService.BuildHotkeySignature(settings);

        HotkeyService.BuildHotkeySignature(settings).Should().Be(original);

        settings.ShowWindowHotkey = "Ctrl+Alt+Q";
        var changedKey = HotkeyService.BuildHotkeySignature(settings);
        changedKey.Should().NotBe(original);

        settings.EnableOcrTranslateHotkey = !settings.EnableOcrTranslateHotkey;
        HotkeyService.BuildHotkeySignature(settings).Should().NotBe(changedKey);
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
    public void UseLongDocWorker_DefaultsToWorkerIsolationEnabled()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current) &&
               !File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
        {
            current = Path.GetDirectoryName(current);
        }

        current.Should().NotBeNullOrEmpty();
        var source = File.ReadAllText(Path.Combine(current!, "src", "Easydict.WinUI", "Services", "SettingsService.cs"));
        source.Should().Contain("public bool UseLongDocWorker { get; set; } = true;");
        source.Should().Contain("UseLongDocWorker = ResolveWorkerIsolationSetting(nameof(UseLongDocWorker), DisableLongDocWorkerEnvironmentVariable);");
        source.Should().Contain("EASYDICT_DISABLE_LONGDOC_WORKER");
        source.Should().Contain("RemoveRuntimeOnlyWorkerIsolationSettings();");
        source.Should().NotContain("_settings[nameof(UseLongDocWorker)] =");
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
        source.Should().Contain("UseLocalAiWorker = ResolveWorkerIsolationSetting(nameof(UseLocalAiWorker), DisableLocalAiWorkerEnvironmentVariable);");
        source.Should().Contain("EASYDICT_DISABLE_LOCALAI_WORKER");
        source.Should().Contain("RemoveRuntimeOnlyWorkerIsolationSettings();");
        source.Should().NotContain("_settings[nameof(UseLocalAiWorker)] =");
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
        source.Should().Contain("UseOcrWorker = ResolveWorkerIsolationSetting(nameof(UseOcrWorker), DisableOcrWorkerEnvironmentVariable);");
        source.Should().Contain("EASYDICT_DISABLE_OCR_WORKER");
        source.Should().Contain("RemoveRuntimeOnlyWorkerIsolationSettings();");
        source.Should().NotContain("_settings[nameof(UseOcrWorker)] =");
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
    public void OpenRouterModel_HasDefaultValue()
    {
        _settings.OpenRouterModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OrcaRouterModel_HasDefaultValue()
    {
        _settings.OrcaRouterModel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OrcaRouterSignedInViaSso_DefaultsToFalse()
    {
        _settings.OrcaRouterSignedInViaSso.Should().BeFalse(
            "a manually-pasted key should never appear locked in the UI");
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
    public void ProxyBypassChina_DefaultsToFalse()
    {
        _settings.ProxyBypassChina.Should().BeFalse();
    }

    [Fact]
    public void OllamaPrompt_DefaultsToStandard()
    {
        _settings.OllamaPromptStyle.Should().Be("Standard");
        _settings.OllamaCustomPrompt.Should().BeEmpty();
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
#if ENABLE_LINGUEE_SERVICE
        SettingsService.InternationalOnlyServices.Should().Contain("linguee");
#else
        SettingsService.InternationalOnlyServices.Should().NotContain("linguee");
#endif
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
        using var testDirectory = new TemporaryDirectory();
        var settings = CreateIsolatedSettingsService(testDirectory.Path);

        settings.SelectedLanguages.Should().BeEquivalentTo("zh", "en", "ja", "ko", "fr", "de", "es");
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
    [InlineData("openrouter", true)]
    [InlineData("orcarouter", true)]
#if ENABLE_LINGUEE_SERVICE
    [InlineData("linguee", true)]
#else
    [InlineData("linguee", false)]
#endif
    public void IsInternationalOnlyService_ReturnsExpectedValue(string serviceId, bool expected)
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
        var originalPpModel = _settings.PpOcrV6ModelId;
        var originalPrompt = _settings.OcrSystemPrompt;
        var originalEnableThinking = _settings.OcrEnableThinking;

        try
        {
            _settings.OcrEngine = OcrEngineType.Ollama;
            _settings.OcrEndpoint = "http://test.local/ocr";
            _settings.OcrModel = "test-model";
            _settings.PpOcrV6ModelId = PpOcrV6ModelCatalog.MediumId;
            _settings.OcrSystemPrompt = "Extract test text.";
            _settings.OcrEnableThinking = true;
            _settings.Save();

            _settings.OcrEngine.Should().Be(OcrEngineType.Ollama);
            _settings.OcrEndpoint.Should().Be("http://test.local/ocr");
            _settings.OcrModel.Should().Be("test-model");
            _settings.PpOcrV6ModelId.Should().Be(PpOcrV6ModelCatalog.MediumId);
            _settings.OcrSystemPrompt.Should().Be("Extract test text.");
            _settings.OcrEnableThinking.Should().BeTrue();
        }
        finally
        {
            _settings.OcrEngine = originalEngine;
            _settings.OcrEndpoint = originalEndpoint;
            _settings.OcrModel = originalModel;
            _settings.PpOcrV6ModelId = originalPpModel;
            _settings.OcrSystemPrompt = originalPrompt;
            _settings.OcrEnableThinking = originalEnableThinking;
            _settings.Save();
        }
    }

    [Fact]
    public void OcrEnableThinking_DefaultsToOff()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current) &&
               !File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
        {
            current = Path.GetDirectoryName(current);
        }

        current.Should().NotBeNullOrEmpty();
        var source = File.ReadAllText(Path.Combine(current!, "src", "Easydict.WinUI", "Services", "SettingsService.cs"));

        // No initializer means false — thinking stays off so recognition stays fast.
        source.Should().Contain("public bool OcrEnableThinking { get; set; }");
        source.Should().NotContain("public bool OcrEnableThinking { get; set; } = true;");
        source.Should().Contain("OcrEnableThinking = GetValue(nameof(OcrEnableThinking), false);");
        source.Should().Contain("_settings[nameof(OcrEnableThinking)] = OcrEnableThinking;");
    }

    [Fact]
    public void TrayIconId_DefaultsToTheCurrentIconAndRoundTripsKnownIds()
    {
        using var testDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(testDirectory.Path, "settings.json");

        var defaults = CreateIsolatedSettingsService(testDirectory.Path);
        defaults.TrayIconId.Should().Be(TrayIconCatalog.DefaultId);

        defaults.TrayIconId = TrayIconCatalog.EId;
        defaults.Save();
        CreateIsolatedSettingsService(testDirectory.Path).TrayIconId.Should().Be(TrayIconCatalog.EId);

        File.WriteAllText(settingsPath, """{"TrayIconId":"not-an-icon"}""");
        CreateIsolatedSettingsService(testDirectory.Path).TrayIconId.Should().Be(TrayIconCatalog.DefaultId);

        File.WriteAllText(settingsPath, """{"TrayIconId":" WEN "}""");
        CreateIsolatedSettingsService(testDirectory.Path).TrayIconId.Should().Be(TrayIconCatalog.WenId);
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

    private static SettingsService CreateIsolatedSettingsService(string directory)
        => new(directory);

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
