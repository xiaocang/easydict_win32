using System.Net;
using System.Net.Sockets;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Tells "the first hop never came up" apart from "the service answered badly".
/// </summary>
/// <remarks>
/// Only failures of the transport itself — a refused or unanswered TCP connect, a name that does
/// not resolve, a CONNECT tunnel the proxy rejected — count here. An HTTP status, a malformed
/// body or a mid-response reset do not: those mean something is listening, and retrying them can
/// still help.
/// </remarks>
internal static class ProxyFailureClassifier
{
    // Guards against a pathological (or cyclic) exception chain; real ones are a few links deep.
    private const int MaxDepth = 16;

    /// <summary>
    /// Whether <paramref name="exception"/> describes a connection that never got established.
    /// </summary>
    internal static bool IsFirstHopFailure(Exception? exception) =>
        IsFirstHopFailure(exception, MaxDepth);

    /// <summary>
    /// The <see cref="ProxyUnreachableException"/> somewhere in this exception's chain, if the
    /// failure was already attributed to the configured proxy.
    /// </summary>
    internal static ProxyUnreachableException? FindProxyFailure(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < MaxDepth; depth++)
        {
            if (exception is ProxyUnreachableException proxyFailure)
            {
                return proxyFailure;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var candidate in aggregate.InnerExceptions)
                {
                    var nested = FindProxyFailure(candidate);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private static bool IsFirstHopFailure(Exception? exception, int remainingDepth)
    {
        // A connect timeout surfaces as a bare TimeoutException under the HTTP exception; on its
        // own a TimeoutException says nothing about the transport, hence the flag.
        var insideHttpFailure = false;

        for (; exception is not null && remainingDepth > 0; remainingDepth--)
        {
            switch (exception)
            {
                case ProxyUnreachableException:
                    return true;

                // .NET 8 classifies transport failures on the exception itself, which is more
                // precise than matching on messages.
                case HttpRequestException { HttpRequestError: System.Net.Http.HttpRequestError.ConnectionError
                    or System.Net.Http.HttpRequestError.ProxyTunnelError
                    or System.Net.Http.HttpRequestError.NameResolutionError }:
                    return true;

                case SocketException socket when IsUnreachable(socket.SocketErrorCode):
                    return true;

                case TimeoutException when insideHttpFailure:
                    return true;

                case WebException web when IsUnreachable(web.Status):
                    return true;

                case AggregateException aggregate:
                    foreach (var candidate in aggregate.InnerExceptions)
                    {
                        if (IsFirstHopFailure(candidate, remainingDepth - 1))
                        {
                            return true;
                        }
                    }

                    return false;
            }

            insideHttpFailure |= exception is HttpRequestException;
            exception = exception.InnerException;
        }

        return false;
    }

    private static bool IsUnreachable(SocketError error) => error is
        SocketError.ConnectionRefused or
        SocketError.HostNotFound or
        SocketError.HostUnreachable or
        SocketError.NetworkUnreachable or
        SocketError.NetworkDown or
        SocketError.AddressNotAvailable or
        SocketError.TimedOut or
        SocketError.TryAgain or
        SocketError.NoData;

    private static bool IsUnreachable(WebExceptionStatus status) => status is
        WebExceptionStatus.ConnectFailure or
        WebExceptionStatus.NameResolutionFailure or
        WebExceptionStatus.ProxyNameResolutionFailure or
        WebExceptionStatus.Timeout;
}
