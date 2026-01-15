using System.Diagnostics;

namespace Easydict.SidecarClient;

/// <summary>
/// Configuration for <see cref="SidecarClient"/>.
/// </summary>
public sealed class SidecarClientOptions
{
    /// <summary>
    /// Child process executable (e.g. python3).
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Child process arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional working directory for the child process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Optional environment variables for the child process.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// If true, the client will attempt to kill the child process during dispose.
    /// </summary>
    public bool KillProcessOnDispose { get; init; } = true;

    internal ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = FileName,
            WorkingDirectory = WorkingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var arg in Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var kv in Environment)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        return psi;
    }
}
