using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

/// <summary>
/// Routes one automatic query without making prompt-capable services wait for online detection.
/// Detection starts only when a service needs an explicit target, and is shared by that query.
/// </summary>
public sealed class AutomaticTranslationRouter
{
    private readonly AutomaticLanguageDirection _direction;
    private readonly Lazy<Task<Language>> _detection;
    private readonly Func<Language, Language> _selectTarget;

    public AutomaticTranslationRouter(
        AutomaticLanguageDirection direction,
        Func<Task<Language>> detect,
        Func<Language, Language> selectTarget)
    {
        _direction = direction;
        _detection = new Lazy<Task<Language>>(detect);
        _selectTarget = selectTarget;
    }

    public async Task<TranslationRequest> CreateRequestAsync(
        ITranslationService service, TranslationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var routing = request.Diagnostics?.Measure(TranslationDiagnosticStage.LanguageRouting);
        using var cancellationRegistration = cancellationToken.Register(() => request.Diagnostics?.Complete(TranslationDiagnosticStatus.Cancelled));
        var canChooseDirection = service.SupportsAutomaticLanguageDirection
            && _direction.FirstLanguage != Language.Auto
            && _direction.SecondLanguage != Language.Auto
            && _direction.FirstLanguage != _direction.SecondLanguage;
        var source = canChooseDirection
            ? Language.Auto
            : await _detection.Value.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new TranslationRequest
        {
            Text = request.Text,
            FromLanguage = source,
            ToLanguage = canChooseDirection ? Language.Auto : _selectTarget(source),
            AutomaticDirection = canChooseDirection ? _direction : null,
            CustomPrompt = request.CustomPrompt,
            OriginalText = request.OriginalText,
            DetectedFromLanguage = source == Language.Auto ? null : source,
            TimeoutMs = request.TimeoutMs,
            BypassCache = request.BypassCache,
            Diagnostics = request.Diagnostics
        };
    }
}
