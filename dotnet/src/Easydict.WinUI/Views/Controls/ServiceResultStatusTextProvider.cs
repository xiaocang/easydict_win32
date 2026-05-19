using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;

namespace Easydict.WinUI.Views.Controls;

internal static class ServiceResultStatusTextProvider
{
    public const string PendingQueryStatusKey = "ServiceResult_PendingQueryStatus";
    public const string PendingQueryHintKey = "ServiceResult_PendingQueryHint";
    public const string CheckingKey = "ServiceResult_Checking";
    public const string StreamingKey = "ServiceResult_Streaming";
    public const string NoResultKey = "ServiceResult_NoResult";
    public const string CachedKey = "ServiceResult_Cached";
    public const string WaitingForResponseKey = "ServiceResult_WaitingForResponse";
    public const string TranslationErrorTooltipKey = "ServiceResult_TranslationErrorTooltip";
    public const string RetryKey = "ServiceResult_Retry";

    public static string GetStatusText(ServiceQueryResult serviceResult)
    {
        var loc = LocalizationService.Instance;

        if (serviceResult.ShowPendingQueryHint)
        {
            return loc.GetString(PendingQueryStatusKey);
        }

        if (serviceResult.IsStreaming)
        {
            return loc.GetString(StreamingKey);
        }

        if (serviceResult.IsLoading)
        {
            return loc.GetString(serviceResult.IsGrammarMode ? CheckingKey : "StatusTranslating");
        }

        if (serviceResult.Error != null)
        {
            return loc.GetString("StatusError");
        }

        if (serviceResult.Result?.ResultKind == TranslationResultKind.NoResult)
        {
            return loc.GetString(NoResultKey);
        }

        if (serviceResult.IsGrammarMode && serviceResult.GrammarResult != null)
        {
            return FormatTiming(serviceResult.GrammarResult.TimingMs);
        }

        if (serviceResult.Result != null)
        {
            return serviceResult.Result.FromCache
                ? loc.GetString(CachedKey)
                : FormatTiming(serviceResult.Result.TimingMs);
        }

        return string.Empty;
    }

    public static string GetPendingQueryHintText() =>
        LocalizationService.Instance.GetString(PendingQueryHintKey);

    public static string GetWaitingForResponseText() =>
        LocalizationService.Instance.GetString(WaitingForResponseKey);

    public static string GetErrorFallbackText() =>
        LocalizationService.Instance.GetString("StatusError");

    public static string GetTranslationErrorTooltipText() =>
        LocalizationService.Instance.GetString(TranslationErrorTooltipKey);

    public static string GetRetryText() =>
        LocalizationService.Instance.GetString(RetryKey);

    private static string FormatTiming(long timingMs) => $"{timingMs}ms";
}
