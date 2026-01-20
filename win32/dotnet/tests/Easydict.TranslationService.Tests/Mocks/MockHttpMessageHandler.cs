using System.Net;

namespace Easydict.TranslationService.Tests.Mocks;

/// <summary>
/// Mock HTTP message handler for testing HTTP-based translation services.
/// Allows setting up expected responses for specific request patterns.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _requests = new();
    private readonly List<string?> _requestBodies = new();

    /// <summary>
    /// Gets all requests that were sent through this handler.
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _requests.AsReadOnly();

    /// <summary>
    /// Gets the last request that was sent, or null if no requests have been made.
    /// </summary>
    public HttpRequestMessage? LastRequest => _requests.Count > 0 ? _requests[^1] : null;

    /// <summary>
    /// Gets the body content of the last request as a string.
    /// This is captured before the request is processed to avoid disposal issues.
    /// </summary>
    public string? LastRequestBody => _requestBodies.Count > 0 ? _requestBodies[^1] : null;

    /// <summary>
    /// Enqueue a response to be returned for the next request.
    /// Responses are returned in FIFO order.
    /// </summary>
    public void EnqueueResponse(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    /// <summary>
    /// Enqueue a successful JSON response.
    /// </summary>
    public void EnqueueJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        _responses.Enqueue(response);
    }

    /// <summary>
    /// Enqueue a streaming SSE response for testing streaming translation services.
    /// </summary>
    public void EnqueueStreamingResponse(IEnumerable<string> sseEvents, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var content = string.Join("\n\n", sseEvents.Select(e => $"data: {e}")) + "\n\ndata: [DONE]\n\n";
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/event-stream")
        };
        _responses.Enqueue(response);
    }

    /// <summary>
    /// Enqueue an error response.
    /// </summary>
    public void EnqueueErrorResponse(HttpStatusCode statusCode, string? errorMessage = null)
    {
        var json = errorMessage != null
            ? $"{{\"error\": {{\"message\": \"{errorMessage}\"}}}}"
            : $"{{\"error\": {{\"message\": \"HTTP {(int)statusCode}\"}}}}";
        EnqueueJsonResponse(json, statusCode);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);

        // Capture request body before it could be disposed
        string? body = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        _requestBodies.Add(body);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                "No responses queued. Call EnqueueResponse or EnqueueJsonResponse before making requests.");
        }

        return _responses.Dequeue();
    }

    /// <summary>
    /// Clear all queued responses and recorded requests.
    /// </summary>
    public void Reset()
    {
        _responses.Clear();
        _requests.Clear();
        _requestBodies.Clear();
    }
}
