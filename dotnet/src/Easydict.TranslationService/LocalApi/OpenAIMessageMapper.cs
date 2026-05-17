using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LocalApi;

/// <summary>
/// Pure mapper from an OpenAI-compatible <see cref="ChatRequest"/> to an internal
/// <see cref="TranslationRequest"/>. Resolution order:
///
/// 1. <c>extra_body.easydict.{target_language, source_language}</c> ISO codes (canonical path).
/// 2. Regex on the last <c>system</c> message: <c>translate (from X )?(in)?to Y</c>.
/// 3. Fallback: <see cref="Language.Auto"/> source, caller-supplied default target.
///
/// Text is the concatenation of all <c>user</c> role messages joined by <c>\n\n</c>.
/// Image/non-text content parts are ignored.
/// </summary>
public static class OpenAIMessageMapper
{
    private static readonly Regex TranslatePromptRegex = new(
        @"translate\s+(?:from\s+([a-z][a-z]+(?:[-_][a-z0-9]+)?)\s+)?(?:in)?to\s+([a-z][a-z]+(?:[-_][a-z0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record Result(TranslationRequest? Request, string? Error);

    public static Result Map(ChatRequest req, Language defaultTarget)
    {
        if (req.Messages is null || req.Messages.Count == 0)
            return new Result(null, "messages must be a non-empty array");

        var text = ExtractUserText(req.Messages);
        if (string.IsNullOrWhiteSpace(text))
            return new Result(null, "user message content is empty");

        var from = Language.Auto;
        var to = defaultTarget;
        var sourceFound = false;
        var targetFound = false;

        // 1. extra_body.easydict.{target_language, source_language}
        if (req.ExtraBody is { ValueKind: JsonValueKind.Object } extra &&
            extra.TryGetProperty("easydict", out var easydictExt) &&
            easydictExt.ValueKind == JsonValueKind.Object)
        {
            if (easydictExt.TryGetProperty("target_language", out var t) && t.ValueKind == JsonValueKind.String &&
                LanguageCodes.TryParseIsoCode(t.GetString(), out var parsedTo))
            {
                to = parsedTo;
                targetFound = true;
            }
            if (easydictExt.TryGetProperty("source_language", out var s) && s.ValueKind == JsonValueKind.String &&
                LanguageCodes.TryParseIsoCode(s.GetString(), out var parsedFrom))
            {
                from = parsedFrom;
                sourceFound = true;
            }
        }

        // 2. Regex over last system message (only fill what extra_body did not provide)
        if (!targetFound || !sourceFound)
        {
            var systemText = FindLastSystemText(req.Messages);
            if (!string.IsNullOrEmpty(systemText))
            {
                var m = TranslatePromptRegex.Match(systemText);
                if (m.Success)
                {
                    if (!targetFound &&
                        LanguageCodes.TryParseIsoCode(NormalizeIsoCandidate(m.Groups[2].Value), out var parsedTo))
                    {
                        to = parsedTo;
                    }
                    if (!sourceFound && m.Groups[1].Success &&
                        LanguageCodes.TryParseIsoCode(NormalizeIsoCandidate(m.Groups[1].Value), out var parsedFrom))
                    {
                        from = parsedFrom;
                    }
                }
            }
        }

        return new Result(
            new TranslationRequest
            {
                Text = text,
                FromLanguage = from,
                ToLanguage = to,
            },
            null);
    }

    private static string ExtractUserText(IList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var m in messages)
        {
            if (!string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            var chunk = ExtractContentText(m.Content);
            if (string.IsNullOrEmpty(chunk))
                continue;
            if (!first) sb.Append("\n\n");
            sb.Append(chunk);
            first = false;
        }
        return sb.ToString();
    }

    private static string? FindLastSystemText(IList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractContentText(m.Content);
            }
        }
        return null;
    }

    /// <summary>
    /// Pull text from either a string content field or an OpenAI vision-style array of parts.
    /// Returns the joined text of all <c>type: "text"</c> parts; ignores everything else.
    /// </summary>
    private static string ExtractContentText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    var type = part.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String
                        ? tp.GetString()
                        : null;
                    if (type != "text") continue;
                    if (part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(tx.GetString());
                    }
                }
                return sb.ToString();
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Convert a regex-captured language token like <c>chinese</c> or <c>english</c> to an ISO
    /// code our parser recognizes. Short BCP-47 codes pass through unchanged.
    /// </summary>
    private static string NormalizeIsoCandidate(string candidate)
    {
        var lower = candidate.Trim().ToLowerInvariant();
        return lower switch
        {
            "english" => "en",
            "chinese" or "mandarin" => "zh-CN",
            "simplified" => "zh-CN",
            "traditional" => "zh-TW",
            "japanese" => "ja",
            "korean" => "ko",
            "french" => "fr",
            "german" => "de",
            "spanish" => "es",
            "italian" => "it",
            "portuguese" => "pt",
            "russian" => "ru",
            "arabic" => "ar",
            _ => lower,
        };
    }
}
