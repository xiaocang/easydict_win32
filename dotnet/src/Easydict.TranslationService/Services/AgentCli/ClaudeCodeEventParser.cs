using System.Text.Json;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Parses newline-delimited JSON emitted by `claude -p --output-format stream-json
/// --include-partial-messages` and classifies CLI failures. Pure logic, unit-testable.
/// </summary>
internal static class ClaudeCodeEventParser
{
    /// <summary>Final `result` event payload.</summary>
    public sealed record ResultInfo(bool IsError, string? ResultText);

    private static readonly string[] QuotaPatterns =
    [
        "rate limit",
        "quota",
        "usage limit",
    ];

    private static readonly string[] AuthPatterns =
    [
        "not logged in",
        "please run /login",
        "authentication_failed",
        "authentication failed",
        "unauthorized",
        "not authenticated",
        "invalid api key",
        "oauth token has expired",
    ];

    /// <summary>
    /// Extract an incremental text chunk from a stream_event line:
    /// {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"..."}}}
    /// </summary>
    public static bool TryExtractTextDelta(string line, out string delta)
    {
        delta = "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var type)
                || type != "stream_event"
                || !root.TryGetProperty("event", out var evt)
                || evt.ValueKind != JsonValueKind.Object
                || !TryGetString(evt, "type", out var eventType)
                || eventType != "content_block_delta"
                || !evt.TryGetProperty("delta", out var deltaElement)
                || deltaElement.ValueKind != JsonValueKind.Object
                || !TryGetString(deltaElement, "type", out var deltaType)
                || deltaType != "text_delta"
                || !TryGetString(deltaElement, "text", out var text))
            {
                return false;
            }

            delta = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse the final result event:
    /// {"type":"result","subtype":"success","is_error":false,"result":"full text",...}
    /// Returns null for any other line.
    /// </summary>
    public static ResultInfo? TryParseResult(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var type)
                || type != "result")
            {
                return null;
            }

            var isError = root.TryGetProperty("is_error", out var isErrorElement)
                && isErrorElement.ValueKind == JsonValueKind.True;
            string? resultText = null;
            if (root.TryGetProperty("result", out var resultElement)
                && resultElement.ValueKind == JsonValueKind.String)
            {
                resultText = resultElement.GetString();
            }

            return new ResultInfo(isError, resultText);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classify a CLI failure into a TranslationException, mirroring the upstream
    /// macOS error mapping (quotaExceeded / notLoggedIn / cliError).
    /// </summary>
    public static TranslationException ClassifyFailure(
        string serviceId,
        int exitCode,
        IReadOnlyList<string> controlLines,
        string stdErr)
    {
        var haystack = string.Join('\n', controlLines) + '\n' + stdErr;

        // A rate_limit_event line is an authoritative quota signal.
        if (haystack.Contains("rate_limit_event", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(haystack, QuotaPatterns))
        {
            return new TranslationException(
                "Claude Code usage limit reached. Try again later or check your subscription quota.")
            {
                ErrorCode = TranslationErrorCode.RateLimited,
                ServiceId = serviceId,
            };
        }

        if (ContainsAny(haystack, AuthPatterns))
        {
            return new TranslationException(
                "Claude Code CLI is not signed in. Run `claude` in a terminal and complete /login, then try again.")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = serviceId,
            };
        }

        var detail = AgentCliErrorFormatter.BuildDetail(controlLines, stdErr);
        return new TranslationException(
            $"Claude Code CLI failed (exit code {exitCode}){detail}")
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = serviceId,
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
