using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.Workers;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public sealed class AiProfileTranslationServiceFactoryTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/api/tags")]
    [InlineData("https://ollama.example/proxy/v1/chat/completions", "https://ollama.example/proxy/api/tags")]
    public async Task OllamaProfilePreservesModelListingCapability(string endpoint, string expected)
    {
        var profile = new AiServiceProfile
        {
            ProfileId = Guid.NewGuid().ToString("N"),
            ServiceId = $"ai-profile:{Guid.NewGuid():N}",
            ProviderType = "ollama",
            Name = "Ollama test",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["endpoint"] = endpoint, ["model"] = "selected-model"
            }
        };
        using var handler = new OllamaCatalogHandler();
        using var client = new HttpClient(handler);
        var service = AiProfileTranslationServiceFactory.Create(profile, _ => null, client);

        AiProviderCatalog.All.Single(provider => provider.Id == "ollama").SupportsModelListing.Should().BeTrue();
        var catalog = service.Should().BeAssignableTo<IModelCatalogProvider>().Which;
        service.Should().BeAssignableTo<IStreamTranslationService>();
        service.Should().BeAssignableTo<IGrammarCorrectionService>();
        service.ServiceId.Should().Be(profile.ServiceId);

        var models = await catalog.FetchModelsAsync();

        catalog.ModelsEndpoint.Should().Be(expected);
        handler.RequestedEndpoints.Should().BeEquivalentTo(expected,
            expected.Replace("/api/tags", "/api/experimental/model-recommendations", StringComparison.Ordinal));
        models.Select(model => model.Id).Should().Equal("qwen3:8b", "gpt-oss:cloud");
        models[1].Name.Should().Contain("recommended");
    }

    private sealed class OllamaCatalogHandler : HttpMessageHandler
    {
        public List<string> RequestedEndpoints { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedEndpoints.Add(request.RequestUri!.AbsoluteUri);
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.Authorization.Should().BeNull();
            request.RequestUri.Host.Should().NotBe("ollama.com", "a local server with recommendations must not need the public catalog");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/model-recommendations", StringComparison.Ordinal)
                    ? """{"recommendations":[{"model":"gpt-oss:cloud","context_length":131072}]}"""
                    : """{"models":[{"name":"qwen3:8b"}]}""", Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    public void AgentCliProvidersRequireAcknowledgmentAndStartDisabled(string providerType)
    {
        var provider = AiProviderCatalog.All.Single(item => item.Id == providerType);

        provider.RequiresAgentCliAcknowledgment.Should().BeTrue();
        provider.NewProfilesEnabledByDefault.Should().BeFalse();
    }

    [Fact]
    public async Task SavedLocalMagpieProfileUsesItsModelWithoutAnUpstreamProviderKey()
    {
        var profile = new AiServiceProfile
        {
            ProfileId = Guid.NewGuid().ToString("N"),
            ServiceId = $"ai-profile:{Guid.NewGuid():N}",
            ProviderType = "magpie",
            Name = "Local Magpie",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["endpoint"] = "http://127.0.0.1:4545/v1/chat/completions",
                ["model"] = "openai/gpt-6-luna",
                ["allowRemote"] = "false"
            }
        };

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = AiProfileTranslationServiceFactory.Create(profile, _ => null, client);
        var chunks = await ReadAllAsync((IStreamTranslationService)service, new TranslationRequest
        {
            Text = "fixture input",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true
        });

        service.IsConfigured.Should().BeTrue();
        handler.Requests.Should().ContainSingle(request =>
            request.Endpoint == "http://127.0.0.1:4545/v1/chat/completions"
            && request.Model == "openai/gpt-6-luna"
            && request.ApiKey == null);
        chunks.Should().Contain("openai/gpt-6-luna-result");
    }

    [Fact]
    public async Task SameProviderProfilesUseIndependentEndpointKeyModelAndIdentity()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, "{}");
        var settings = CreateSettings(directory.Path);

        var first = CreateOpenAiProfile(settings, "Personal", "https://personal.invalid/v1/chat/completions", "personal-model", "synthetic-personal-key");
        var second = CreateOpenAiProfile(settings, "Work", "https://work.invalid/v1/chat/completions", "work-model", "synthetic-work-key");

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var firstService = AiProfileTranslationServiceFactory.Create(first, settings, client);
        var secondService = AiProfileTranslationServiceFactory.Create(second, settings, client);
        firstService.ServiceId.Should().NotBe(secondService.ServiceId);
        firstService.DisplayName.Should().Be("Personal");
        secondService.DisplayName.Should().Be("Work");
        ((ICacheKeyDiscriminatorProvider)firstService).CacheKeyDiscriminator
            .Should().NotBe(((ICacheKeyDiscriminatorProvider)secondService).CacheKeyDiscriminator);

        var request = new TranslationRequest
        {
            Text = "fixture input",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true
        };
        var results = await Task.WhenAll(
            ReadAllAsync((IStreamTranslationService)firstService, request),
            ReadAllAsync((IStreamTranslationService)secondService, request));

        results[0].Should().Contain("personal-model-result");
        results[1].Should().Contain("work-model-result");
        var requests = handler.Requests.OrderBy(item => item.Endpoint, StringComparer.Ordinal).ToArray();
        requests.Should().HaveCount(2);
        requests.Single(item => item.Model == "personal-model").ApiKey.Should().Be("synthetic-personal-key");
        requests.Single(item => item.Model == "personal-model").Endpoint.Should().Be("https://personal.invalid/v1/chat/completions");
        requests.Single(item => item.Model == "work-model").ApiKey.Should().Be("synthetic-work-key");
        requests.Single(item => item.Model == "work-model").Endpoint.Should().Be("https://work.invalid/v1/chat/completions");
    }

    [Fact]
    public async Task EditingProfileConfigurationChangesCacheDiscriminatorAndResult()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{}");
        var settings = CreateSettings(directory.Path);
        var profile = CreateOpenAiProfile(
            settings,
            "Personal",
            "https://personal.invalid/v1/chat/completions",
            "model-before-edit",
            "synthetic-key");

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var manager = new TranslationManager();
        var beforeEdit = AiProfileTranslationServiceFactory.Create(profile, settings, client);
        manager.RegisterService(beforeEdit);
        var request = new TranslationRequest
        {
            Text = "same input",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var first = await manager.TranslateAsync(request, serviceId: profile.ServiceId);
        var configuration = new Dictionary<string, string>(profile.Configuration, StringComparer.Ordinal)
        {
            ["model"] = "model-after-edit"
        };
        var edited = settings.UpdateAiServiceProfileConfiguration(profile.ProfileId, configuration);
        edited.ConfigurationRevision.Should().BeGreaterThan(profile.ConfigurationRevision);
        var afterEdit = AiProfileTranslationServiceFactory.Create(edited, settings, client);
        ((ICacheKeyDiscriminatorProvider)afterEdit).CacheKeyDiscriminator
            .Should().NotBe(((ICacheKeyDiscriminatorProvider)beforeEdit).CacheKeyDiscriminator);
        manager.RegisterService(afterEdit);

        var second = await manager.TranslateAsync(request, serviceId: edited.ServiceId);
        first.TranslatedText.Should().Be("model-before-edit-result");
        second.TranslatedText.Should().Be("model-after-edit-result");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProfileStreamingForwardsCancellation()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{}");
        var settings = CreateSettings(directory.Path);
        var profile = CreateOpenAiProfile(
            settings,
            "Personal",
            "https://personal.invalid/v1/chat/completions",
            "model",
            "synthetic-key");
        using var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        var service = (IStreamTranslationService)AiProfileTranslationServiceFactory.Create(profile, settings, client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ReadAllAsync(service, new TranslationRequest
            {
                Text = "cancel me",
                FromLanguage = Language.English,
                ToLanguage = Language.SimplifiedChinese,
                BypassCache = true
            }, cancellation.Token));
    }

    [Fact]
    public async Task DisabledProfileRejectsTranslationAndCorrectionWithoutHttpRequests()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{}");
        var settings = CreateSettings(directory.Path);
        var profile = CreateOpenAiProfile(
            settings,
            "Disabled account",
            "https://disabled.invalid/v1/chat/completions",
            "model",
            "synthetic-key");
        settings.UpdateAiServiceProfileDetails(profile.ProfileId, profile.Name, isEnabled: false);
        profile = settings.AiServiceProfiles.Single(candidate => candidate.ProfileId == profile.ProfileId);

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = AiProfileTranslationServiceFactory.Create(profile, settings, client);
        using var manager = new TranslationManager();
        manager.RegisterService(service);
        var request = new TranslationRequest
        {
            Text = "must not be sent",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true
        };

        var translationError = await Assert.ThrowsAsync<TranslationException>(
            () => manager.TranslateAsync(request, serviceId: profile.ServiceId));
        translationError.ErrorCode.Should().Be(TranslationErrorCode.ServiceDisabled);
        translationError.ServiceId.Should().Be(profile.ServiceId);

        var streaming = (IStreamTranslationService)service;
        await Assert.ThrowsAsync<TranslationException>(async () =>
            await ReadAllAsync(streaming, request));

        var grammar = (IGrammarCorrectionService)service;
        await Assert.ThrowsAsync<TranslationException>(async () =>
            await ReadAllAsync(grammar.CorrectGrammarStreamAsync(new GrammarCorrectionRequest
            {
                Text = "must not be sent",
                Language = Language.English
            })));

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void LongDocSnapshotCarriesProfileIdentityAndSecretButOtherWorkersDoNot()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{}");
        var settings = CreateSettings(directory.Path);
        var profile = CreateOpenAiProfile(
            settings,
            "Long doc account",
            "https://longdoc.invalid/v1/chat/completions",
            "longdoc-model",
            "synthetic-longdoc-key");

        var localAiSnapshot = WorkerSpawner.BuildSnapshot(settings);
        localAiSnapshot.AiServiceProfiles.Should().BeNull();

        var longDocSnapshot = WorkerSpawner.BuildSnapshot(settings, includeAiServiceProfiles: true);
        var snapshotProfile = longDocSnapshot.AiServiceProfiles
            .Should().ContainSingle(candidate => candidate.ServiceId == profile.ServiceId).Subject;
        snapshotProfile.ServiceId.Should().Be(profile.ServiceId);
        snapshotProfile.Name.Should().Be("Long doc account");
        snapshotProfile.Configuration["endpoint"].Should().Be("https://longdoc.invalid/v1/chat/completions");
        snapshotProfile.Configuration["model"].Should().Be("longdoc-model");
        snapshotProfile.Secrets["apiKey"].Should().Be("synthetic-longdoc-key");
    }

    private static AiServiceProfile CreateOpenAiProfile(
        SettingsService settings,
        string name,
        string endpoint,
        string model,
        string apiKey)
    {
        var profile = settings.AddAiServiceProfile("openai", name);
        var configuration = new Dictionary<string, string>(profile.Configuration, StringComparer.Ordinal)
        {
            ["endpoint"] = endpoint,
            ["model"] = model,
            ["apiFormat"] = "ChatCompletions"
        };
        profile = settings.UpdateAiServiceProfileConfiguration(profile.ProfileId, configuration);
        settings.SetAiServiceProfileSecret(profile.ProfileId, "apiKey", apiKey);
        return settings.AiServiceProfiles.Single(candidate => candidate.ProfileId == profile.ProfileId);
    }

    private static async Task<List<string>> ReadAllAsync(
        IStreamTranslationService service,
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<string>();
        await foreach (var chunk in service.TranslateStreamAsync(request, cancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async Task<List<string>> ReadAllAsync(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static SettingsService CreateSettings(string directory)
        => new(directory);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var model = document.RootElement.GetProperty("model").GetString()!;
            Requests.Enqueue(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Parameter,
                model));

            var content = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{model}-result\"}}}}]}}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed record CapturedRequest(string Endpoint, string? ApiKey, string Model);

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Easydict.AiProfile.Tests",
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
                // Windows may briefly hold a settings-file handle while finalizers run.
            }
        }
    }
}
