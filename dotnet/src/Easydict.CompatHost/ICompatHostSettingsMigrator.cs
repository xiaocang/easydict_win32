using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public interface ICompatHostSettingsMigrator
{
    Task<SettingsMigrateResult> MigrateAsync(
        SettingsMigrateParams parameters,
        CancellationToken cancellationToken = default);
}
