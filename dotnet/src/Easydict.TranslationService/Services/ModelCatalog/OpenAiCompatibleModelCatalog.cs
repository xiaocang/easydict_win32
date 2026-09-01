using Easydict.TranslationService;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Easydict.TranslationService.Services.ModelCatalog;

/// <summary>
/// Fetches and parses an OpenAI-compatible <c>/models</c> catalog. Tolerant of schema drift
/// between providers (OpenRouter, OrcaRouter, ...): the envelope may be <c>{"data":[...]}</c>
/// or a bare top-level array, "free" pricing may be a JSON string or a number, and most fields
/// beyond <c>id</c> are optional. A malformed individual entry is skipped rather than failing
/// the whole fetch; only a transport failure or a completely unparseable body throws.
/// </summary>
public static class OpenAiCompatibleModelCatalog
{
    /// <summary>
    /// Model ids known to always be free regardless of what the pricing block says
    /// (aggregator "auto-pick a free model for me" routers).
    /// </summary>
    private static readonly HashSet<string> KnownFreeRouterIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "openrouter/free",
        "orcarouter/free",
    };

    /// <summary>
    /// Fetch and parse the catalog at <paramref name="modelsEndpoint"/>, sorted with free
    /// models first (then by id). Optionally sends a Bearer token, for providers (OrcaRouter)
    /// whose model list requires authentication.
    /// </summary>
    public static async Task<IReadOnlyList<ModelCatalogEntry>> FetchAsync(
        HttpClient httpClient,
        string modelsEndpoint,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TranslationException($"Network error fetching model catalog: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
            };
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException(
                    $"Model catalog request failed ({(int)response.StatusCode}): {body}")
                {
                    ErrorCode = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? TranslationErrorCode.InvalidApiKey
                        : TranslationErrorCode.ServiceUnavailable,
                };
            }

            return Parse(body);
        }
    }

    /// <summary>
    /// Parse a catalog response body into sorted entries. Never throws on malformed
    /// individual entries; throws <see cref="TranslationException"/> only when the body is not
    /// valid JSON or carries no recognizable entry array at all.
    /// </summary>
    public static IReadOnlyList<ModelCatalogEntry> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException($"Model catalog response was not valid JSON: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
            };
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array)
            {
                items = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.Array)
            {
                items = data;
            }
            else
            {
                throw new TranslationException("Model catalog response did not contain a model list")
                {
                    ErrorCode = TranslationErrorCode.InvalidResponse,
                };
            }

            var entries = new List<ModelCatalogEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var entry = TryParseEntry(item);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            return entries
                .OrderByDescending(e => e.IsFree)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static ModelCatalogEntry? TryParseEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string? name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        long? contextLength = TryGetLong(item, "context_length");
        if (contextLength is null &&
            item.TryGetProperty("top_provider", out var topProvider) &&
            topProvider.ValueKind == JsonValueKind.Object)
        {
            contextLength = TryGetLong(topProvider, "context_length");
        }

        var isFree = IsFreeModel(id, item);

        return new ModelCatalogEntry(id, name, isFree, contextLength);
    }

    private static bool IsFreeModel(string id, JsonElement item)
    {
        if (KnownFreeRouterIds.Contains(id))
        {
            return true;
        }

        if (id.EndsWith(":free", StringComparison.OrdinalIgnoreCase) ||
            id.EndsWith("-free", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
        {
            var prompt = TryGetPrice(pricing, "prompt");
            var completion = TryGetPrice(pricing, "completion");

            // Only treat as "known free by price" when both are present and both are zero.
            // Missing pricing information is not evidence of being free.
            if (prompt is not null && completion is not null)
            {
                return prompt.Value == 0 && completion.Value == 0;
            }
        }

        return false;
    }

    private static decimal? TryGetPrice(JsonElement pricing, string propertyName)
    {
        if (!pricing.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var num) => num,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static long? TryGetLong(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var num) => num,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
