using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public sealed class CompatHostRuntimeState
{
    private SettingsSnapshot? _settings;

    public SettingsSnapshot Settings => Volatile.Read(ref _settings) ?? new SettingsSnapshot();

    public void Configure(SettingsSnapshot settings)
    {
        Volatile.Write(ref _settings, settings);
    }
}
