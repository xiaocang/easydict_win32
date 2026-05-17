using System.Net;
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
/// Supports both Chat Completions and Responses API formats, auto-detected
/// by URL path or by probing the endpoint at first request.
/// </summary>
public abstract class BaseOpenAIService : BaseTranslationService, IStreamTranslationService, IGrammarCorrectionService
{
    /// <summary>
    /// Common set of languages supported by most LLM services.
    /// </summary>
    internal static readonly IReadOnlyList<Language> OpenAILanguages = new[]
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
    /// System prompt for grammar correction mode (no explanation).
    /// Instructs the model to output only the corrected text.
    /// </summary>
    internal const string GrammarCorrectionSystemPrompt = """
        You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

        Rules:
        1. NEVER translate the text. The output must be in the exact same language as the input.
        2. Keep the original meaning unchanged.
        3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
        4. Output ONLY the corrected text with no additional commentary, labels, or formatting.
        5. If the text has no errors, output it unchanged.
        """;

    /// <summary>
    /// System prompt for grammar correction mode with explanations.
    /// Instructs the model to output the corrected text followed by a list of changes.
    /// </summary>
    internal const string GrammarCorrectionSystemPromptWithExplanation = """
        You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

        Rules:
        1. NEVER translate the text. The output must be in the exact same language as the input.
        2. Keep the original meaning unchanged.
        3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
        4. First output the fully corrected text, then on a new line output "---", then briefly list the key corrections you made.
        5. If the text has no errors, output it unchanged followed by "---" and "No errors found."
        """;

    /// <summary>
    /// System prompt from macOS StreamService.translationSystemPrompt.
    /// Instructs the model to act as a translation expert.
    /// </summary>
    internal const string TranslationSystemPrompt = """
        You are a translation expert proficient in various languages, focusing solely on translating text without interpretation. You accurately understand the meanings of proper nouns, idioms, metaphors, allusions, and other obscure words in sentences, translating them appropriately based on the context and language environment. The translation should be natural and fluent. Only return the translated text, without including redundant quotes or additional notes.
        """;

    private readonly object _formatLock = new();
    private OpenAIApiFormat _detectedFormat = OpenAIApiFormat.Auto;

    protected BaseOpenAIService(HttpClient httpClient) : base(httpClient) { }

    /// <summary>
    /// API endpoint URL for chat completions or responses.
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
    /// Currently detected (or pinned) API format. Exposed for diagnostics and tests.
    /// Returns <see cref="OpenAIApiFormat.Auto"/> until the first successful request
    /// (or the URL is unambiguous and a request has been made).
    /// </summary>
    public OpenAIApiFormat DetectedFormat
    {
        get
        {
            lock (_formatLock) { return _detectedFormat; }
        }
    }

    /// <summary>
    /// Reset the cached format detection. Subclasses MUST call this from their
    /// Configure() methods so that an endpoint change re-triggers detection.
    /// </summary>
    protected void ResetFormatDetection()
    {
        lock (_formatLock) { _detectedFormat = OpenAIApiFormat.Auto; }
    }

    private void CacheFormat(OpenAIApiFormat format)
    {
        lock (_formatLock) { _detectedFormat = format; }
    }

