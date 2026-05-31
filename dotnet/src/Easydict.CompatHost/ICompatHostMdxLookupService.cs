using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public interface ICompatHostMdxLookupService
{
    Task<MdxLookupResult> LookupAsync(
        MdxLookupParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken = default);
}
