using System.Net;
using System.Net.Sockets;
using Relatude.DB.Common;

namespace Relatude.Common;

/// <summary>
/// <see cref="TcpProbe"/> exists for one reason: to find out that nothing is listening without
/// raising an exception, because a refused connection inside HttpClient raises about a dozen and
/// they stop the debugger whether or not the caller catches them. So these tests check the answers
/// and, for the case the type exists for, that getting the answer cost nothing.
/// </summary>
[TestClass]
public class TcpProbeTests {
    /// <summary>A port on loopback with nothing behind it. Bound, read, and closed again, so the
    /// number is one the machine had free rather than one hoped to be.</summary>
    static int ClosedPort() {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    static Socket Listening(out int port) {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return socket;
    }

    /// <summary>Counts the exceptions raised anywhere in the process while an operation runs, which
    /// is what the debugger breaks on - a caught one counts, and that is the point.</summary>
    static async Task<(T Result, List<string> Raised)> Watching<T>(Func<Task<T>> operation) {
        List<string> raised = [];
        void onRaised(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) {
            lock (raised) raised.Add(e.Exception.GetType().Name);
        }
        AppDomain.CurrentDomain.FirstChanceException += onRaised;
        try {
            var result = await operation();
            lock (raised) return (result, [.. raised]);
        } finally {
            AppDomain.CurrentDomain.FirstChanceException -= onRaised;
        }
    }

    /// <summary>
    /// Long enough that the refusal, not the clock, decides the answer. A refusal is instant on some
    /// machines and takes a couple of seconds on others, and a test that raced it would be measuring
    /// <see cref="TcpProbe.DefaultTimeout"/> rather than the behaviour.
    /// </summary>
    static readonly TimeSpan _patient = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task ARefusedPortAnswersNoAndRaisesNothing() {
        var port = ClosedPort();
        var (listening, raised) = await Watching(() => TcpProbe.IsListeningAsync("127.0.0.1", port, _patient));

        Assert.IsFalse(listening, "nothing is listening on a port that was just given up.");
        CollectionAssert.AreEqual(Array.Empty<string>(), raised,
            "the whole point of the type is that finding this out costs no exception; raised: " + string.Join(", ", raised));
    }

    [TestMethod]
    public async Task AListeningPortAnswersYesAndRaisesNothing() {
        using var listener = Listening(out var port);
        var (listening, raised) = await Watching(() => TcpProbe.IsListeningAsync("127.0.0.1", port, _patient));

        Assert.IsTrue(listening);
        CollectionAssert.AreEqual(Array.Empty<string>(), raised, "raised: " + string.Join(", ", raised));
    }

    [TestMethod]
    public async Task AUrlIsProbedOnTheHostAndPortItNames() {
        using var listener = Listening(out var port);
        Assert.IsTrue(await TcpProbe.IsListeningAsync($"http://127.0.0.1:{port}/api/license/heartbeat", _patient));
        Assert.IsFalse(await TcpProbe.IsListeningAsync($"https://127.0.0.1:{ClosedPort()}/", _patient));
    }

    /// <summary>
    /// Everything the probe cannot turn into a definite no has to answer yes, so that the request it
    /// stands in front of is still made and its own error handling decides. A probe that guessed no
    /// would quietly stop the caller from ever trying.
    /// </summary>
    [TestMethod]
    public async Task AnythingItCannotRuleOutAnswersYes() {
        Assert.IsTrue(await TcpProbe.IsListeningAsync((string?)null), "no url is not a verdict about a server.");
        Assert.IsTrue(await TcpProbe.IsListeningAsync("not a url"), "an unparseable url belongs to whatever uses it.");
        Assert.IsTrue(await TcpProbe.IsListeningAsync("http://127.0.0.1:0/"), "port zero is not an address to refuse.");
        Assert.IsTrue(await TcpProbe.IsListeningAsync("", 443), "an empty host is not a verdict either.");

        // a cancelled probe is not a no, and does not throw the way an awaited task would
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.IsTrue(await TcpProbe.IsListeningAsync("127.0.0.1", ClosedPort(), _patient, cancelled.Token));
    }

    /// <summary>
    /// A timeout is not a no either. Probed against an address that is routable but silent, with a
    /// budget short enough to be sure the answer came from the clock.
    /// </summary>
    [TestMethod]
    public async Task ATimeoutAnswersYes() {
        // 198.51.100.0/24 is reserved for documentation, so nothing answers and nothing refuses
        var listening = await TcpProbe.IsListeningAsync("198.51.100.1", 443, TimeSpan.FromMilliseconds(150));
        Assert.IsTrue(listening, "a silent address says nothing about the service, so the request should still be tried.");
    }
}