    /// <summary>
    /// Inspect the endpoint URL path to determine API format. Returns
    /// <see cref="OpenAIApiFormat.Auto"/> when the URL doesn't end with a
    /// recognized suffix (caller must then probe).
    /// </summary>
    internal static OpenAIApiFormat DetectFormatFromUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return OpenAIApiFormat.Auto;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return OpenAIApiFormat.Responses;
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return OpenAIApiFormat.ChatCompletions;
        return OpenAIApiFormat.Auto;
    }

    /// <summary>
    /// Implement non-streaming translation by consuming the stream.
    /// </summary>
    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var translatedText = CleanupResult(
            await ConsumeStreamAsync(TranslateStreamAsync(request, cancellationToken), cancellationToken));

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
    /// Dispatches to Chat Completions or Responses path based on cached / detected format.
    /// </summary>
    public virtual IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        return StreamWithFormatDispatchAsync(BuildChatMessages(request), cancellationToken);
    }

    /// <summary>
    /// Stream grammar correction output using OpenAI-compatible API.
    /// Reuses the same dispatch+probe logic as translation.
    /// </summary>
    public virtual IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        return StreamWithFormatDispatchAsync(BuildGrammarCorrectionMessages(request), cancellationToken);
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

        var systemPrompt = TranslationSystemPrompt;
        if (!string.IsNullOrWhiteSpace(request.CustomPrompt))
        {
            systemPrompt += $"\n\nAdditional instructions: {request.CustomPrompt}";
        }

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"Translate the following {sourceLangName} text into {targetLangName} text: \"\"\"{request.Text}\"\"\"")
        };
    }

    /// <summary>
    /// Build chat messages for grammar correction request.
    /// Override to customize prompts.
    /// </summary>
    protected virtual List<ChatMessage> BuildGrammarCorrectionMessages(GrammarCorrectionRequest request)
    {
        var userPrompt = request.Language == Language.Auto
            ? $"Correct the grammar in the following text:\n\n{request.Text}"
            : $"Correct the grammar in the following {request.Language.GetDisplayName()} text. The result MUST remain in {request.Language.GetDisplayName()}:\n\n{request.Text}";

        var systemPrompt = request.IncludeExplanations
            ? GrammarCorrectionSystemPromptWithExplanation
            : GrammarCorrectionSystemPrompt;

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };
    }

    /// <summary>
    /// Build the Chat Completions API request body.
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
    /// Build the Responses API request body. The system prompt becomes
    /// <c>instructions</c>; all other messages are concatenated as <c>input</c>.
    /// </summary>
    protected virtual object BuildResponsesRequestBody(List<ChatMessage> messages)
    {
        var instructions = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content;
        var input = string.Join(
            "\n\n",
            messages.Where(m => m.Role != ChatRole.System).Select(m => m.Content));

        return new
        {
            model = Model,
            instructions,
            input,
            temperature = Temperature,
            stream = true,
            store = false
        };
    }

    /// <summary>
    /// Hook for subclasses to add custom headers or modify the HTTP request
    /// before it is sent. Called after Authorization header is set.
    /// </summary>
    protected virtual void ConfigureHttpRequest(HttpRequestMessage request) { }

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

    private async IAsyncEnumerable<string> StreamWithFormatDispatchAsync(
        List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cached = DetectedFormat;
        var initial = cached != OpenAIApiFormat.Auto
            ? cached
            : DetectFormatFromUrl(Endpoint);

        OpenAIApiFormat chosen;
        HttpResponseMessage response;

        if (initial != OpenAIApiFormat.Auto)
        {
            chosen = initial;
            response = await SendFormatRequestAsync(chosen, messages, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Auto: probe with preferred format; if endpoint rejects the path,
            // retry with the other format and cache whichever succeeds.
            var first = PreferredAutoProbeFormat();
            chosen = first;
            response = await SendFormatRequestAsync(first, messages, cancellationToken).ConfigureAwait(false);

            if (ShouldFallback(response.StatusCode))
            {
                response.Dispose();
                chosen = OtherFormat(first);
                response = await SendFormatRequestAsync(chosen, messages, cancellationToken).ConfigureAwait(false);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw CreateErrorFromResponse(response.StatusCode, errorBody);
            }

            CacheFormat(chosen);

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var chunks = chosen == OpenAIApiFormat.Responses
                ? ResponsesSseParser.ParseStreamAsync(stream, cancellationToken)
                : SseParser.ParseStreamAsync(stream, cancellationToken);

            await foreach (var chunk in chunks.ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    private async Task<HttpResponseMessage> SendFormatRequestAsync(
        OpenAIApiFormat format,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var body = format == OpenAIApiFormat.Responses
            ? BuildResponsesRequestBody(messages)
            : BuildRequestBody(messages);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }

        ConfigureHttpRequest(httpRequest);

        try
        {
            return await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            httpRequest.Dispose();
            throw new TranslationException($"Network error: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = ServiceId
            };
        }
    }

    private OpenAIApiFormat PreferredAutoProbeFormat()
    {
        // Official api.openai.com → try Responses (latest) first. Otherwise
        // (third-party / self-hosted) → Chat Completions, which has wider support.
        if (Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAIApiFormat.Responses;
        }
        return OpenAIApiFormat.ChatCompletions;
    }

    private static OpenAIApiFormat OtherFormat(OpenAIApiFormat format) =>
        format == OpenAIApiFormat.Responses
            ? OpenAIApiFormat.ChatCompletions
            : OpenAIApiFormat.Responses;

    private static bool ShouldFallback(HttpStatusCode status) =>
        status == HttpStatusCode.NotFound || status == HttpStatusCode.MethodNotAllowed;
}
