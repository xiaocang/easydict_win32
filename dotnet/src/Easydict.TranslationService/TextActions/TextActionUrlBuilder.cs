using System.Text.RegularExpressions;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.TextActions;

/// <summary>
/// Expands <see cref="TextAction.UrlTemplate"/> placeholders and validates the result.
/// Only absolute http/https URLs are ever produced; every substituted value is percent-encoded.
/// </summary>
public static partial class TextActionUrlBuilder
{
    public const string EncodedTextPlaceholder = "{encodedText}";
    public const string EncodedTranslationPlaceholder = "{encodedTranslation}";
    public const string FromPlaceholder = "{from}";
    public const string ToPlaceholder = "{to}";

    private static readonly string[] KnownPlaceholders =
    [
        EncodedTextPlaceholder,
        EncodedTranslationPlaceholder,
        FromPlaceholder,
        ToPlaceholder
    ];

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Build the URL for an <see cref="TextActionType.OpenUrl"/> action.
    /// </summary>
    public static bool TryBuildUri(TextAction action, TextActionContext context, out Uri? uri, out string? error)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        uri = null;
        error = null;

        if (action.Type != TextActionType.OpenUrl)
        {
            error = "Action is not an OpenUrl action.";
            return false;
        }

        var templateError = ValidateTemplate(action.UrlTemplate);
        if (templateError is not null)
        {
            error = templateError;
            return false;
        }

        var text = context.Text ?? string.Empty;
        var translation = string.IsNullOrWhiteSpace(context.Translation) ? text : context.Translation!;

        var expanded = action.UrlTemplate!
            .Replace(EncodedTextPlaceholder, Uri.EscapeDataString(text), StringComparison.Ordinal)
            .Replace(EncodedTranslationPlaceholder, Uri.EscapeDataString(translation), StringComparison.Ordinal)
            .Replace(FromPlaceholder, Uri.EscapeDataString(context.From.ToCode()), StringComparison.Ordinal)
            .Replace(ToPlaceholder, Uri.EscapeDataString(context.To.ToCode()), StringComparison.Ordinal);

        if (!Uri.TryCreate(expanded, UriKind.Absolute, out var candidate) || !IsWebScheme(candidate))
        {
            error = "The expanded URL is not a valid http(s) address.";
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>
    /// Validate a template without a context. Returns <c>null</c> when valid, otherwise a short reason.
    /// </summary>
    public static string? ValidateTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "URL template is empty.";
        }

        var trimmed = template.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "URL template must start with http:// or https://.";
        }

        if (!trimmed.Contains(EncodedTextPlaceholder, StringComparison.Ordinal)
            && !trimmed.Contains(EncodedTranslationPlaceholder, StringComparison.Ordinal))
        {
            return $"URL template must contain {EncodedTextPlaceholder} or {EncodedTranslationPlaceholder}.";
        }

        foreach (Match match in PlaceholderRegex().Matches(trimmed))
        {
            if (Array.IndexOf(KnownPlaceholders, match.Value) < 0)
            {
                return $"Unknown placeholder {match.Value}.";
            }
        }

        // Substitute sample values to catch templates that only break once expanded.
        var probe = trimmed
            .Replace(EncodedTextPlaceholder, "probe", StringComparison.Ordinal)
            .Replace(EncodedTranslationPlaceholder, "probe", StringComparison.Ordinal)
            .Replace(FromPlaceholder, "en", StringComparison.Ordinal)
            .Replace(ToPlaceholder, "zh", StringComparison.Ordinal);
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var probeUri) || !IsWebScheme(probeUri))
        {
            return "URL template does not form a valid http(s) address.";
        }

        return null;
    }

    private static bool IsWebScheme(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
