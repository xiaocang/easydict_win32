using System.Net;
using System.Text;

namespace Easydict.BobPlugin.Tests.Mocks;

/// <summary>
/// Answers plugin HTTP requests from a queue of canned responses and records what was sent, so a
/// fixture's use of <c>$http</c> can be asserted without touching the network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly object _sync = new();

    /// <summary>What a plugin actually sent, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Answer the next request with this JSON body.</summary>
    public StubHttpMessageHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(json, "application/json", status);

    /// <summary>Answer the next request with this plain-text body.</summary>
    public StubHttpMessageHandler EnqueueText(string text, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(text, "text/plain", status);

    public StubHttpMessageHandler Enqueue(string body, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        lock (_sync)
        {
            _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            }));
        }

        return this;
    }

    /// <summary>Fail the next request the way a dead endpoint would.</summary>
    public StubHttpMessageHandler EnqueueFailure(string message)
    {
        lock (_sync)
        {
            _responses.Enqueue((_, _) => throw new HttpRequestException(message));
        }

        return this;
    }

    /// <summary>
    /// Answer the next request by hanging until that specific request's own cancellation token
    /// fires, and report it via <paramref name="cancelledSignal"/>. Lets a test prove that a
    /// particular in-flight request - not just the overall plugin call - is linked to the call's
    /// cancellation, which a shared, always-cancelled token would not distinguish.
    /// </summary>
    public StubHttpMessageHandler EnqueueHang(TaskCompletionSource<bool> cancelledSignal)
    {
        lock (_sync)
        {
            _responses.Enqueue(async (_, ct) =>
            {
                using var registration = ct.Register(() => cancelledSignal.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable: Task.Delay with an infinite timeout only returns via cancellation.");
            });
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder;
        lock (_sync)
        {
            _requests.Add(new RecordedRequest(
                request.Method.Method,
                // AbsoluteUri keeps percent-encoding intact; ToString() would undo it.
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Headers
                    .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                    .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
                body));

            responder = _responses.Count > 0 ? _responses.Dequeue() : null;
        }

        if (responder is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("no stub response enqueued", Encoding.UTF8, "text/plain")
            };
        }

        return await responder(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One request a plugin made.</summary>
    internal sealed record RecordedRequest(string Method, string Url, Dictionary<string, string> Headers, string? Body);
}
