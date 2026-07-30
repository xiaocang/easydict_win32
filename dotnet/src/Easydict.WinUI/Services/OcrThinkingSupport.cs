using System.Collections.Concurrent;
using System.Net;

namespace Easydict.WinUI.Services;

/// <summary>
/// Remembers which OCR endpoints reject the thinking-control field, so it is sent at most
/// once per configuration.
/// </summary>
/// <remarks>
/// Providers disagree about this parameter: Ark/Doubao, GLM and MiMo need
/// <c>thinking: { type: "disabled" }</c> to turn reasoning off, OpenAI answers 400
/// ("Unrecognized request argument supplied: thinking"), and Ollama answers 400
/// ("... does not support thinking") for models built without it. Rather than maintain an
/// allowlist that silently does nothing for unknown providers, the services send the field
/// and drop it for the rest of the session when the provider objects.
/// </remarks>
internal static class OcrThinkingSupport
{
    private static readonly ConcurrentDictionary<string, bool> _rejectedConfigurations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this endpoint/model pair has already rejected the thinking field.
    /// </summary>
    public static bool IsRejectedBy(string endpoint, string model)
        => _rejectedConfigurations.ContainsKey(BuildKey(endpoint, model));

    /// <summary>
    /// Records that this endpoint/model pair rejects the thinking field.
    /// </summary>
    public static void MarkRejectedBy(string endpoint, string model)
        => _rejectedConfigurations[BuildKey(endpoint, model)] = true;

    /// <summary>
    /// Whether a failed response looks like "I don't know this thinking parameter" rather
    /// than a genuine request error worth surfacing to the user.
    /// </summary>
    public static bool IsThinkingFieldRejection(HttpStatusCode statusCode, string? responseBody)
    {
        if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        return !string.IsNullOrEmpty(responseBody)
            && (responseBody.Contains("think", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("reasoning", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Clears the cache so tests do not leak state into each other.
    /// </summary>
    internal static void ResetForTests() => _rejectedConfigurations.Clear();

    private static string BuildKey(string endpoint, string model) => $"{endpoint}|{model}";
}
