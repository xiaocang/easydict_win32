using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.BobPlugin.Mapping;

/// <summary>
/// Converts a plugin error into either a neutral "no result" outcome or a
/// <see cref="TranslationException"/>. A lookup that simply found nothing must not be rendered
/// as a failure, which is what makes dictionary and search plugins usable.
/// </summary>
public static class BobErrorMapper
{
    /// <summary>Bob's "nothing found" error type.</summary>
    public const string NotFoundType = "notFound";

    /// <summary>True when the error means "the query ran but matched nothing".</summary>
    public static bool IsNotFound(BobError? error)
        => string.Equals(error?.Type, NotFoundType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Map a <c>notFound</c> error to a neutral result, or return <c>null</c> for real failures.
    /// </summary>
    public static TranslationResult? TryMapToNoResult(BobError error, TranslationRequest request, string serviceDisplayName)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(request);

        if (!IsNotFound(error))
        {
            return null;
        }

        return new TranslationResult
        {
            TranslatedText = string.Empty,
            OriginalText = request.OriginalText ?? request.Text,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = serviceDisplayName,
            ResultKind = TranslationResultKind.NoResult,
            InfoMessage = string.IsNullOrWhiteSpace(error.Message) ? "No result" : error.Message
        };
    }

    /// <summary>Map a plugin failure to the host's exception type.</summary>
    public static TranslationException MapToException(BobError? error, string serviceId)
    {
        var code = MapErrorCode(error?.Type);
        var message = BuildMessage(error, code);

        return new TranslationException(message)
        {
            ErrorCode = code,
            ServiceId = serviceId,
            DocumentationUrl = string.IsNullOrWhiteSpace(error?.TroubleshootingLink) ? null : error!.TroubleshootingLink
        };
    }

    /// <summary>
    /// Bob error types mapped onto Easydict error codes. Both spellings of the unsupported-language
    /// type are accepted because plugins in the wild use each.
    /// </summary>
    private static TranslationErrorCode MapErrorCode(string? type) => type?.ToLowerInvariant() switch
    {
        "unsupportlanguage" or "unsupportedlanguage" => TranslationErrorCode.UnsupportedLanguage,
        "secretkey" => TranslationErrorCode.InvalidApiKey,
        "network" => TranslationErrorCode.NetworkError,
        "api" => TranslationErrorCode.ServiceUnavailable,
        "param" => TranslationErrorCode.InvalidResponse,
        _ => TranslationErrorCode.Unknown
    };

    private static string BuildMessage(BobError? error, TranslationErrorCode code)
    {
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? $"The plugin reported a {code} error."
            : error!.Message!.Trim();

        return string.IsNullOrWhiteSpace(error?.Addition)
            ? message
            : $"{message} ({error!.Addition!.Trim()})";
    }
}
