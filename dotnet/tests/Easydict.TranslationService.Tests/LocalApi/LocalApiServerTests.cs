using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.LocalApi;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LocalApi;

/// <summary>
/// Integration tests for <see cref="LocalApiServer"/>. We start a real HttpListener on a
/// free local port, register a stub translation service, and hit the endpoints via HttpClient.
/// </summary>
public class LocalApiServerTests : IAsyncLifetime
{
    private readonly TranslationManager _manager = new();
    private readonly StubStreamService _stub = new();
    private readonly HttpClient _http = new();
    private LocalApiServer? _server;
    private int _port;
    private const string Token = "sk-edt-testtoken1234567890abcdef";

    public async Task InitializeAsync()
    {
        _manager.RegisterService(_stub);
        _server = new LocalApiServer(() => _manager);
        _port = GetFreePort();
        await _server.StartAsync(BuildOptions(_port, new[] { _stub.ServiceId }));
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync();
        _server?.Dispose();
        _http.Dispose();
    }

    private string BaseUrl => $"http://127.0.0.1:{_port}";

    private static LocalApiOptions BuildOptions(int port, IEnumerable<string> exposed) => new()
    {
        Port = port,
        Token = Token,
        ExposedServiceIds = new HashSet<string>(exposed),
        CorsMode = LocalApiCorsMode.Any,
        AllowedOrigins = Array.Empty<string>(),
        DefaultTargetLanguage = Language.SimplifiedChinese,
    };

    [Fact]
    public async Task Healthz_returns_200_without_auth()
    {
        var res = await _http.GetAsync($"{BaseUrl}/healthz");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("ok");
    }

    [Fact]
    public async Task Models_requires_bearer()
    {
        var res = await _http.GetAsync($"{BaseUrl}/v1/models");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Models_filters_to_exposed_services()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var res = await _http.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("\"easydict-stub-stream\"");
    }

    [Fact]
    public async Task ChatCompletions_nonstream_returns_translated_text()
    {
        var msg = BuildChatRequest(stream: false, "hello world");
        var res = await _http.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("\"content\":\"翻译: hello world\"");
        body.Should().Contain("\"finish_reason\":\"stop\"");
    }

    [Fact]
    public async Task ChatCompletions_stream_emits_chunks_and_done()
    {
        var msg = BuildChatRequest(stream: true, "hello world");
        var res = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        var collected = new StringBuilder();
        string? line;
        var sawDone = false;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (line == "data: [DONE]") { sawDone = true; break; }
            if (line.StartsWith("data:"))
            {
                collected.AppendLine(line);
            }
        }
        sawDone.Should().BeTrue();
        collected.ToString().Should().Contain("delta");
        collected.ToString().Should().Contain("\"finish_reason\":\"stop\"");
    }

    [Fact]
    public async Task Bad_bearer_returns_401()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var res = await _http.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cors_preflight_returns_204_with_headers()
    {
        var msg = new HttpRequestMessage(HttpMethod.Options, $"{BaseUrl}/v1/chat/completions");
        msg.Headers.Add("Origin", "https://example.com");
        msg.Headers.Add("Access-Control-Request-Method", "POST");
        var res = await _http.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        res.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("*");
        res.Headers.GetValues("Access-Control-Allow-Methods").Should().ContainMatch("*POST*");
    }

    [Fact]
    public async Task AllowList_origin_not_in_list_rejected()
    {
        await _server!.ReconfigureAsync(new LocalApiOptions
        {
            Port = _port,
            Token = Token,
            ExposedServiceIds = new HashSet<string>(new[] { _stub.ServiceId }),
            CorsMode = LocalApiCorsMode.AllowList,
            AllowedOrigins = new[] { "https://allowed.example.com" },
            DefaultTargetLanguage = Language.SimplifiedChinese,
        });

        var msg = new HttpRequestMessage(HttpMethod.Options, $"{BaseUrl}/v1/chat/completions");
        msg.Headers.Add("Origin", "https://evil.example.com");
        msg.Headers.Add("Access-Control-Request-Method", "POST");
        var res = await _http.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unknown_model_returns_404()
    {
        var msg = BuildChatRequest(stream: false, "hi", model: "easydict-unknown");
        var res = await _http.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reconfigure_changes_port()
    {
        var newPort = GetFreePort();
        await _server!.ReconfigureAsync(BuildOptions(newPort, new[] { _stub.ServiceId }));
        _port = newPort;
        var res = await _http.GetAsync($"http://127.0.0.1:{newPort}/healthz");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private HttpRequestMessage BuildChatRequest(bool stream, string user, string model = "easydict-stub-stream")
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream,
            messages = new[] { new { role = "user", content = user } }
        });
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return msg;
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}

internal sealed class StubStreamService : IStreamTranslationService
{
    public string ServiceId => "stub-stream";
    public string DisplayName => "Stub (stream)";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;
    public bool IsStreaming => true;
    public IReadOnlyList<Language> SupportedLanguages => new[] { Language.Auto, Language.English, Language.SimplifiedChinese };

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TranslationResult
        {
            TranslatedText = $"翻译: {request.Text}",
            OriginalText = request.Text,
            ServiceName = DisplayName,
            TargetLanguage = request.ToLanguage,
            DetectedLanguage = request.FromLanguage,
        });

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "翻译: ";
        await Task.Yield();
        yield return request.Text;
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);
}
