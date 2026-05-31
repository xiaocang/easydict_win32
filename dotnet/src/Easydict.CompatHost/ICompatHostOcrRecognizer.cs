using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public interface ICompatHostOcrRecognizer
{
    Task<OcrResultDto> RecognizeAsync(
        OcrRecognizeParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken = default);
}
