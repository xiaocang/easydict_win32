using Easydict.SidecarClient.Protocol;

namespace Easydict.Workers.LongDoc.Infrastructure;

/// <summary>
/// Process-local state shared across handlers. Populated by ConfigureHandler
/// from the SettingsSnapshot received over stdin; read by all other handlers.
/// </summary>
internal sealed class WorkerState
{
    private SettingsSnapshot? _settings;
    private readonly object _lock = new();

    public SettingsSnapshot? Settings
    {
        get
        {
            lock (_lock) return _settings;
        }
    }

    public void ApplySettings(SettingsSnapshot snapshot)
    {
        lock (_lock)
        {
            _settings = snapshot;
        }
    }

    public bool IsConfigured
    {
        get
        {
            lock (_lock) return _settings is not null;
        }
    }

    /// <summary>
    /// Set by Program.Main right before exit so the OS reports the correct exit code.
    /// </summary>
    public int? LastExitCode { get; set; }
}
