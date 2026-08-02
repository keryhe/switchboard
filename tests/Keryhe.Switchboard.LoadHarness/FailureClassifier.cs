using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Keryhe.Switchboard.LoadHarness;

/// <summary>
/// Plan decision D35: a connection failure during a large ramp must be classified by cause, not
/// lumped into one "failed" bucket — a harness that reports "connection failures at 9,400 clients"
/// without saying why will be read as a service limit and will usually be wrong. Only
/// <see cref="HandshakeTimeout"/> and <see cref="Other"/> are candidate service defects;
/// everything else is either the host's own ceiling or the service's documented, correct
/// backpressure (D5's negotiate 503).
/// </summary>
public enum FailureCategory
{
    EphemeralPortExhaustion,
    FileDescriptorExhaustion,
    NegotiateBackpressure503,
    HandshakeTimeout,
    Other,
}

public static class FailureClassifier
{
    public static FailureCategory Classify(Exception exception)
    {
        // Walk AggregateException/inner exceptions — HubConnection.StartAsync wraps failures from
        // several layers (HttpClient, WebSocket, the handshake itself).
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException)
            {
                return FailureCategory.HandshakeTimeout;
            }

            if (current is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable })
            {
                return FailureCategory.NegotiateBackpressure503;
            }

            if (current is SocketException socketEx)
            {
                if (socketEx.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressAlreadyInUse)
                {
                    return FailureCategory.EphemeralPortExhaustion;
                }

                // EMFILE ("too many open files") surfaces as SocketError.TooManyOpenSockets on
                // some platforms and as a bare native error (24 on BSD/macOS, 23/24 family on
                // Linux) wrapped as SocketError.SocketError on others — check both the typed
                // value and the message text, since .NET's SocketException message text passes
                // the OS's own errno string through verbatim.
                if (socketEx.SocketErrorCode == SocketError.TooManyOpenSockets ||
                    socketEx.Message.Contains("Too many open files", StringComparison.OrdinalIgnoreCase))
                {
                    return FailureCategory.FileDescriptorExhaustion;
                }
            }

            // A raw IOException / native error whose text names the same OS conditions — .NET's
            // WebSocket/HttpClient stack doesn't always surface a typed SocketException for these.
            var message = current.Message;
            if (message.Contains("Too many open files", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("EMFILE", StringComparison.Ordinal))
            {
                return FailureCategory.FileDescriptorExhaustion;
            }

            if (message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Address not available", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("EADDRNOTAVAIL", StringComparison.Ordinal) ||
                message.Contains("cannot assign requested address", StringComparison.OrdinalIgnoreCase))
            {
                return FailureCategory.EphemeralPortExhaustion;
            }
        }

        return FailureCategory.Other;
    }
}
