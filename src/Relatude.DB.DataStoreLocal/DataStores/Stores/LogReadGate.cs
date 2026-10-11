namespace Relatude.DB.DataStores.Stores;

/// <summary>
/// Keeps the log file where it is while node bytes are read from it outside the store lock.
/// <para>A query takes a read lease while it still holds the store's read lock, notes the log positions
/// of the nodes it needs, lets the store lock go and only then reads the bytes. Positions in the log
/// never change - the file only grows - except when the file itself is closed, swapped for a rewritten
/// one, copied or truncated. Those take the gate exclusively, which waits for the leases out, so a
/// position handed out under a lease always points into the file it was read from.</para>
/// <para>Deadlock rules, all kept by the callers:
/// (1) the exclusive side is only taken by a thread holding the store's write lock, or by Dispose, which
/// holds nothing a reader could wait for;
/// (2) a lease holder never asks for the store lock (it may still hold the read lock it took the lease
/// under, a nested read, which is fine as no writer can then hold the write lock);
/// (3) nothing that waits for the gate holds the node store or cache locks a reader takes.
/// Unlike the store lock, a waiting exclusive taker does not hold up new leases - only a held exclusive
/// side does - so a nested lease can never deadlock against a waiting writer. The one self-deadlock left,
/// a thread asking for the exclusive side while it holds a lease, throws instead of hanging.</para>
/// </summary>
internal sealed class LogReadGate {
    readonly object _sync = new();
    int _leases;
    readonly Dictionary<int, int> _leasesByThread = []; // managed thread id -> leases it holds
    Thread? _owner; // the thread holding the exclusive side
    int _ownerDepth;
    bool _closed;

    /// <summary>A read lease: the file stays in place until it is disposed. A second Dispose does nothing.</summary>
    public sealed class Lease : IDisposable {
        LogReadGate? _gate;
        readonly int _threadId;
        internal Lease(LogReadGate gate, int threadId) {
            _gate = gate;
            _threadId = threadId;
        }
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.exitRead(_threadId);
    }
    public Lease EnterRead() {
        var me = Thread.CurrentThread;
        var threadId = me.ManagedThreadId;
        lock (_sync) {
            while (_owner != null && _owner != me) Monitor.Wait(_sync);
            if (_closed) throw new ObjectDisposedException("log file", "The log file is closed. ");
            _leases++;
            _leasesByThread[threadId] = _leasesByThread.GetValueOrDefault(threadId) + 1;
        }
        return new Lease(this, threadId);
    }
    void exitRead(int threadId) {
        lock (_sync) {
            var held = _leasesByThread.GetValueOrDefault(threadId);
            if (held <= 1) _leasesByThread.Remove(threadId);
            else _leasesByThread[threadId] = held - 1;
            _leases--;
            if (_leases == 0) Monitor.PulseAll(_sync);
        }
    }
    /// <summary>Waits until no lease is out and takes the file for itself. Re-entrant on the same thread.</summary>
    public void EnterExclusive() {
        var me = Thread.CurrentThread;
        lock (_sync) {
            if (_owner == me) {
                _ownerDepth++;
                return;
            }
            // a lease held by this very thread would never be given back while it waits here
            if (_leasesByThread.ContainsKey(me.ManagedThreadId)) throw new InvalidOperationException("Internal error: the log file cannot be closed or swapped by a thread that is reading from it. ");
            while (_owner != null || _leases > 0) Monitor.Wait(_sync);
            _owner = me;
            _ownerDepth = 1;
        }
    }
    public void ExitExclusive() {
        lock (_sync) {
            if (_owner != Thread.CurrentThread) throw new InvalidOperationException("Internal error: the log file gate is not held by this thread. ");
            if (--_ownerDepth > 0) return;
            _owner = null;
            Monitor.PulseAll(_sync);
        }
    }
    /// <summary>
    /// For disposal: no lease is handed out after this, and the leases out are waited for, at most
    /// <paramref name="timeoutMs"/>. A reader still out after that finds the file closed under it and fails,
    /// as any read during a dispose always could. Returns whether the readers were all gone.
    /// </summary>
    public bool Close(int timeoutMs) {
        lock (_sync) {
            _closed = true;
            Monitor.PulseAll(_sync); // anyone waiting to enter fails now instead of reading a closed file
            if (_leasesByThread.ContainsKey(Environment.CurrentManagedThreadId)) return false; // the disposing thread reads itself: waiting would never end
            var deadline = Environment.TickCount64 + timeoutMs;
            while (_leases > 0) {
                var left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_sync, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }
    internal int LeasesOut { get { lock (_sync) return _leases; } }
}
