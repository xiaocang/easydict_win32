using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;

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

    public static bool IsReadyForSelection(
        ITranslationService service,
        IReadOnlyDictionary<string, bool> serviceTestStatus)
    {
        if (!service.IsConfigured)
        {
            return false;
        }

        if (service is ILocalModelProvider localModelProvider)
        {
            return localModelProvider.GetStatus().State == LocalModelState.Ready;
        }

        return serviceTestStatus.TryGetValue(service.ServiceId, out var passed) && passed;
    }
}
