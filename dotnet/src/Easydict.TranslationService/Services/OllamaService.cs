using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Ollama local LLM translation service.
/// No API key required, runs on localhost.
/// Supports OpenAI-compatible API format.
/// </summary>
public sealed class OllamaService : BaseOpenAIService, IModelCatalogProvider
{
    private const string DefaultEndpoint = "http://localhost:11434/v1/chat/completions";
    private const string DefaultModel = "llama3.2";
    internal const string CloudModelsEndpoint = "https://ollama.com/api/tags";

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

    public string ModelsEndpoint
    {
        get
        {
            var uri = new UriBuilder(_endpoint);
            var path = uri.Path.TrimEnd('/');
            foreach (var suffix in new[] { "/v1/chat/completions", "/v1/responses", "/chat/completions", "/api/chat", "/api/generate", "/api/tags", "/v1" })
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^suffix.Length];
                    break;
                }
            }
            uri.Path = path + "/api/tags";
            uri.Query = "";
            uri.Fragment = "";
            return uri.Uri.AbsoluteUri;
        }
    }

    /// <summary>Combine registered models and cloud recommendations from the configured Ollama server.</summary>
    public async Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = ModelsEndpoint;
        if (string.Equals(new Uri(endpoint).Host, "ollama.com", StringComparison.OrdinalIgnoreCase))
            return await FetchCatalogAsync(endpoint, cancellationToken).ConfigureAwait(false);

        // Recommendations are optional and experimental. Bound discovery independently of inference.
        using var cloudCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cloudCts.CancelAfter(TimeSpan.FromSeconds(3));
        var recommendationsEndpoint = new Uri(new Uri(endpoint), "experimental/model-recommendations").AbsoluteUri;
        var cloudTask = FetchOptionalCloudCatalogAsync(recommendationsEndpoint, cloudCts.Token, cancellationToken);
        IReadOnlyList<ModelCatalogEntry> local = [];
        TranslationException? localError = null;
        try
        {
            local = await FetchCatalogAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            localError = ex;
        }

        var cloud = await cloudTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (localError is not null && cloud.Count == 0)
            throw localError;

        return local.Concat(cloud)
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<ModelCatalogEntry>> FetchOptionalCloudCatalogAsync(
        string recommendationsEndpoint, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                return await FetchCatalogAsync(recommendationsEndpoint, timeoutToken, recommendations: true).ConfigureAwait(false);
            }
            catch (UnsupportedModelRecommendationsException)
            {
                // Older servers lack this endpoint. Public catalog entries are suggestions,
                // not evidence of the signed-in user's access. Keep local results first.
                var catalog = await FetchCatalogAsync(CloudModelsEndpoint, timeoutToken).ConfigureAwait(false);
                return catalog.Select(model => model with
                {
                    Id = ToLocalCloudModelId(model.Id), Name = "Ollama Cloud (catalog)"
                }).ToList();
            }
        }
        catch (TranslationException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private sealed class UnsupportedModelRecommendationsException : Exception { }

    private static string ToLocalCloudModelId(string id) =>
        id.EndsWith(":cloud", StringComparison.Ordinal) || id.EndsWith("-cloud", StringComparison.Ordinal)
            ? id : id.Contains(':') ? id + "-cloud" : id + ":cloud";

    private async Task<IReadOnlyList<ModelCatalogEntry>> FetchCatalogAsync(
        string endpoint, CancellationToken cancellationToken, bool recommendations = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await HttpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (recommendations && response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
                throw new UnsupportedModelRecommendationsException();
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException($"Ollama model catalog request failed ({(int)response.StatusCode}).")
                {
                    ErrorCode = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? TranslationErrorCode.RateLimited : TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId
                };
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(recommendations ? "recommendations" : "models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                throw new TranslationException("Ollama model catalog response did not contain a model list.")
                {
                    ErrorCode = TranslationErrorCode.InvalidResponse,
                    ServiceId = ServiceId
                };
            }

            if (recommendations)
            {
                // Local recommendations already carry callable IDs, including cloud aliases.
                // Do not add downloadable local recommendations that are not registered.
                return models.EnumerateArray()
                    .Where(model => model.ValueKind == JsonValueKind.Object)
                    .Where(model => model.TryGetProperty("model", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(model => new ModelCatalogEntry(
                        model.GetProperty("model").GetString()!,
                        model.TryGetProperty("required_plan", out var plan) && plan.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(plan.GetString())
                            ? $"Ollama Cloud (recommended; {plan.GetString()})" : "Ollama Cloud (recommended)",
                        false,
                        model.TryGetProperty("context_length", out var context) && context.ValueKind == JsonValueKind.Number
                            && context.TryGetInt64(out var length) && length > 0
                            ? length : null))
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id)
                        && (model.Id.EndsWith(":cloud", StringComparison.Ordinal) || model.Id.EndsWith("-cloud", StringComparison.Ordinal)))
                    .DistinctBy(model => model.Id, StringComparer.Ordinal)
                    .ToList();
            }

            return models.EnumerateArray()
                .Where(model => model.ValueKind == JsonValueKind.Object)
                .Select(model => model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : model.TryGetProperty("model", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Select(name => new ModelCatalogEntry(name!,
                    new Uri(endpoint).Host.Equals("ollama.com", StringComparison.OrdinalIgnoreCase)
                        || name!.EndsWith(":cloud", StringComparison.Ordinal) || name.EndsWith("-cloud", StringComparison.Ordinal)
                        ? "Ollama Cloud" : null, false, null))
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new TranslationException("Ollama model catalog response was not valid JSON.", ex)
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TranslationException("Network error fetching Ollama model catalog.", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = ServiceId
            };
        }
    }

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
                    .Replace("{to}", request.AutomaticDirection is null
                        ? request.ToLanguage.GetDisplayName()
                        : "the target language selected by the first/second-language rule in the user message", StringComparison.Ordinal);
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
            var models = await FetchCatalogAsync(ModelsEndpoint, cancellationToken).ConfigureAwait(false);
            _availableModels = models.Select(model => model.Id).ToList();

            // Preserve the legacy refresh behavior; the settings catalog fetch never changes the model.
            if (_availableModels.Count > 0 && !_availableModels.Contains(_model))
            {
                _model = _availableModels[0];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
