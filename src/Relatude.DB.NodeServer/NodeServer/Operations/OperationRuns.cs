namespace Relatude.DB.NodeServer.Operations;

/// <summary>
/// Calls a client can stop while they run. One sent with a run id of the client's making gets a cancellation token
/// (<see cref="OperationContext.Cancellation"/>), cancelled when the client sends <c>command-cancel</c> with that id and
/// when the request is aborted. The explicit cancel is what makes stopping reliable: behind a proxy the server does not
/// always learn that a client gave up.
/// <para>A cancel can overtake its own call (two requests, two connections), so an id cancelled before its call arrived
/// is remembered for a while and the call starts out cancelled.</para>
/// Run ids are scoped by an owner, the user, so one cannot stop another's calls. Thread-safe.
/// </summary>
public sealed class OperationRuns {
    readonly object _sync = new();
    readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _cancelledEarly = new(StringComparer.Ordinal); // key -> when, in Environment.TickCount64
    const long _rememberEarlyCancelMs = 60_000;
    const int _maxEarlyCancels = 1000;
    const int _maxRunIdLength = 100;

    static string key(string owner, string runId) => owner + ":" + runId;

    /// <summary>Registers a run; dispose it when the call is done.</summary>
    public Run Begin(string owner, string runId, CancellationToken requestAborted) {
        if (runId.Length > _maxRunIdLength) throw new ArgumentException("Run id too long. ");
        var k = key(owner, runId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        lock (_sync) {
            if (_cancelledEarly.Remove(k)) cts.Cancel();
            _running[k] = cts; // ids are the client's random ones; a reused id would only take the slot over
        }
        return new Run(this, k, cts);
    }

    /// <summary>Cancels the run, or - when it has not started yet - makes it start out cancelled. True when it was running.</summary>
    public bool Cancel(string owner, string runId) {
        if (string.IsNullOrEmpty(runId) || runId.Length > _maxRunIdLength) return false;
        var k = key(owner, runId);
        CancellationTokenSource? cts;
        lock (_sync) {
            if (!_running.TryGetValue(k, out cts)) {
                pruneEarlyCancels();
                if (_cancelledEarly.Count < _maxEarlyCancels) _cancelledEarly[k] = Environment.TickCount64;
                return false;
            }
        }
        try {
            cts.Cancel(); // outside the lock: cancelling runs what is registered on the token
        } catch (ObjectDisposedException) {
            // the run ended between the lookup and the cancel: nothing left to stop
        }
        return true;
    }

    void pruneEarlyCancels() {
        if (_cancelledEarly.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var kv in _cancelledEarly.ToArray()) {
            if (now - kv.Value > _rememberEarlyCancelMs) _cancelledEarly.Remove(kv.Key);
        }
    }

    void end(string k, CancellationTokenSource cts) {
        lock (_sync) {
            if (_running.TryGetValue(k, out var current) && current == cts) _running.Remove(k);
        }
        cts.Dispose();
    }

    public sealed class Run : IDisposable {
        readonly OperationRuns _runs;
        readonly string _key;
        readonly CancellationTokenSource _cts;
        int _disposed;
        internal Run(OperationRuns runs, string key, CancellationTokenSource cts) {
            _runs = runs;
            _key = key;
            _cts = cts;
            Token = cts.Token;
        }
        public CancellationToken Token { get; }
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _runs.end(_key, _cts);
        }
    }
}
