using System.Text.Json;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Parses newline-delimited JSON emitted by `codex exec --json` and classifies
/// CLI failures. Pure logic, unit-testable.
/// </summary>
internal static class CodexCliEventParser
{
    private static readonly string[] AuthPatterns =
    [
        "not signed in",
        "not logged in",
        "codex login",
        "authentication_failed",
        "authentication failed",
        "unauthorized",
        "not authenticated",
        "openai_api_key",
        "invalid api key",
        "401",
    ];

    private static readonly string[] QuotaPatterns =
    [
        "rate limit",
        "quota",
        "usage limit",
        "insufficient_quota",
        "insufficient quota",
        "credit",
        "429",
    ];

    /// <summary>
    /// Extract the assistant text from an item.completed event:
    /// {"type":"item.completed","item":{"type":"agent_message","text":"..."}}
    /// Returns null for any other line.
    /// </summary>
    public static string? TryExtractAgentMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var type)
                || type != "item.completed"
                || !root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object
                || !TryGetString(item, "type", out var itemType)
                || itemType != "agent_message"
                || !TryGetString(item, "text", out var text))
            {
                return null;
            }

            return text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extract an error message from terminal failure events: turn.failed,
    /// thread.failed, error, or item.completed with item.type == "error".
    /// The error payload may be a plain string or an object with "message".
    /// Returns null for non-error lines.
    /// </summary>
    public static string? TryExtractErrorMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var type))
            {
                return null;
            }

            switch (type)
            {
                case "turn.failed":
                case "thread.failed":
                    return ExtractErrorText(root) ?? "CLI reported a failed turn.";
                case "error":
                    return ExtractErrorText(root)
                        ?? (TryGetString(root, "message", out var message) ? message : "CLI reported an error.");
                case "item.completed":
                    if (root.TryGetProperty("item", out var item)
                        && item.ValueKind == JsonValueKind.Object
                        && TryGetString(item, "type", out var itemType)
                        && itemType == "error")
                    {
                        return TryGetString(item, "text", out var text) ? text : "CLI reported an error item.";
                    }

                    return null;
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classify a CLI failure into a TranslationException, mirroring the upstream
    /// macOS error mapping (notLoggedIn / quotaExceeded / cliError).
    /// </summary>
    public static TranslationException ClassifyFailure(
        string serviceId,
        int exitCode,
        IReadOnlyList<string> controlLines,
        string stdErr)
    {
        var errorMessages = controlLines
            .Select(TryExtractErrorMessage)
            .Where(static message => message != null)
            .ToList();
        var haystack = string.Join('\n', errorMessages) + '\n' + stdErr;

        if (ContainsAny(haystack, AuthPatterns))
        {
            return new TranslationException(
                "Codex CLI is not signed in. Run `codex login` in a terminal, then try again.")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = serviceId,
            };
        }

        if (ContainsAny(haystack, QuotaPatterns))
        {
            return new TranslationException(
                "Codex usage limit reached. Try again later or check your subscription quota.")
            {
                ErrorCode = TranslationErrorCode.RateLimited,
                ServiceId = serviceId,
            };
        }

        var detail = errorMessages.Count > 0
            ? $": {string.Join("; ", errorMessages)}"
            : AgentCliErrorFormatter.BuildDetail(controlLines, stdErr);
        return new TranslationException(
            $"Codex CLI failed (exit code {exitCode}){detail}")
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = serviceId,
        };
    }

    private static string? ExtractErrorText(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
        {
            return null;
        }

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString(),
            JsonValueKind.Object when error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String => message.GetString(),
            _ => null,
        };
    }

    private static bool ContainsAny(string haystack, string[] patterns)
    {
        return patterns.Any(pattern => haystack.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        return false;
    }
}
