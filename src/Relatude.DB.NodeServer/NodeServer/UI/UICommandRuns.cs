namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// Commands the page can stop while they run. A command sent with a run id of the browser's making gets a
/// cancellation token (<see cref="UICommandContext.Cancellation"/>), cancelled when the page sends
/// <c>command-cancel</c> with that id - its Stop button, or a newer query taking over - and when the
/// request itself is aborted. The explicit cancel is what makes stopping reliable: behind a proxy the
/// server does not always learn that a browser gave up on a request.
/// <para>A cancel can overtake its own command (two requests, two connections), so an id cancelled
/// before its command arrived is remembered for a while and the command starts out cancelled.</para>
/// Thread-safe.
/// </summary>
internal sealed class UICommandRuns {
    readonly object _sync = new();
    readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _cancelledEarly = new(StringComparer.Ordinal); // run id -> when, in Environment.TickCount64
    const long _rememberEarlyCancelMs = 60_000;
    const int _maxEarlyCancels = 1000;
    const int _maxRunIdLength = 100;

    /// <summary>Registers a run; dispose it when the command is done.</summary>
    public Run Begin(string runId, CancellationToken requestAborted) {
        if (runId.Length > _maxRunIdLength) throw new Exception("Run id too long. ");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        lock (_sync) {
            if (_cancelledEarly.Remove(runId)) cts.Cancel();
            _running[runId] = cts; // ids are the browser's random ones; a reused id would only take the slot over
        }
        return new Run(this, runId, cts);
    }
    /// <summary>Cancels the run, or - when it has not started yet - makes it start out cancelled. True when it was running.</summary>
    public bool Cancel(string runId) {
        if (string.IsNullOrEmpty(runId) || runId.Length > _maxRunIdLength) return false;
        CancellationTokenSource? cts;
        lock (_sync) {
            if (!_running.TryGetValue(runId, out cts)) {
                pruneEarlyCancels();
                if (_cancelledEarly.Count < _maxEarlyCancels) _cancelledEarly[runId] = Environment.TickCount64;
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
    void end(string runId, CancellationTokenSource cts) {
        lock (_sync) {
            if (_running.TryGetValue(runId, out var current) && current == cts) _running.Remove(runId);
        }
        cts.Dispose();
    }
    internal int RunningCount { get { lock (_sync) return _running.Count; } }
    public sealed class Run : IDisposable {
        readonly UICommandRuns _runs;
        readonly string _runId;
        readonly CancellationTokenSource _cts;
        int _disposed;
        internal Run(UICommandRuns runs, string runId, CancellationTokenSource cts) {
            _runs = runs;
            _runId = runId;
            _cts = cts;
            Token = cts.Token;
        }
        public CancellationToken Token { get; }
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _runs.end(_runId, _cts);
        }
    }
}
