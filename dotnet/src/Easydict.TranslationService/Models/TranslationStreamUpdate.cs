namespace Easydict.TranslationService.Models;

/// <summary>
/// One update on a rich translation stream. Unlike the plain <c>string</c> chunks of
/// <see cref="IStreamTranslationService"/>, a rich stream can replace the accumulated text
/// (<see cref="TextSnapshot"/>) and can deliver the authoritative, fully structured final
/// result (<see cref="Completed"/>) in the same execution, so consumers never need a second
/// request to obtain dictionary data that only arrives at the end.
/// </summary>
public abstract record TranslationStreamUpdate
{
    private TranslationStreamUpdate()
    {
    }

    /// <summary>Append <see cref="Text"/> to the text accumulated so far.</summary>
    public sealed record TextDelta(string Text) : TranslationStreamUpdate;

    /// <summary>Replace the text accumulated so far with <see cref="Text"/>.</summary>
    public sealed record TextSnapshot(string Text) : TranslationStreamUpdate;

    /// <summary>
    /// The final result. When present it supersedes any accumulated text, including the case where
    /// <see cref="TranslationResult.TranslatedText"/> is empty because the outcome is
    /// <see cref="TranslationResultKind.NoResult"/>.
    /// </summary>
    public sealed record Completed(TranslationResult Result) : TranslationStreamUpdate;
}
