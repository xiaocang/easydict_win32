using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.TranslationService.Services;

/// <summary>OpenAI Chat Completions adapter for a Magpie gateway profile.</summary>
public sealed class MagpieService : BaseOpenAIService, IModelCatalogProvider
{
    private string _endpoint = "http://127.0.0.1:3425/v1/chat/completions";
    private string _apiKey = "";
    private string _model = "";
    private bool _allowRemote;

    public MagpieService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "magpie";
    public override string DisplayName => "Magpie";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => IsHttpEndpoint(_endpoint) && !string.IsNullOrWhiteSpace(_model);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;
    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => 0.3;

    public string ModelsEndpoint => GetModelsEndpoint(_endpoint);

    public void Configure(string endpoint, string? apiKey, string? model, bool allowRemote = false)
    {
        _endpoint = endpoint?.Trim() ?? "";
        _apiKey = apiKey?.Trim() ?? "";
        _model = model?.Trim() ?? "";
        _allowRemote = allowRemote;
        ResetFormatDetection();
    }

    public Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        ValidateGatewayAccess();
        return OpenAiCompatibleModelCatalog.FetchAsync(HttpClient, ModelsEndpoint, _apiKey, cancellationToken);
    }

    public override async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateGatewayAccess();
        await foreach (var chunk in base.TranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public override async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateGatewayAccess();
        await foreach (var chunk in base.CorrectGrammarStreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    protected override void ValidateRequest(TranslationRequest request)
    {
        ValidateMagpieConfiguration();
        base.ValidateRequest(request);
    }

    protected override void ValidateConfiguration()
    {
        ValidateMagpieConfiguration();
        base.ValidateConfiguration();
    }

    private void ValidateMagpieConfiguration()
    {
        if (!IsHttpEndpoint(_endpoint))
        {
            throw new TranslationException("Magpie gateway endpoint must use HTTP or HTTPS.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new TranslationException("Choose a model for this Magpie profile before translating.")
            {
                ErrorCode = TranslationErrorCode.InvalidModel,
                ServiceId = ServiceId
            };
        }
    }

    private static bool IsHttpEndpoint(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void ValidateGatewayAccess()
    {
        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Magpie gateway endpoint must use HTTP or HTTPS.");
        }

        var loopback = string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(endpoint.Host, out var address) && System.Net.IPAddress.IsLoopback(address));
        if (loopback) return;

        if (!_allowRemote
            || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(_apiKey)
            || string.Equals(_apiKey, "magpie-easydict", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Remote Magpie gateways require explicit opt-in, HTTPS, and an authorized gateway key.");
        }
    }

    private static string GetModelsEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Magpie endpoint must be an absolute URL.", nameof(endpoint));
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/chat/completions".Length];
        }
        else if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Length == 0 ? "/v1" : path + "/v1";
        }

        var builder = new UriBuilder(uri) { Path = path + "/models", Query = "", Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }
}
