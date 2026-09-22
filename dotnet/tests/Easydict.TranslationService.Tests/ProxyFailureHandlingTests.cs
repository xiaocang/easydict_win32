using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// A proxy that is switched off or mistyped used to look like a hang: every service spent the
/// full request budget three times over before saying anything, and what it finally said was a
/// generic network error. These cover the fast, named failure that replaced it.
/// </summary>
public class ProxyFailureHandlingTests
{
    private const string ProxyEndpoint = "http://127.0.0.1:59999";

    private static HttpRequestException ConnectionRefused() =>
        new(
            HttpRequestError.ConnectionError,
            "An error occurred while sending the request.",
            new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task ProxiedRequest_ThatCannotConnect_IsReportedAsAProxyFailure()
    {
        using var invoker = CreateInvoker(ConnectionRefused(), bypassLocal: true);

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://translate.googleapis.com/translate_a/single"),
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ProxyUnreachableException>();
        thrown.Which.ProxyEndpoint.Should().Be(ProxyEndpoint);
        thrown.Which.Message.Should().Contain(ProxyEndpoint);
    }

    [Fact]
    public async Task BypassedRequest_ThatCannotConnect_StillBlamesTheServiceItself()
    {
        // A local Ollama that is simply not running must not be reported as a proxy problem.
        using var invoker = CreateInvoker(ConnectionRefused(), bypassLocal: true);

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat"),
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Should().NotBeOfType<ProxyUnreachableException>();
    }

    [Fact]
    public async Task ProxiedRequest_ThatReachedTheService_IsLeftAlone()
    {
        // The service answered — badly, but it answered. That is not the proxy's fault.
        using var invoker = CreateInvoker(
            new HttpRequestException("Response ended prematurely.", null, HttpStatusCode.BadGateway),
            bypassLocal: true);

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/chat/completions"),
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Should().NotBeOfType<ProxyUnreachableException>();
    }

    [Fact]
    public async Task ConnectTimeoutThroughTheProxy_IsReportedAsAProxyFailure()
    {
        // The case the whole change exists for: a proxy that accepts the SYN and then says
        // nothing. This drives the real SocketsHttpHandler timeout rather than asserting against
        // a hand-built exception, because the shape .NET produces here is the crux — it is a
        // cancellation, not an HttpRequestException.
        var proxy = new WebProxy(new Uri(ProxyEndpoint)) { BypassProxyOnLocal = true };
        using var invoker = new HttpMessageInvoker(new ProxyFailureDetectingHandler(
            new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(200),
                ConnectCallback = HangUntilCancelled,
            },
            proxy,
            ProxyEndpoint));

        // Backstop: if ConnectTimeout ever stopped covering ConnectCallback this would fail
        // rather than hang the test run.
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://translate.googleapis.com/translate_a/single"),
            backstop.Token);

