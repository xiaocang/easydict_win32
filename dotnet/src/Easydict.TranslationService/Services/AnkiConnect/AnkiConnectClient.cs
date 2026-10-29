using System.Net;
using System.Text;
using System.Text.Json;

namespace Easydict.TranslationService.Services.AnkiConnect;

/// <summary>A small asynchronous client for the AnkiConnect v6 HTTP API.</summary>
public sealed class AnkiConnectClient
{
    public static readonly Uri DefaultEndpoint = new("http://127.0.0.1:8765/");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int ApiVersion = 6;
    private const int MaxErrorBodyChars = 300;

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly TimeSpan _requestTimeout;

    /// <param name="httpClient">HTTP client owned by the caller.</param>
    /// <param name="endpoint">AnkiConnect endpoint, defaulting to the local Anki server.</param>
    /// <param name="apiKey">Optional key configured in the AnkiConnect add-on.</param>
    /// <param name="requestTimeout">Per-request timeout; defaults to eight seconds.</param>
    public AnkiConnectClient(
        HttpClient httpClient,
        Uri? endpoint = null,
        string? apiKey = null,
        TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? DefaultEndpoint;
        if (!_endpoint.IsAbsoluteUri || (_endpoint.Scheme != Uri.UriSchemeHttp && _endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("AnkiConnect endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(8);
        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Request timeout must be positive.");
        }
    }

    /// <summary>Checks connectivity and returns the AnkiConnect API version.</summary>
    public Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
        => SendAsync<int>("version", parameters: null, cancellationToken);

    /// <summary>Gets the names of the decks available in Anki.</summary>
    public Task<IReadOnlyList<string>> GetDeckNamesAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<string>>("deckNames", parameters: null, cancellationToken);

    /// <summary>Gets the names of the note types available in Anki.</summary>
    public Task<IReadOnlyList<string>> GetModelNamesAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<string>>("modelNames", parameters: null, cancellationToken);

    /// <summary>Gets the field names for a note type.</summary>
    public Task<IReadOnlyList<string>> GetModelFieldNamesAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return SendAsync<IReadOnlyList<string>>("modelFieldNames", new { modelName }, cancellationToken);
    }

    /// <summary>Adds a note and returns its Anki note ID.</summary>
    public Task<long> AddNoteAsync(AnkiNote note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        note.Validate();
        return SendAsync<long>("addNote", new { note = note.ToRequestPayload() }, cancellationToken);
    }

    private async Task<T> SendAsync<T>(string action, object? parameters, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["version"] = ApiVersion,
            ["params"] = parameters ?? new { },
        };
        if (_apiKey is not null)
        {
            payload["key"] = _apiKey;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        using var timeoutCts = _requestTimeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(_requestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AnkiConnectException($"AnkiConnect request '{action}' timed out after {_requestTimeout}.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AnkiConnectException($"Could not reach AnkiConnect at {_endpoint}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] + "…" : body;
                throw new AnkiConnectException(
                    $"AnkiConnect request '{action}' failed with HTTP {(int)response.StatusCode} ({response.StatusCode}): {snippet}");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("error", out var error) ||
                    !root.TryGetProperty("result", out var result))
                {
                    throw new AnkiConnectException($"AnkiConnect request '{action}' returned an invalid response envelope.");
                }

                if (error.ValueKind != JsonValueKind.Null)
                {
                    throw new AnkiConnectException($"AnkiConnect request '{action}' returned an error: {FormatError(error)}");
                }

                var value = result.Deserialize<T>(JsonOptions);
                if (value is null)
                {
                    throw new AnkiConnectException($"AnkiConnect request '{action}' returned an empty result.");
                }

                return value;
            }
            catch (JsonException ex)
            {
                throw new AnkiConnectException($"AnkiConnect request '{action}' returned invalid JSON: {ex.Message}", ex);
            }
        }
    }

    private static string FormatError(JsonElement error)
        => error.ValueKind == JsonValueKind.String ? error.GetString() ?? "unknown error" : error.ToString();
}

/// <summary>Fields used by AnkiConnect's <c>addNote</c> action.</summary>
public sealed record AnkiNote(
    string DeckName,
    string ModelName,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string>? Tags = null,
    bool AllowDuplicate = false)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeckName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelName);
        ArgumentNullException.ThrowIfNull(Fields);
        if (Fields.Count == 0 || Fields.Any(static field => string.IsNullOrWhiteSpace(field.Key)))
        {
            throw new ArgumentException("A note must contain at least one named field.", nameof(Fields));
        }
    }

    // Keep this explicit so the AnkiConnect wire format remains camelCase regardless of caller settings.
    internal object ToRequestPayload() => new
    {
        deckName = DeckName,
        modelName = ModelName,
        fields = Fields,
        tags = Tags ?? Array.Empty<string>(),
        options = new { allowDuplicate = AllowDuplicate },
    };
}

public sealed class AnkiConnectException(string message, Exception? innerException = null)
    : Exception(message, innerException);
