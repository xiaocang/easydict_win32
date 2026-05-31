using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public interface ICompatHostLongDocTranslator
{
    Task<TranslateDocumentResult> TranslateAsync(
        TranslateDocumentParams parameters,
        SettingsSnapshot settings,
        Action<IpcEvent> onEvent,
        CancellationToken cancellationToken = default);
}
