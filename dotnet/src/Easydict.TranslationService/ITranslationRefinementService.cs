using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

public sealed record TranslationRefinementRequest(
    string SourceText, string TranslatedText, Language TargetLanguage, string Context);

public sealed record WordAlternatives(string Text, IReadOnlyList<string> Alternatives);

public sealed record WordReplacementRequest(
    TranslationRefinementRequest Translation, int StartIndex, int Length, string Replacement);

public interface IContextTranslationService
{
    Task<string> TranslateWithContextAsync(TranslationRefinementRequest request, CancellationToken cancellationToken = default);
}

public interface IWordRefinementService
{
    /// <summary>Segments must concatenate to the exact input translation, including punctuation and whitespace.</summary>
    Task<IReadOnlyList<WordAlternatives>> GetWordAlternativesAsync(TranslationRefinementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Preserve the prefix and chosen replacement exactly; the remaining text may change for fluency.</summary>
    Task<string> ApplyWordAlternativeAsync(WordReplacementRequest request, CancellationToken cancellationToken = default);
}
