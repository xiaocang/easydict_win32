using Easydict.TranslationService.Models;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Services;

internal enum PhiSilicaModelPreparationPromptResult
{
    NoPrompt,
    Prepared,
    Skipped,
    Disabled
}

internal static class PhiSilicaModelPreparationPromptService
{
    public static bool ShouldPromptForPhiSilicaModel(
        IEnumerable<ServiceQueryResult> serviceResults,
        SettingsService settings)
    {
        return ShouldPromptForPhiSilicaModel(
            serviceResults.Select(result => result.ServiceId),
            settings.LocalAIProvider,
            PhiSilicaAvailability.GetReadyState());
    }

    public static bool ShouldPromptForPhiSilicaModel(
        IEnumerable<string> serviceIds,
        string localAIProvider,
        WindowsAIReadyState readyState)
    {
        if (LocalAIProviderModeExtensions.Parse(localAIProvider) is LocalAIProviderMode.OpenVINO
            or LocalAIProviderMode.FoundryLocal)
        {
            return false;
        }

        return readyState == WindowsAIReadyState.NotReady
            && serviceIds.Any(IsPhiSilicaService);
    }

    public static bool ShouldSkipServiceForCurrentQuery(
        ServiceQueryResult serviceResult,
        PhiSilicaModelPreparationPromptResult promptResult)
    {
        return promptResult is PhiSilicaModelPreparationPromptResult.Skipped or PhiSilicaModelPreparationPromptResult.Disabled
            && IsPhiSilicaService(serviceResult.ServiceId);
    }

    public static bool ShouldQueryServiceForCurrentQuery(
        ServiceQueryResult serviceResult,
        PhiSilicaModelPreparationPromptResult promptResult)
    {
        return serviceResult.EnabledQuery
            && !ShouldSkipServiceForCurrentQuery(serviceResult, promptResult);
    }

    public static async Task<PhiSilicaModelPreparationPromptResult> PromptAndPrepareIfNeededAsync(
        IEnumerable<ServiceQueryResult> serviceResults,
        SettingsService settings,
        XamlRoot? xamlRoot,
        Func<ContentDialog, Task<ContentDialogResult>> showDialogAsync,
        CancellationToken cancellationToken,
        Action<string>? reportPreparationProgress = null)
    {
        var serviceResultList = serviceResults.ToArray();
        if (serviceResultList.Length == 0
            || xamlRoot is null
            || !ShouldPromptForPhiSilicaModel(serviceResultList, settings))
        {
            return PhiSilicaModelPreparationPromptResult.NoPrompt;
        }

        var loc = LocalizationService.Instance;
        var dialog = new ContentDialog
        {
            Title = loc.GetString(PhiSilicaResources.PromptKeys.Title),
            Content = loc.GetString(PhiSilicaResources.PromptKeys.Message),
            PrimaryButtonText = loc.GetString(PhiSilicaResources.PromptKeys.DownloadNow),
            SecondaryButtonText = loc.GetString(PhiSilicaResources.PromptKeys.Disable),
            CloseButtonText = loc.GetString(PhiSilicaResources.PromptKeys.NotNow),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await showDialogAsync(dialog);
        if (result == ContentDialogResult.Secondary)
        {
            settings.DisablePhiSilicaService();
            return PhiSilicaModelPreparationPromptResult.Disabled;
        }

        if (result != ContentDialogResult.Primary)
        {
            return PhiSilicaModelPreparationPromptResult.Skipped;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await PhiSilicaModelPreparationCoordinator.Instance.EnsureReadyAsync(
            reportPreparationProgress,
            cancellationToken);

        return PhiSilicaModelPreparationPromptResult.Prepared;
    }

    private static bool IsPhiSilicaService(string serviceId)
    {
        return string.Equals(serviceId, LocalAITranslationService.ServiceIdValue, StringComparison.OrdinalIgnoreCase);
    }
}
