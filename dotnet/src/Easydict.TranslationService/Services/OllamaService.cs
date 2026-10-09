using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Ollama local LLM translation service.
/// No API key required, runs on localhost.
/// Supports OpenAI-compatible API format.
/// </summary>
public sealed class OllamaService : BaseOpenAIService
{
    private const string DefaultEndpoint = "http://localhost:11434/v1/chat/completions";
    private const string DefaultModel = "llama3.2";

    /// <summary>
    /// Output-token floor. High enough that a thinking model (qwen3, deepseek-r1) can finish
    /// its reasoning before the answer on a short query, which counts against the same limit.
    /// </summary>
    internal const int MinOutputTokens = 2048;

    /// <summary>Output-token ceiling, reached only by very long inputs.</summary>
    internal const int MaxOutputTokensCeiling = 16384;

    /// <summary>
    /// Short imperative prompt for <see cref="OllamaPromptStyle.Concise"/>. The user message
    /// already names the source and target languages.
    /// </summary>
    internal const string ConciseSystemPrompt = """
        You are a translation engine. Output only the translation of the given text: no explanations, notes, quotation marks or original text. Write entirely in the target language and never keep words from the source language. Translate idioms by meaning, not word for word.
        """;

    private static readonly IReadOnlyList<Language> _ollamaLanguages = new[]
    {
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.Spanish,
        Language.German,
        Language.Russian,
        Language.Italian,
        Language.Portuguese,
        Language.Dutch,
        Language.Polish,
        Language.Vietnamese,
        Language.Thai,
        Language.Arabic,
        Language.Turkish,
        Language.Indonesian
    };

    private string _endpoint = DefaultEndpoint;
    private string _model = DefaultModel;
    private List<string> _availableModels = new();
    private OllamaPromptStyle _promptStyle = OllamaPromptStyle.Standard;
    private string _customPrompt = "";

    public OllamaService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "ollama";
    public override string DisplayName => "Ollama";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => true; // Always configured (local service)
    public override IReadOnlyList<Language> SupportedLanguages => _ollamaLanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => ""; // No API key needed
    public override string Model => _model;

    /// <summary>
    /// Ollama generates until end-of-sequence by default and serves one request at a time,
    /// so a small model that rambles holds every later query behind it. Bound the output
    /// with room to spare: a translation is rarely longer than a few tokens per input char.
    /// </summary>
    protected override int? GetMaxOutputTokens(string inputText)
        => GetOutputTokenLimit(inputText.Length);

    internal static int GetOutputTokenLimit(int inputLength)
        => (int)Math.Clamp(inputLength * 4L, MinOutputTokens, MaxOutputTokensCeiling);

    /// <summary>
    /// List of locally available Ollama models.
    /// Call RefreshLocalModelsAsync() to populate.
    /// </summary>
    public IReadOnlyList<string> AvailableModels => _availableModels;

    /// <summary>
    /// Configure the Ollama service.
    /// </summary>
    /// <param name="endpoint">Ollama API endpoint (optional, defaults to localhost:11434).</param>
    /// <param name="model">Model to use (optional, defaults to llama3.2).</param>
    public void Configure(string? endpoint = null, string? model = null)
    {
        if (!string.IsNullOrEmpty(endpoint))
            _endpoint = endpoint;
        if (!string.IsNullOrEmpty(model))
            _model = model;
    }

    /// <summary>
    /// Choose the translation system prompt. A <see cref="OllamaPromptStyle.Custom"/> prompt
    /// may use <c>{from}</c> and <c>{to}</c> for the language names; a blank custom prompt
    /// falls back to the standard one.
    /// </summary>
    public void ConfigurePrompt(OllamaPromptStyle style, string? customPrompt = null)
    {
        _promptStyle = style;
        _customPrompt = customPrompt?.Trim() ?? "";
    }

    public OllamaPromptStyle PromptStyle => _promptStyle;

    protected override string GetTranslationSystemPrompt(TranslationRequest request)
    {
        switch (_promptStyle)
        {
            case OllamaPromptStyle.Concise:
                return ConciseSystemPrompt;
            case OllamaPromptStyle.Custom when _customPrompt.Length > 0:
                var source = request.FromLanguage == Language.Auto
                    ? "the detected language"
                    : request.FromLanguage.GetDisplayName();
                return _customPrompt
                    .Replace("{from}", source, StringComparison.Ordinal)
                    .Replace("{to}", request.ToLanguage.GetDisplayName(), StringComparison.Ordinal);
            default:
                return base.GetTranslationSystemPrompt(request);
        }
    }

    /// <summary>
    /// Fetch available models from Ollama API (/api/tags).
    /// </summary>
    public async Task RefreshLocalModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract base URL from endpoint
            var endpointUri = new Uri(_endpoint);
            var tagsUrl = $"{endpointUri.Scheme}://{endpointUri.Host}:{endpointUri.Port}/api/tags";

            var response = await HttpClient.GetStringAsync(tagsUrl, cancellationToken);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                _availableModels = models.EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Cast<string>()
                    .ToList();

                // Set default model if current model not available and we have models
                if (_availableModels.Count > 0 && !_availableModels.Contains(_model))
                {
                    _model = _availableModels[0];
                }
            }
        }
        catch
        {
            // Ollama may not be running - set default model list
            _availableModels = new List<string> { DefaultModel };
        }
    }

    /// <summary>
    /// Override validation to not require API key and to check Ollama availability.
    /// </summary>
    protected override void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(Endpoint))
        {
            throw new TranslationException("Ollama endpoint is not configured")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        // Don't validate API key for Ollama
    }
}
