namespace Easydict.SidecarClient;

/// <summary>
/// Configuration options for SidecarClient.
/// </summary>
public sealed class SidecarClientOptions
{
    /// <summary>
    /// Path to the sidecar executable.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// Arguments to pass to the sidecar executable.
    /// </summary>
    public string[]? Arguments { get; init; }

    /// <summary>
    /// Working directory for the sidecar process.
    /// If null, uses the current directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Default timeout for requests (in milliseconds).
    /// Default: 30000 (30 seconds).
    /// </summary>
    public int DefaultTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Environment variables to set for the sidecar process.
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
}

