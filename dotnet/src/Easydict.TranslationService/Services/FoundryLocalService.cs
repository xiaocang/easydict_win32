using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Microsoft Foundry Local provider using its OpenAI-compatible local endpoint.
/// The Foundry Local service chooses a dynamic port, so the endpoint can either
/// be configured explicitly or discovered from <c>foundry service status</c>.
/// </summary>
public sealed class FoundryLocalService : BaseOpenAIService, ILocalModelProvider
{
    public const string ServiceIdValue = "foundry-local";
    public const string DefaultModel = "qwen2.5-0.5b";
    public const string InstallDocumentationUrl = "https://learn.microsoft.com/en-us/azure/foundry-local/how-to/how-to-use-foundry-local-cli";

    private readonly IFoundryLocalEndpointResolver _endpointResolver;
    private string _configuredEndpoint = "";
    private string? _resolvedEndpoint;
    private string _model = DefaultModel;
    private string? _resolvedModel;

    public FoundryLocalService(
        HttpClient httpClient,
        IFoundryLocalEndpointResolver? endpointResolver = null)
        : base(httpClient)
    {
        _endpointResolver = endpointResolver ?? new FoundryLocalCliEndpointResolver();
    }

    public override string ServiceId => ServiceIdValue;

    public override string DisplayName => "Foundry Local";

    public override bool RequiresApiKey => false;

    public override bool IsConfigured => !string.IsNullOrWhiteSpace(_model);

    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _resolvedEndpoint ?? _configuredEndpoint;

    public override string ApiKey => "";

    public override string Model => _resolvedModel ?? _model;

    public event EventHandler<LocalModelStatus>? StatusChanged;

    public void Configure(string? endpoint = null, string? model = null)
    {
        _configuredEndpoint = NormalizeChatCompletionsEndpoint(endpoint);
        _resolvedEndpoint = null;
        _resolvedModel = null;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    public LocalModelStatus GetStatus()
    {
        return IsConfigured
            ? new LocalModelStatus(LocalModelState.Ready, "FoundryLocal_Status_Ready")
            : new LocalModelStatus(LocalModelState.Failed, "FoundryLocal_Status_NotConfigured");
    }

    public async Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false);
            var status = GetStatus();
            StatusChanged?.Invoke(this, status);
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var status = new LocalModelStatus(
                LocalModelState.Failed,
                "FoundryLocal_Status_NotRunning",
                DetailMessage: ex.Message);
            StatusChanged?.Invoke(this, status);
            return status;
        }
    }

    public override async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureModelAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var chunk in base.TranslateStreamAsync(request, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public override async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureModelAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var chunk in base.CorrectGrammarStreamAsync(request, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    protected override void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new TranslationException("Foundry Local model is not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidModel,
                ServiceId = ServiceId
            };
        }

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new TranslationException(
                "Foundry Local endpoint is not configured. Start Foundry Local or set the endpoint manually.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }
    }

    private async Task EnsureEndpointAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_configuredEndpoint)
            || !string.IsNullOrWhiteSpace(_resolvedEndpoint))
        {
            return;
        }

        string? resolvedEndpoint;
        try
        {
            resolvedEndpoint = await _endpointResolver
                .ResolveChatCompletionsEndpointAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FoundryLocalCliNotFoundException ex)
        {
            throw new TranslationException(
                "Foundry Local CLI is not installed or is not available on PATH. Install Foundry Local, then start a local model, or configure the endpoint manually. " +
                $"Install guide: {InstallDocumentationUrl}",
                ex)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        if (string.IsNullOrWhiteSpace(resolvedEndpoint))
        {
            throw new TranslationException(
                "Foundry Local service is not running. Start it with the Foundry Local CLI or configure an endpoint.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        _resolvedEndpoint = NormalizeChatCompletionsEndpoint(resolvedEndpoint);
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedModel)
            || string.IsNullOrWhiteSpace(_model)
            || string.IsNullOrWhiteSpace(Endpoint))
        {
            return;
        }

        var modelsEndpoint = GetModelsEndpoint(Endpoint);
        if (string.IsNullOrWhiteSpace(modelsEndpoint))
        {
            return;
        }

        try
        {
            using var response = await HttpClient.GetAsync(modelsEndpoint, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            _resolvedModel = TryResolveModelId(json, _model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[FoundryLocal] Failed to resolve model id: {ex.Message}");
        }
    }

    internal static string? GetModelsEndpoint(string chatCompletionsEndpoint)
    {
        if (!Uri.TryCreate(chatCompletionsEndpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var basePath = path[..^"/chat/completions".Length];
        var builder = new UriBuilder(uri)
        {
            Path = $"{basePath}/models",
            Query = "",
            Fragment = "",
        };
        return builder.Uri.ToString();
    }

    internal static string? TryResolveModelId(string modelListJson, string configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel)
            || string.IsNullOrWhiteSpace(modelListJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(modelListJson);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = data.EnumerateArray()
                .Select(model => model.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();

            var exact = ids.FirstOrDefault(id =>
                string.Equals(id, configuredModel, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            var aliasPrefix = $"{configuredModel}-instruct-";
            var aliasMatches = ids
                .Where(id => id.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetFoundryDevicePreference)
                .ToArray();

            return aliasMatches.FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int GetFoundryDevicePreference(string modelId)
    {
        if (modelId.Contains("openvino-npu", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("-npu", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (modelId.Contains("openvino-gpu", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("-gpu", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (modelId.Contains("-cpu", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    public static string NormalizeChatCompletionsEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "";
        }

        var normalized = endpoint.Trim().TrimEnd('/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.Equals("/openai/status", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = "/v1/chat/completions",
                    Query = "",
                    Fragment = "",
                };
                return builder.Uri.ToString().TrimEnd('/');
            }
        }

        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/chat/completions";
        }

        return $"{normalized}/v1/chat/completions";
    }
}

public sealed class FoundryLocalCliNotFoundException : Exception
{
    public FoundryLocalCliNotFoundException(Exception inner)
        : base("Foundry Local CLI is not installed or is not available on PATH.", inner)
    {
    }
}

public interface IFoundryLocalEndpointResolver
{
    Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken);
}

public sealed class FoundryLocalCliEndpointResolver : IFoundryLocalEndpointResolver
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
    {
        var output = await RunFoundryAsync(["service", "status", "--json"], cancellationToken)
            .ConfigureAwait(false);
        var endpoint = TryExtractEndpoint(output);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        output = await RunFoundryAsync(["service", "status"], cancellationToken)
            .ConfigureAwait(false);
        return TryExtractEndpoint(output);
    }

    public static string? TryExtractEndpoint(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var candidates = UrlRegex.Matches(output)
            .Select(match => match.Value.TrimEnd('.', ',', ';', ')', ']'))
            .Select(FoundryLocalService.NormalizeChatCompletionsEndpoint)
            .Where(endpoint => endpoint.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault(endpoint =>
                endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private static async Task<string> RunFoundryAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "foundry",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new FoundryLocalCliNotFoundException(ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return $"{stdout}{Environment.NewLine}{stderr}";
    }
}
