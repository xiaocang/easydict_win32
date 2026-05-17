using System.Diagnostics;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;

namespace Easydict.WinUI.Services;

internal static class FoundryLocalRecoveryCoordinator
{
    public static async Task StartAndRetryAsync(
        ServiceQueryResult serviceResult,
        Func<CancellationToken, Task<LocalModelStatus>> prepareAsync,
        Func<ServiceQueryResult, CancellationToken, Task> retryAsync,
        Action<ServiceQueryResult> refresh,
        Func<LocalModelStatus, TranslationException>? createRecoveryException = null,
        Func<bool>? isAborted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceResult);
        ArgumentNullException.ThrowIfNull(prepareAsync);
        ArgumentNullException.ThrowIfNull(retryAsync);
        ArgumentNullException.ThrowIfNull(refresh);

        createRecoveryException ??= CreateRecoveryException;

        if (serviceResult.IsLoading || IsAborted(isAborted))
        {
            return;
        }

        ResetForStart(serviceResult);
        refresh(serviceResult);

        try
        {
            var status = await prepareAsync(cancellationToken);
            if (IsAborted(isAborted))
            {
                StopLoading(serviceResult);
                refresh(serviceResult);
                return;
            }

            if (status.State != LocalModelState.Ready)
            {
                serviceResult.Error = createRecoveryException(status);
                StopLoading(serviceResult);
                serviceResult.ApplyAutoCollapseLogic();
                refresh(serviceResult);
                return;
            }

            serviceResult.IsLoading = false;
            serviceResult.ClearQueried();
            refresh(serviceResult);

            await retryAsync(serviceResult, cancellationToken);

            if (!serviceResult.IsStreaming)
            {
                serviceResult.IsLoading = false;
                serviceResult.ApplyAutoCollapseLogic();
                refresh(serviceResult);
            }
        }
        catch (OperationCanceledException)
        {
            StopLoading(serviceResult);
            serviceResult.ClearQueried();
            refresh(serviceResult);
        }
        catch (TranslationException ex)
        {
            Debug.WriteLine(
                $"[FoundryLocalRecovery] Retry failed: code={ex.ErrorCode}, recovery={ex.RecoveryAction}, message={FoundryLocalService.TrimForLog(ex.Message)}");
            serviceResult.Error = ex;
            StopLoading(serviceResult);
            serviceResult.ApplyAutoCollapseLogic();
            refresh(serviceResult);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[FoundryLocalRecovery] Start/retry failed: {ex.GetType().Name}: {FoundryLocalService.TrimForLog(ex.Message)}");
            serviceResult.Error = CreateStartException(ex);
            StopLoading(serviceResult);
            serviceResult.ApplyAutoCollapseLogic();
            refresh(serviceResult);
        }
    }

    public static TranslationException CreateRecoveryException(LocalModelStatus status)
    {
        var loc = LocalizationService.Instance;
        var message = loc.GetString(status.ResourceKey);
        if (!string.IsNullOrWhiteSpace(status.DetailMessage))
        {
            message = $"{message}\n\n{status.DetailMessage.Trim()}";
        }

        return new TranslationException(message)
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = FoundryLocalService.ServiceIdValue,
            RecoveryAction = status.State == LocalModelState.NotCompatible
                ? FoundryLocalResources.InstallRecoveryAction
                : FoundryLocalResources.StartRecoveryAction,
            DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
        };
    }

    private static bool IsAborted(Func<bool>? isAborted) => isAborted?.Invoke() == true;

    private static void ResetForStart(ServiceQueryResult serviceResult)
    {
        serviceResult.Error = null;
        serviceResult.Result = null;
        serviceResult.GrammarResult = null;
        serviceResult.IsStreaming = false;
        serviceResult.StreamingText = "";
        serviceResult.IsLoading = true;
    }

    private static void StopLoading(ServiceQueryResult serviceResult)
    {
        serviceResult.IsLoading = false;
        serviceResult.IsStreaming = false;
        serviceResult.StreamingText = "";
    }

    private static TranslationException CreateStartException(Exception ex)
    {
        return new TranslationException(ex.Message)
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = FoundryLocalService.ServiceIdValue,
            RecoveryAction = FoundryLocalResources.StartRecoveryAction,
            DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
        };
    }
}
