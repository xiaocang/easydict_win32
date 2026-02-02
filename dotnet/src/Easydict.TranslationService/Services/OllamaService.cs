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
