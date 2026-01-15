namespace Easydict.SidecarClient;

/// <summary>
/// A typed exception thrown when the remote sidecar responds with an error.
/// </summary>
public sealed class SidecarRemoteException : Exception
{
    public SidecarRemoteException(string code, string message)
        : base($"Remote error ({code}): {message}")
    {
        Code = code;
        RemoteMessage = message;
    }

    public string Code { get; }
    public string RemoteMessage { get; }
}

/// <summary>
/// A typed exception thrown when a call exceeds its per-call timeout.
/// </summary>
public sealed class SidecarTimeoutException : TimeoutException
{
    public SidecarTimeoutException(string method, string requestId, TimeSpan timeout)
        : base($"Sidecar request timed out after {timeout.TotalMilliseconds}ms: {method} (id={requestId})")
    {
        Method = method;
        RequestId = requestId;
        Timeout = timeout;
    }

    public string Method { get; }
    public string RequestId { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>
/// A typed exception thrown when the child process exited (or stdout closed) while requests are in-flight.
/// </summary>
public sealed class SidecarProcessExitedException : Exception
{
    public SidecarProcessExitedException(int? exitCode, string message, Exception? inner = null)
        : base(exitCode is null ? message : $"{message} (exitCode={exitCode})", inner)
    {
        ExitCode = exitCode;
    }

    public int? ExitCode { get; }
}