        var thrown = await act.Should().ThrowAsync<ProxyUnreachableException>();
        thrown.Which.ProxyEndpoint.Should().Be(ProxyEndpoint);
    }

    [Fact]
    public async Task ConnectTimeoutShape_WhileTheCallerIsCancelling_StaysACancellation()
    {
        // HttpClient.Timeout and a user pressing Escape produce the same exception shape as the
        // connect timeout. Their token being cancelled is the only thing that tells them apart.
        using var invoker = CreateInvoker(
            new TaskCanceledException(
                "The operation was canceled.",
                new TimeoutException("A connection could not be established within the configured ConnectTimeout.")),
            bypassLocal: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://translate.googleapis.com/translate_a/single"),
            cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HttpClientTimeout_IsNotBlamedOnTheProxy()
    {
        // Same hang, but the deadline that fires is the caller's, so the proxy is not the story.
        var proxy = new WebProxy(new Uri(ProxyEndpoint)) { BypassProxyOnLocal = true };
        using var client = new HttpClient(new ProxyFailureDetectingHandler(
            new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = true,
                ConnectTimeout = Timeout.InfiniteTimeSpan,
                ConnectCallback = HangUntilCancelled,
            },
            proxy,
            ProxyEndpoint))
        {
            Timeout = TimeSpan.FromMilliseconds(200)
        };

        var act = () => client.GetAsync("http://translate.googleapis.com/translate_a/single");

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public void ATimeoutUnderAnHttpFailure_CountsAsAFirstHopFailure()
    {
        // Not the ConnectTimeout shape — a custom connect callback that times out arrives wrapped
        // like this.
        var failure = new HttpRequestException(
            "A connection could not be established.",
            new TimeoutException("Timed out connecting."));

        ProxyFailureClassifier.IsFirstHopFailure(failure).Should().BeTrue();
    }

    [Fact]
    public void UnresolvableHost_CountsAsAFirstHopFailure()
    {
        var failure = new HttpRequestException(
            "An error occurred while sending the request.",
            new SocketException((int)SocketError.HostNotFound));

        ProxyFailureClassifier.IsFirstHopFailure(failure).Should().BeTrue();
    }

    [Fact]
    public void ATimeoutOnItsOwn_SaysNothingAboutTheTransport()
    {
        ProxyFailureClassifier.IsFirstHopFailure(new TimeoutException()).Should().BeFalse();
    }

    [Fact]
    public async Task ProxyFailure_IsNotRetried_AndNamesTheProxy()
    {
        using var manager = new TranslationManager();
        var service = new FailingService("proxy-failing", () => new TranslationException(
            "Network error: something went wrong",
            new ProxyUnreachableException(ProxyEndpoint, new SocketException((int)SocketError.ConnectionRefused)))
        {
            ErrorCode = TranslationErrorCode.NetworkError,
            ServiceId = "proxy-failing"
        });
        manager.RegisterService(service);

        var act = () => manager.TranslateAsync(NewRequest(), CancellationToken.None, "proxy-failing");

        var thrown = await act.Should().ThrowAsync<TranslationException>();
        thrown.Which.ErrorCode.Should().Be(TranslationErrorCode.ProxyError);
        thrown.Which.ServiceId.Should().Be("proxy-failing");
        thrown.Which.Message.Should().Contain(ProxyEndpoint);
        service.Attempts.Should().Be(1, "a proxy that is down does not come back within a retry");
    }

    [Fact]
    public async Task OrdinaryNetworkFailure_IsStillRetried()
    {
        using var manager = new TranslationManager();
        var service = new FailingService("flaky", () => new TranslationException("Network error: reset")
        {
            ErrorCode = TranslationErrorCode.NetworkError,
            ServiceId = "flaky"
        });
        manager.RegisterService(service);

        var act = () => manager.TranslateAsync(NewRequest(), CancellationToken.None, "flaky");

        await act.Should().ThrowAsync<TranslationException>();
        service.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task LegacyStreamApi_ReportsAProxyFailureAsOne()
    {
        // TranslateStreamUpdatesAsync is what the app uses, but this overload is public and
        // documented; a caller of it needs the same error code to show the proxy hint.
        using var manager = new TranslationManager();
        manager.RegisterService(new FailingStreamService("proxy-stream", () =>
            new TranslationException(
                "Network error: something went wrong",
                new ProxyUnreachableException(
                    ProxyEndpoint, new SocketException((int)SocketError.ConnectionRefused)))
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = "proxy-stream"
            }));

        var act = async () =>
        {
            await foreach (var _ in manager.TranslateStreamAsync(
                NewRequest(), CancellationToken.None, "proxy-stream"))
            {
            }
        };

        var thrown = await act.Should().ThrowAsync<TranslationException>();
        thrown.Which.ErrorCode.Should().Be(TranslationErrorCode.ProxyError);
        thrown.Which.Message.Should().Contain(ProxyEndpoint);
    }

    [Fact]
    public void AlreadyDescribedProxyFailure_IsPassedThroughUnchanged()
    {
        // Re-labelling twice would bury the proxy's name under a second wrapper.
        var proxyFailure = new TranslationException(
            "Cannot reach the HTTP proxy",
            new ProxyUnreachableException(ProxyEndpoint, new SocketException((int)SocketError.HostNotFound)))
        {
            ErrorCode = TranslationErrorCode.ProxyError
        };

        TranslationManager.DescribeProxyFailure(proxyFailure, "any")
            .Should().BeSameAs(proxyFailure);
    }

    [Fact]
    public void ServiceFailureWithoutAProxyInvolved_IsNotDescribedAsOne()
    {
        var failure = new TranslationException("Network error: reset")
        {
            ErrorCode = TranslationErrorCode.NetworkError
        };

        TranslationManager.DescribeProxyFailure(failure, "any").Should().BeNull();
    }

    private static TranslationRequest NewRequest() => new()
    {
        Text = "test",
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese
    };

    private static HttpMessageInvoker CreateInvoker(Exception transportFailure, bool bypassLocal)
    {
        var proxy = new WebProxy(new Uri(ProxyEndpoint)) { BypassProxyOnLocal = bypassLocal };
        return new HttpMessageInvoker(
            new ProxyFailureDetectingHandler(new ThrowingHandler(transportFailure), proxy, ProxyEndpoint));
    }

    private static async ValueTask<Stream> HangUntilCancelled(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return Stream.Null;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _failure;

        internal ThrowingHandler(Exception failure) => _failure = failure;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_failure);
    }

    private sealed class FailingStreamService : FailingService, IStreamTranslationService
    {
        internal FailingStreamService(string serviceId, Func<Exception> failure)
            : base(serviceId, failure)
        {
        }

        public bool IsStreaming => true;

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw Fail();
#pragma warning disable CS0162 // Unreachable: the compiler needs a yield to make this an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }

    private class FailingService : ITranslationService
    {
        private readonly Func<Exception> _failure;

        internal FailingService(string serviceId, Func<Exception> failure)
        {
            ServiceId = serviceId;
            _failure = failure;
        }

        internal int Attempts { get; private set; }

        protected Exception Fail() => _failure();

        public string ServiceId { get; }
        public string DisplayName => ServiceId;
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages =>
            new[] { Language.English, Language.SimplifiedChinese };

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromException<TranslationResult>(_failure());
        }

        public Task<Language> DetectLanguageAsync(
            string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(Language.Auto);
    }
}
