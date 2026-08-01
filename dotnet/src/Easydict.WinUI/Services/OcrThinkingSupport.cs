using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

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
    private static readonly ConcurrentDictionary<(string Endpoint, string Model), bool>
        _rejectedConfigurations = new();

    private static readonly string[] _rejectionTerms =
    [
        "unknown",
        "unrecognized",
        "unsupported",
        "not supported",
        "not permitted",
        "does not support",
        "extra_forbidden",
        "extra inputs are not permitted",
    ];


    /// <summary>
    /// Whether this endpoint/model pair has already rejected the thinking field.
    /// </summary>
    public static bool IsRejectedBy(string endpoint, string model)
        => _rejectedConfigurations.ContainsKey((endpoint, model));

    /// <summary>
    /// Records that this endpoint/model pair rejects the thinking field.
    /// </summary>
    public static void MarkRejectedBy(string endpoint, string model)
        => _rejectedConfigurations[(endpoint, model)] = true;

    /// <summary>
    /// Whether a failed response says the exact control field sent by the caller was rejected.
    /// </summary>
    public static bool IsThinkingFieldRejection(
        HttpStatusCode statusCode,
        string? responseBody,
        string fieldName)
    {
        if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        if (HasStructuredFieldParam(responseBody, fieldName))
        {
            return true;
        }

        var namesSentField = ContainsFieldToken(responseBody, fieldName);
        var hasRejectionSemantics = _rejectionTerms.Any(
            term => responseBody.Contains(term, StringComparison.OrdinalIgnoreCase));

        // Ollama names its `think` request field "thinking" in this known rejection.
        return namesSentField && hasRejectionSemantics
            || string.Equals(fieldName, "think", StringComparison.Ordinal)
            && responseBody.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStructuredFieldParam(string responseBody, string fieldName)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var errorElement) &&
                        errorElement.ValueKind == JsonValueKind.Object
                ? errorElement
                : root;

            return error.TryGetProperty("param", out var param) &&
                   param.ValueKind == JsonValueKind.String &&
                   IsSameFieldPath(param.GetString(), fieldName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSameFieldPath(string? value, string fieldName)
    {
        return string.Equals(value, fieldName, StringComparison.OrdinalIgnoreCase)
            || value?.StartsWith(fieldName + ".", StringComparison.OrdinalIgnoreCase) == true
            || value?.StartsWith(fieldName + "[", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsFieldToken(string text, string fieldName)
    {
        var searchStart = 0;

        while (text.IndexOf(fieldName, searchStart, StringComparison.OrdinalIgnoreCase) is var index &&
               index >= 0)
        {
            var end = index + fieldName.Length;
            var hasLeftBoundary = index == 0 || !IsFieldNameCharacter(text[index - 1]);
            var hasRightBoundary = end == text.Length || !IsFieldNameCharacter(text[end]);
            if (hasLeftBoundary && hasRightBoundary)
            {
                return true;
            }

            searchStart = end;
        }

        return false;
    }

    private static bool IsFieldNameCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    /// <summary>
    /// Clears the cache so tests do not leak state into each other.
    /// </summary>
    internal static void ResetForTests() => _rejectedConfigurations.Clear();

}
