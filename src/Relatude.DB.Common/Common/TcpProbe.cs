using System.Net;
using System.Net.Sockets;

namespace Relatude.DB.Common;

/// <summary>
/// Whether anything is listening on a TCP port, answered without raising an exception.
///
/// <para>It exists to be called before an HTTP request to a service that may well be down - the
/// license server on a developer's machine, above all. A refused connection inside
/// <c>HttpClient</c> costs about a dozen first-chance exceptions, which stop the debugger and fill
/// the output window even though the caller catches them. Asking the socket first costs none:
/// <see cref="Socket.ConnectAsync(SocketAsyncEventArgs)"/> reports the refusal as a status on the
/// event args rather than raising it, unlike every other connect overload.</para>
///
/// <para><b>What a true answer is worth.</b> Only that a TCP connection was accepted. Not that the
/// service behind the port is the right one, that it is healthy, or that TLS will succeed. This
/// narrows the common "nothing is running there" case and nothing else, so the caller still needs
/// the error handling it always had around the real request.</para>
///
/// <para><b>Why false is rare.</b> False is returned only when the far end or the network said no
/// in so many words. A timeout, a cancellation or an unexpected failure answers true, because the
/// probe's job is to skip a request that is certain to fail and no other: anything less certain has
/// to go through the real request, whose own handling decides. That way a slow or filtered network
/// costs the exception it costs today rather than silently stopping the caller from ever trying.</para>
/// </summary>
public static class TcpProbe {
    /// <summary>
    /// How long to wait for the connection before giving up and answering true.
    ///
    /// <para>This has to be longer than a refusal takes to come back, or the probe times out first,
    /// answers true, and has bought nothing but a delay. A refusal is often instant, but not
    /// everywhere: measured on a Windows development machine it took about two seconds per address,
    /// and about four for a host like <c>localhost</c> that resolves to both ::1 and 127.0.0.1 and
    /// is therefore tried twice. Five seconds covers that and still fails faster than the HTTP
    /// request it stands in front of.</para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The refusals that mean nothing is there. Everything else - a timeout, an interrupted call, a
    /// local resource problem - says nothing about the far end and is not treated as a no.
    /// </summary>
    static bool IsDefiniteNo(SocketError error) => error
        is SocketError.ConnectionRefused
        or SocketError.HostNotFound
        or SocketError.HostUnreachable
        or SocketError.NetworkUnreachable
        or SocketError.NetworkDown
        or SocketError.AddressNotAvailable;

    /// <summary>
    /// Whether the host and port in a url are accepting connections. The port is the one in the url,
    /// or the scheme's default when it names none. A url that cannot be parsed answers true, so a
    /// bad setting is reported by the request that uses it rather than silently skipped here.
    /// </summary>
    public static Task<bool> IsListeningAsync(string? url, TimeSpan? timeout = null, CancellationToken cancellationToken = default) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.DnsSafeHost)) return Task.FromResult(true);
        return IsListeningAsync(uri.DnsSafeHost, uri.Port, timeout, cancellationToken);
    }

    /// <summary>
    /// Whether anything is listening on the host and port. See the type's own summary for what true
    /// and false are each worth; nothing here throws, including on a cancelled token.
    /// </summary>
    public static async Task<bool> IsListeningAsync(string host, int port, TimeSpan? timeout = null, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535) return true;
        try {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var args = new SocketAsyncEventArgs { RemoteEndPoint = Endpoint(host, port) };
            // RunContinuationsAsynchronously: the completion arrives on an IO thread, and resuming
            // the caller's work on it would hold that thread up.
            var completion = new TaskCompletionSource<SocketError>(TaskCreationOptions.RunContinuationsAsynchronously);
            args.Completed += (_, e) => completion.TrySetResult(e.SocketError);
            if (!socket.ConnectAsync(args)) completion.TrySetResult(args.SocketError); // answered synchronously

            // Task.WhenAny rather than WaitAsync: WaitAsync signals a timeout by throwing one, which
            // is the very thing this method exists to avoid.
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout ?? DefaultTimeout, cancellationToken)).ConfigureAwait(false);
            if (finished != completion.Task) return true; // timed out or cancelled: no verdict
            var error = completion.Task.Result;
            if (error == SocketError.Success) {
                // let go of the half-open connection at once rather than at the next collection
                try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
                return true;
            }
            return !IsDefiniteNo(error);
        } catch (Exception) {
            // The probe is an optimisation. Anything unexpected in it - and the socket layer can
            // still throw for a local reason, a disposed handle or an exhausted resource - must not
            // become a reason to skip the request it was meant to make cheaper.
            return true;
        }
    }

    /// <summary>
    /// An address literal is used as one rather than sent through DNS, which saves the resolver the
    /// work and, for a literal that is not an address, the one exception a failed lookup costs.
    /// </summary>
    static EndPoint Endpoint(string host, int port) =>
        IPAddress.TryParse(host, out var address) ? new IPEndPoint(address, port) : new DnsEndPoint(host, port);
}
