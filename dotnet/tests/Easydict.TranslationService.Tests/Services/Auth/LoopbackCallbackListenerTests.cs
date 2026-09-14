using System.Net;
using System.Net.Sockets;
using Easydict.TranslationService.Services.Auth;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.Auth;

/// <summary>
/// Exercises the loopback callback server over real 127.0.0.1 sockets. Each test is bounded
/// by a guard timeout so a regression cannot hang the suite.
/// </summary>
public class LoopbackCallbackListenerTests
{
    private const string State = "expected-state-token";
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(15);

    // Bypass any proxy configured in the environment so requests really hit 127.0.0.1.
    private static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = GuardTimeout };

    [Fact]
    public void Constructor_BindsLoopbackOnFreePort()
    {
        using var listener = new LoopbackCallbackListener();

        listener.Port.Should().BeGreaterThan(0);
        listener.CallbackUrl.Should().Be($"http://127.0.0.1:{listener.Port}/cb");
    }

    [Fact]
    public async Task WaitForCallbackAsync_ReturnsCodeWhenStateMatches()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync(State, guard.Token);

        using var http = CreateClient();
        using var response = await http.GetAsync($"{listener.CallbackUrl}?code=abc123&state={State}", guard.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(guard.Token)).Should().Contain("close this tab");

        var result = await wait;
        result.IsSuccess.Should().BeTrue();
        result.Code.Should().Be("abc123");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task WaitForCallbackAsync_RejectsWrongStateAndKeepsWaiting()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync(State, guard.Token);

        using var http = CreateClient();
        using (var forged = await http.GetAsync($"{listener.CallbackUrl}?code=evil&state=wrong", guard.Token))
        {
            forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await forged.Content.ReadAsStringAsync(guard.Token)).Should().Contain("state mismatch");
        }

        wait.IsCompleted.Should().BeFalse("a forged callback must not complete the sign-in");

        using var genuine = await http.GetAsync($"{listener.CallbackUrl}?code=real&state={State}", guard.Token);
        genuine.StatusCode.Should().Be(HttpStatusCode.OK);

        (await wait).Code.Should().Be("real");
    }

    [Fact]
    public async Task WaitForCallbackAsync_RejectsMissingState()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync(State, guard.Token);

        using var http = CreateClient();
        using var response = await http.GetAsync($"{listener.CallbackUrl}?code=abc", guard.Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wait.IsCompleted.Should().BeFalse();

        guard.Cancel();
        await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForCallbackAsync_ReportsProviderError()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync(State, guard.Token);

        using var http = CreateClient();
        using var response = await http.GetAsync(
            $"{listener.CallbackUrl}?error=access_denied&error_description=User%20denied%20access&state={State}",
            guard.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await wait;
        result.IsSuccess.Should().BeFalse();
        result.Code.Should().BeNull();
        result.Error.Should().Be("access_denied");
        result.ErrorDescription.Should().Be("User denied access");
    }

    [Fact]
    public async Task WaitForCallbackAsync_IgnoresOtherPathsAndMethods()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync(State, guard.Token);

        using var http = CreateClient();
        using (var favicon = await http.GetAsync($"http://127.0.0.1:{listener.Port}/favicon.ico", guard.Token))
        {
            favicon.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using (var post = await http.PostAsync($"{listener.CallbackUrl}?code=abc&state={State}", new StringContent("x"), guard.Token))
        {
            post.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }

        wait.IsCompleted.Should().BeFalse();

        using var genuine = await http.GetAsync($"{listener.CallbackUrl}?code=abc&state={State}", guard.Token);
        (await wait).Code.Should().Be("abc");
    }

    [Fact]
    public async Task WaitForCallbackAsync_SurvivesIdlePreconnectSocket()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync(State, guard.Token);

        // Browsers open speculative sockets that never send a request.
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, listener.Port, guard.Token);

        using var http = CreateClient();
        using var genuine = await http.GetAsync($"{listener.CallbackUrl}?code=after-idle&state={State}", guard.Token);

        genuine.StatusCode.Should().Be(HttpStatusCode.OK);
        (await wait).Code.Should().Be("after-idle");
    }

    [Fact]
    public async Task WaitForCallbackAsync_ThrowsWhenCancelledAndReleasesPort()
    {
        using var listener = new LoopbackCallbackListener();
        var port = listener.Port;
        using var cts = new CancellationTokenSource();

        var wait = listener.WaitForCallbackAsync(State, cts.Token);
        cts.CancelAfter(100);

        await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();

        using var probe = new TcpClient();
        var connect = () => probe.ConnectAsync(IPAddress.Loopback, port);
        await connect.Should().ThrowAsync<SocketException>("the listener must stop after the wait ends");
    }

    [Fact]
    public async Task WaitForCallbackAsync_DecodesPercentEncodedValues()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var listener = new LoopbackCallbackListener();
        var wait = listener.WaitForCallbackAsync("st ate/1", guard.Token);

        using var http = CreateClient();
        using var response = await http.GetAsync($"{listener.CallbackUrl}?code=a%2Fb%3Dc&state=st+ate%2F1", guard.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await wait).Code.Should().Be("a/b=c");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var listener = new LoopbackCallbackListener();

        listener.Dispose();
        var act = () => listener.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void ParseQuery_DecodesPlusPercentAndKeepsFirstDuplicate()
    {
        var parsed = LoopbackCallbackListener.ParseQuery("a=1+2&b=%2Fx&a=3&empty=&novalue&=skipped");

        parsed.Should().Equal(new Dictionary<string, string>
        {
            ["a"] = "1 2",
            ["b"] = "/x",
            ["empty"] = "",
            ["novalue"] = "",
        });
    }

    [Fact]
    public void ParseQuery_EmptyInputYieldsEmptyDictionary()
    {
        LoopbackCallbackListener.ParseQuery("").Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET /cb?code=1&state=2 HTTP/1.1", "GET", "/cb", "code=1&state=2")]
    [InlineData("GET /cb HTTP/1.0", "GET", "/cb", "")]
    [InlineData("HEAD /favicon.ico HTTP/1.1", "HEAD", "/favicon.ico", "")]
    [InlineData("GET http://127.0.0.1:1234/cb?x=1 HTTP/1.1", "GET", "/cb", "x=1")]
    [InlineData("GET /cb?x=1#frag HTTP/1.1", "GET", "/cb", "x=1")]
    public void TryParseRequestLine_SplitsMethodPathAndQuery(string line, string method, string path, string query)
    {
        LoopbackCallbackListener.TryParseRequestLine(line, out var m, out var p, out var q).Should().BeTrue();

        m.Should().Be(method);
        p.Should().Be(path);
        q.Should().Be(query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("GET /cb")]
    [InlineData("GET cb HTTP/1.1")]
    [InlineData("\u0016\u0003\u0001 TLS-hello")]
    public void TryParseRequestLine_RejectsMalformedLines(string line)
    {
        LoopbackCallbackListener.TryParseRequestLine(line, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void BuildResponse_ProducesCloseConnectionHttpResponse()
    {
        var bytes = LoopbackCallbackListener.BuildResponse(200, "OK", "<p>hi</p>");
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        text.Should().StartWith("HTTP/1.1 200 OK\r\n");
        text.Should().Contain("Content-Type: text/html; charset=utf-8\r\n");
        text.Should().Contain("Content-Length: 9\r\n");
        text.Should().Contain("Connection: close\r\n");
        text.Should().EndWith("\r\n\r\n<p>hi</p>");
    }
}
