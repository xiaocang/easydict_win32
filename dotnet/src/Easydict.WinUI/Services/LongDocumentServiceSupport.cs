using Easydict.TranslationService;

namespace Easydict.WinUI.Services;

internal static class LongDocumentServiceSupport
{
    public static bool IsSupported(ITranslationService service)
    {
        if (string.Equals(service.ServiceId, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(service.ServiceId, LocalAITranslationService.ServiceIdValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return service is IStreamTranslationService;
    }
}
