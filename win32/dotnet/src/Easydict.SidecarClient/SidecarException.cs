using Easydict.SidecarClient.Protocol;

namespace Easydict.SidecarClient;

/// <summary>
/// Exception thrown when a sidecar operation fails.
/// </summary>
public class SidecarException : Exception
{
    public SidecarException(string message) : base(message) { }
    public SidecarException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when the sidecar process is not running.
/// </summary>
public class SidecarNotRunningException : SidecarException
{
    public SidecarNotRunningException() : base("Sidecar process is not running.") { }
}

/// <summary>
/// Exception thrown when a request times out.
/// </summary>
public class SidecarTimeoutException : SidecarException
{
    public string RequestId { get; }

    public SidecarTimeoutException(string requestId)
        : base($"Request '{requestId}' timed out.")
    {
        RequestId = requestId;
    }
}

/// <summary>
/// Exception thrown when the sidecar returns an error response.
/// </summary>
public class SidecarErrorException : SidecarException
{
    public IpcError Error { get; }

    public SidecarErrorException(IpcError error)
        : base($"Sidecar error [{error.Code}]: {error.Message}")
    {
        Error = error;
    }
}

/// <summary>
/// Exception thrown when the sidecar process exits unexpectedly.
/// </summary>
public class SidecarProcessExitedException : SidecarException
{
    public int? ExitCode { get; }

    public SidecarProcessExitedException(int? exitCode)
        : base($"Sidecar process exited unexpectedly with code {exitCode?.ToString() ?? "unknown"}.")
    {
        ExitCode = exitCode;
    }
}

