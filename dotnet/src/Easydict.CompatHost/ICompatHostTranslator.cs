using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public interface ICompatHostTranslator
{
    Task<TranslationResultDto> TranslateAsync(
        TranslateParams parameters,
        CancellationToken cancellationToken = default);

    Task<TranslationResultDto> TranslateStreamAsync(
        TranslateParams parameters,
        Func<string, CancellationToken, Task> onChunkAsync,
        CancellationToken cancellationToken = default);

    Task<GrammarCorrectResultDto> CorrectGrammarAsync(
        GrammarCorrectParams parameters,
        Func<string, CancellationToken, Task> onChunkAsync,
        CancellationToken cancellationToken = default);
}
