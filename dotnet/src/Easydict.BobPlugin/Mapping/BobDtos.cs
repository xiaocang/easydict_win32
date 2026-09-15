using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.BobPlugin.Mapping;

/// <summary>
/// Shapes of the JSON a plugin hands back through its completion callback.
/// Everything is optional and unknown fields are ignored: plugins in the wild vary, and a missing
/// field must degrade rather than fail. Verify against https://bobtranslate.com/plugin.
/// </summary>
public static class BobJson
{
    /// <summary>Deserializer settings shared by every plugin payload.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

/// <summary>What a plugin passes to <c>completion</c> / <c>onCompletion</c>: a result or an error.</summary>
public sealed class BobEnvelope
{
    public BobResult? Result { get; set; }
    public BobError? Error { get; set; }
}

/// <summary>A successful plugin result.</summary>
public sealed class BobResult
{
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>Translated text, one entry per paragraph.</summary>
    public List<string>? ToParagraphs { get; set; }

    /// <summary>Source text as the plugin segmented it (unused by the host).</summary>
    public List<string>? FromParagraphs { get; set; }

    /// <summary>Dictionary data for single-word queries.</summary>
    public BobDict? ToDict { get; set; }
}

/// <summary>Dictionary section of a plugin result.</summary>
public sealed class BobDict
{
    public string? Word { get; set; }
    public List<BobPhonetic>? Phonetics { get; set; }
    public List<BobPart>? Parts { get; set; }
    public List<BobExchange>? Exchanges { get; set; }
    public List<BobRelatedWordPart>? RelatedWordParts { get; set; }
    public List<BobAddition>? Additions { get; set; }
}

/// <summary>One pronunciation.</summary>
public sealed class BobPhonetic
{
    /// <summary>"us" or "uk" in practice.</summary>
    public string? Type { get; set; }

    /// <summary>The phonetic transcription.</summary>
    public string? Value { get; set; }

    public BobTts? Tts { get; set; }
}

/// <summary>Audio for a pronunciation.</summary>
public sealed class BobTts
{
    /// <summary>"url" or "base64"; only "url" is used by the host.</summary>
    public string? Type { get; set; }

    public string? Value { get; set; }
    public string? AudioType { get; set; }
}

/// <summary>Meanings grouped by part of speech.</summary>
public sealed class BobPart
{
    public string? Part { get; set; }
    public List<string>? Means { get; set; }
}

/// <summary>An inflection group ("plural", "past tense", ...).</summary>
public sealed class BobExchange
{
    public string? Name { get; set; }
    public List<string>? Words { get; set; }
}

/// <summary>Related words grouped by part of speech.</summary>
public sealed class BobRelatedWordPart
{
    public string? Part { get; set; }
    public List<BobRelatedWord>? Words { get; set; }
}

/// <summary>One related word and its meanings.</summary>
public sealed class BobRelatedWord
{
    public string? Word { get; set; }
    public List<string>? Means { get; set; }
}

/// <summary>Free-form extra section a plugin wants to show.</summary>
public sealed class BobAddition
{
    public string? Name { get; set; }
    public string? Value { get; set; }
}

/// <summary>A plugin-reported failure.</summary>
public sealed class BobError
{
    /// <summary>
    /// One of unknown, param, unsupportLanguage, secretKey, network, api, notFound.
    /// </summary>
    public string? Type { get; set; }

    public string? Message { get; set; }
    public string? Addition { get; set; }
    public string? TroubleshootingLink { get; set; }
}
