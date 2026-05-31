using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public interface ICompatHostLocalAiService
{
    Task<LocalModelStatusDto> PrepareModelAsync(
        PrepareModelParams parameters,
        SettingsSnapshot settings,
        Action<IpcEvent> onEvent,
        CancellationToken cancellationToken = default);

    Task<LocalAiTranslateResult> TranslateAsync(
        LocalAiTranslateParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken = default);
}
