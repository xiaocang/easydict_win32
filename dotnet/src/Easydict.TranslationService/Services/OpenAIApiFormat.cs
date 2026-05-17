namespace Easydict.TranslationService.Services;

/// <summary>
/// OpenAI-compatible API request/response format.
/// </summary>
public enum OpenAIApiFormat
{
    /// <summary>
    /// Format is undetermined. Resolved at first request by URL path inspection,
    /// then by probing the endpoint.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Chat Completions API (POST /v1/chat/completions) — the classic OpenAI format
    /// supported by virtually all OpenAI-compatible third-party providers.
    /// </summary>
    ChatCompletions = 1,

    /// <summary>
    /// Responses API (POST /v1/responses) — OpenAI's newer unified format.
    /// </summary>
    Responses = 2,
}
