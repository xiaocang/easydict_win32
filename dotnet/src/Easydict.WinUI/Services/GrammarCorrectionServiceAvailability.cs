using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

internal static class GrammarCorrectionServiceAvailability
{
    public static bool IsAvailable(ITranslationService service, Language sourceLanguage)
    {
        if (service is not IGrammarCorrectionService || !service.IsConfigured)
        {
            return false;
        }

        if (service is LocalAITranslationService localAI)
        {
            return localAI.SupportsGrammarCorrection(sourceLanguage);
        }

        var from = sourceLanguage == Language.Auto ? Language.Auto : sourceLanguage;
        var to = sourceLanguage == Language.Auto ? Language.English : sourceLanguage;
        return service.SupportsLanguagePair(from, to);
    }
}
