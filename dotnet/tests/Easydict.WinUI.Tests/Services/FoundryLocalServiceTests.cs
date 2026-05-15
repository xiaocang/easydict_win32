using System.ComponentModel;
using System.Net;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class FoundryLocalServiceTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5273", "http://127.0.0.1:5273/v1/chat/completions")]
    [InlineData("http://127.0.0.1:5273/v1", "http://127.0.0.1:5273/v1/chat/completions")]
    [InlineData("http://127.0.0.1:5273/v1/chat/completions", "http://127.0.0.1:5273/v1/chat/completions")]
    [InlineData("http://127.0.0.1:5273/openai/status", "http://127.0.0.1:5273/v1/chat/completions")]
    public void NormalizeChatCompletionsEndpoint_AcceptsBaseOrV1Endpoint(
        string input,
        string expected)
    {
        FoundryLocalService.NormalizeChatCompletionsEndpoint(input).Should().Be(expected);
    }

    [Fact]
    public void TryExtractEndpoint_UsesLocalOpenAiCompatibleEndpoint()
    {
        const string status = """
            Foundry Local service is running.
            Model management service is running on http://127.0.0.1:5273/openai/status
            """;

        FoundryLocalCliEndpointResolver.TryExtractEndpoint(status)
            .Should()
            .Be("http://127.0.0.1:5273/v1/chat/completions");
    }

    [Fact]
    public void TryExtractLatestEndpoint_UsesMostRecentFoundryLogEndpoint()
    {
        const string log = """
            2026-05-15 08:10:00 [INF] Found service endpoints: http://127.0.0.1:3968
            2026-05-15 08:37:35 [INF] Loading model: http://127.0.0.1:1587/openai/load/qwen2.5-0.5b-instruct-openvino-npu:4?ttl=600
            """;

        FoundryLocalCliEndpointResolver.TryExtractLatestEndpoint(log)
            .Should()
            .Be("http://127.0.0.1:1587/v1/chat/completions");
    }

    [Fact]
    public void TryExtractEndpointFromLogDirectory_ReadsLatestFoundryLog()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "foundry-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        try
        {
            var oldLog = Path.Combine(logDirectory, "foundry20260514.log");
            var newLog = Path.Combine(logDirectory, "foundry20260515.log");
            File.WriteAllText(oldLog, "Found service endpoints: http://127.0.0.1:3968");
            File.WriteAllText(newLog, "Found service endpoints: http://127.0.0.1:1587");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newLog, DateTime.UtcNow);

            FoundryLocalCliEndpointResolver.TryExtractEndpointFromLogDirectory(logDirectory)
                .Should()
                .Be("http://127.0.0.1:1587/v1/chat/completions");
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TranslateAsync_PostsToConfiguredFoundryLocalEndpoint()
    {
        var handler = new CapturingHandler();
        var service = new FoundryLocalService(new HttpClient(handler));
        service.Configure("http://127.0.0.1:5273/v1", "test-model");

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        result.TranslatedText.Should().Be("你好");
        handler.RequestUri.Should().Be("http://127.0.0.1:5273/v1/chat/completions");
        handler.RequestBody.Should().Contain("\"model\":\"test-model\"");
    }

    [Fact]
    public async Task TranslateAsync_ResolvesAliasToLoadedModelId()
    {
        var handler = new ModelResolvingHandler();
        var service = new FoundryLocalService(new HttpClient(handler));
        service.Configure("http://127.0.0.1:5273/v1", "qwen2.5-0.5b");

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        result.TranslatedText.Should().Be("你好");
        handler.PostBody.Should().Contain("\"model\":\"qwen2.5-0.5b-instruct-openvino-npu:4\"");
    }

    [Fact]
    public async Task TranslateAsync_RefreshesLoopbackEndpointAfterConnectionFailure()
    {
        var handler = new EndpointRefreshHandler();
        var service = new FoundryLocalService(
            new HttpClient(handler),
            new StaticEndpointResolver("http://127.0.0.1:1587/"));
        service.Configure("http://127.0.0.1:3968/v1", "test-model");

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        result.TranslatedText.Should().Be("你好");
        handler.PostUris.Should().Contain("http://127.0.0.1:3968/v1/chat/completions");
        handler.PostUris.Should().Contain("http://127.0.0.1:1587/v1/chat/completions");
    }

    [Fact]
    public async Task TranslateAsync_ExplainsWhenFoundryCliIsMissing()
    {
        var service = new FoundryLocalService(
            new HttpClient(new CapturingHandler()),
            new MissingCliResolver());
        service.Configure(endpoint: "", model: "test-model");

        var act = async () => await service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            BypassCache = true,
        });

        var exception = await act.Should().ThrowAsync<TranslationException>();
        exception.Which.Message.Should().Contain("CLI is not installed");
        exception.Which.Message.Should().Contain(FoundryLocalService.InstallDocumentationUrl);
        exception.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            const string sse = """
                data: {"choices":[{"delta":{"content":"你"}}]}
                data: {"choices":[{"delta":{"content":"好"}}]}
                data: [DONE]

                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class EndpointRefreshHandler : HttpMessageHandler
    {
        public List<string> PostUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            PostUris.Add(uri);
            if (request.RequestUri?.Port == 3968)
            {
                throw new HttpRequestException("connection refused");
            }

            const string sse = """
                data: {"choices":[{"delta":{"content":"你好"}}]}
                data: [DONE]

                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class ModelResolvingHandler : HttpMessageHandler
    {
        public string? PostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"data":[
                          {"id":"qwen2.5-0.5b-instruct-generic-cpu:4"},
                          {"id":"qwen2.5-0.5b-instruct-openvino-npu:4"}
                        ],"object":"list"}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            PostBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            const string sse = """
                data: {"choices":[{"delta":{"content":"你好"}}]}
                data: [DONE]

                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class MissingCliResolver : IFoundryLocalEndpointResolver
    {
        public Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
        {
            throw new FoundryLocalCliNotFoundException(new Win32Exception());
        }
    }

    private sealed class StaticEndpointResolver : IFoundryLocalEndpointResolver
    {
        private readonly string _endpoint;

        public StaticEndpointResolver(string endpoint)
        {
            _endpoint = endpoint;
        }

        public Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(_endpoint);
        }
    }
}
