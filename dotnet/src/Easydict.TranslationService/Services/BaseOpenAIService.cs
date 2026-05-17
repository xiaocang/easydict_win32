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
/// Supports both Chat Completions and Responses API formats; the format
/// is chosen per request based on the endpoint URL suffix
/// (<c>/responses</c> → Responses, anything else → Chat Completions) or
/// by an explicit override via <see cref="PinFormat"/>. Pinning bypasses
/// URL inspection — the caller is responsible for ensuring the endpoint
/// accepts the pinned format.
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

    private OpenAIApiFormat? _formatOverride;

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
    /// Format that will be used for the next request — either the explicit
    /// override (set via <see cref="PinFormat"/>) or the format inferred from
    /// the endpoint URL.
    /// </summary>
    public OpenAIApiFormat DetectedFormat => _formatOverride ?? DetectFormatFromUrl(Endpoint);

    /// <summary>
    /// Clear any pinned format. The next request will infer the format from
    /// the endpoint URL. Subclasses call this from their Configure() methods.
    /// </summary>
    protected void ResetFormatDetection() => _formatOverride = null;

    /// <summary>
    /// Pin the API format, bypassing URL inspection. Subclasses call this
    /// from Configure() when the user has explicitly chosen a format.
    /// </summary>
    protected void PinFormat(OpenAIApiFormat format)
    {
        if (format == OpenAIApiFormat.Auto)
        {
            throw new ArgumentException(
                "Use ResetFormatDetection() to clear the pinned format.", nameof(format));
        }
        _formatOverride = format;
    }

    /// <summary>
    /// Inspect the endpoint URL path to determine API format. Returns
    /// <see cref="OpenAIApiFormat.ChatCompletions"/> for any URL that
    /// doesn't end with the Responses suffix — Chat Completions is the
    /// safe default since it is supported by virtually every
    /// OpenAI-compatible third-party provider.
    /// </summary>
    internal static OpenAIApiFormat DetectFormatFromUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return OpenAIApiFormat.ChatCompletions;

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
            ? OpenAIApiFormat.Responses
            : OpenAIApiFormat.ChatCompletions;
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
    /// Dispatches to Chat Completions or Responses path based on the endpoint URL.
    /// </summary>
    public virtual async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var strategy = OpenAIFormatStrategies.For(DetectedFormat);
        var messages = BuildChatMessages(request);
        var requestBody = strategy.BuildRequestBody(messages, Model, Temperature);

        await foreach (var chunk in SendAndParseAsync(strategy, requestBody, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Stream grammar correction output using OpenAI-compatible API.
    /// </summary>
    public virtual async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var strategy = OpenAIFormatStrategies.For(DetectedFormat);
        var messages = BuildGrammarCorrectionMessages(request);
        var requestBody = strategy.BuildRequestBody(messages, Model, Temperature);

        await foreach (var chunk in SendAndParseAsync(strategy, requestBody, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
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

    private async IAsyncEnumerable<string> SendAndParseAsync(
        IOpenAIFormatStrategy strategy,
        object requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        if (!string.IsNullOrEmpty(ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }

        ConfigureHttpRequest(httpRequest);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
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
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw CreateErrorFromResponse(response.StatusCode, errorBody);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var chunk in strategy.ParseStreamAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }
}
