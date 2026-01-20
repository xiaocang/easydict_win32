using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Streaming;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Base class for OpenAI-compatible streaming translation services.
/// Mirrors macOS BaseOpenAIService pattern with SSE streaming support.
/// </summary>
public abstract class BaseOpenAIService : BaseTranslationService, IStreamTranslationService
{
    /// <summary>
    /// Common set of languages supported by most LLM services.
    /// </summary>
    protected static readonly IReadOnlyList<Language> OpenAILanguages = new[]
    {
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.Spanish,
        Language.Portuguese,
        Language.Italian,
        Language.German,
        Language.Russian,
        Language.Arabic,
        Language.Dutch,
        Language.Polish,
        Language.Vietnamese,
        Language.Thai,
        Language.Indonesian,
        Language.Turkish,
        Language.Swedish,
        Language.Danish,
        Language.Norwegian,
        Language.Finnish,
        Language.Greek,
        Language.Czech,
        Language.Romanian,
        Language.Hungarian,
        Language.Ukrainian,
        Language.Hebrew,
        Language.Hindi,
        Language.Bengali,
        Language.Tamil,
        Language.Persian
    };

    /// <summary>
    /// System prompt from macOS StreamService.translationSystemPrompt.
    /// Instructs the model to act as a translation expert.
    /// </summary>
    protected const string TranslationSystemPrompt = """
        You are a translation expert proficient in various languages, focusing solely on translating text without interpretation. You accurately understand the meanings of proper nouns, idioms, metaphors, allusions, and other obscure words in sentences, translating them appropriately based on the context and language environment. The translation should be natural and fluent. Only return the translated text, without including redundant quotes or additional notes.
        """;

    protected BaseOpenAIService(HttpClient httpClient) : base(httpClient) { }

    /// <summary>
    /// API endpoint URL for chat completions.
    /// </summary>
    public abstract string Endpoint { get; }

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public abstract string ApiKey { get; }

    /// <summary>
    /// Model identifier to use for generation.
    /// </summary>
    public abstract string Model { get; }

    /// <summary>
    /// Temperature for generation (0.0-1.0).
    /// Lower values produce more deterministic output.
    /// Default: 0.3 for consistent translations.
    /// </summary>
    public virtual double Temperature => 0.3;

    /// <summary>
    /// Whether this service requires an API key to function.
    /// Override to false for services like Ollama that don't need auth.
    /// </summary>
    public override bool RequiresApiKey => true;

    /// <summary>
    /// This is a streaming service.
    /// </summary>
    public bool IsStreaming => true;

    /// <summary>
    /// Implement non-streaming translation by consuming the stream.
    /// </summary>
    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        await foreach (var chunk in TranslateStreamAsync(request, cancellationToken))
        {
            sb.Append(chunk);
        }

        var translatedText = CleanupResult(sb.ToString());

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
    }

    /// <summary>
    /// Stream translate text using OpenAI-compatible API.
    /// </summary>
    public virtual async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var messages = BuildChatMessages(request);
        var requestBody = BuildRequestBody(messages);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        if (!string.IsNullOrEmpty(ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException($"Network error: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = ServiceId
            };
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw CreateErrorFromResponse(response.StatusCode, errorBody);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in SseParser.ParseStreamAsync(stream, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Build chat messages for translation request.
    /// Override to customize prompts.
    /// </summary>
    protected virtual List<ChatMessage> BuildChatMessages(TranslationRequest request)
    {
        var sourceLangName = request.FromLanguage == Language.Auto
            ? "the detected language"
            : request.FromLanguage.GetDisplayName();
        var targetLangName = request.ToLanguage.GetDisplayName();

        return new List<ChatMessage>
        {
            new(ChatRole.System, TranslationSystemPrompt),
            new(ChatRole.User, $"Translate the following {sourceLangName} text into {targetLangName} text: \"\"\"{request.Text}\"\"\"")
        };
    }

    /// <summary>
    /// Build the request body for the API call.
    /// </summary>
    protected virtual object BuildRequestBody(List<ChatMessage> messages)
    {
        return new
        {
            model = Model,
            messages = messages.Select(m => new { role = m.RoleString, content = m.Content }),
            temperature = Temperature,
            stream = true
        };
    }

    /// <summary>
    /// Validate service configuration before making API calls.
    /// </summary>
    protected virtual void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(Endpoint))
        {
            throw new TranslationException("Endpoint is not configured")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        if (RequiresApiKey && string.IsNullOrEmpty(ApiKey))
        {
            throw new TranslationException("API key is required but not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }
    }

    /// <summary>
    /// Create appropriate exception from HTTP error response.
    /// </summary>
    private TranslationException CreateErrorFromResponse(System.Net.HttpStatusCode statusCode, string errorBody)
    {
        var errorCode = statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => TranslationErrorCode.InvalidApiKey,
            System.Net.HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
            System.Net.HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
            System.Net.HttpStatusCode.BadRequest => TranslationErrorCode.InvalidResponse,
            System.Net.HttpStatusCode.InternalServerError => TranslationErrorCode.ServiceUnavailable,
            System.Net.HttpStatusCode.ServiceUnavailable => TranslationErrorCode.ServiceUnavailable,
            System.Net.HttpStatusCode.GatewayTimeout => TranslationErrorCode.Timeout,
            _ => TranslationErrorCode.Unknown
        };

        // Try to extract error message from response
        var message = $"API error ({(int)statusCode}): {statusCode}";
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var msgElement))
                {
                    message = msgElement.GetString() ?? message;
                }
            }
        }
        catch (JsonException)
        {
            // Use default message
        }

        return new TranslationException(message)
        {
            ErrorCode = errorCode,
            ServiceId = ServiceId
        };
    }

    /// <summary>
    /// Clean up the final translation result.
    /// Removes common artifacts like quotes and whitespace.
    /// </summary>
    protected virtual string CleanupResult(string text)
    {
        var result = text.Trim();

        // Remove surrounding quotes if present
        if (result.Length >= 2)
        {
            if ((result.StartsWith('"') && result.EndsWith('"')) ||
                (result.StartsWith('\"') && result.EndsWith('\"')) ||
                (result.StartsWith('"') && result.EndsWith('"')))
            {
                result = result[1..^1].Trim();
            }
        }

        return result;
    }
}
