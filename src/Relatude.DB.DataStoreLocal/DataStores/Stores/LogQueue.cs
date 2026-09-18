using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal delegate long BatchCallback(ExecutedPrimitiveTransaction[] batch, Action<string, int>? progress, int actionCount, int transactionCount);
internal class LogQueue : IDisposable {
    readonly BatchCallback _workCallback;
    List<ExecutedPrimitiveTransaction> _queue;
    readonly System.Threading.Lock _workLock = new();
    readonly System.Threading.Lock _queueLock = new();
    int _estimatedTransactionCount;
    int _estimatedActionCount; // kept with Interlocked so the per transaction check in the store never has to sum the queue
    public LogQueue(BatchCallback workCallback) {
        _workCallback = workCallback;
        _queue = [];
    }
    public void Add(ExecutedPrimitiveTransaction work) {
        lock (_queueLock) {
            _queue.Add(work);
        }
        Interlocked.Increment(ref _estimatedTransactionCount);
        Interlocked.Add(ref _estimatedActionCount, work.ExecutedActions.Count);
    }
    public void DequeAllWorkThreadSafe(Action<string, int>? progress, out int transactionCount, out int actionCount, out long bytesWritten) {
        lock (_workLock) {
            // _workLock is needed to prevent multiple batches running simultaneously
            // ( would cause problem with disk flushes as they have no lock,
            // and could interleave with db rewrite, that uses flush to ensure all node segments are written)
            // the queue snapshot is taken INSIDE _workLock so that the order batches are written
            // always matches the order transactions were queued (snapshot order == write order)
            ExecutedPrimitiveTransaction[] batch;
            lock (_queueLock) {
                actionCount = 0;
                foreach (var t in _queue) actionCount += t.ExecutedActions.Count;
                batch = _queue.ToArray();
                transactionCount = _queue.Count;
                _queue = [];
            }
            bytesWritten = 0;
            if (transactionCount > 0) bytesWritten = _workCallback(batch, progress, actionCount, transactionCount);
        }
        Interlocked.Add(ref _estimatedTransactionCount, -transactionCount);
        Interlocked.Add(ref _estimatedActionCount, -actionCount);
    }
    public void Dispose() {
        DequeAllWorkThreadSafe(null, out _, out _, out _);
    }

    /// <summary>Actions queued and not yet written. An estimate in the same sense as <see cref="EstimateTransactionCount"/>: no lock.</summary>
    internal int GetQueueActionCount() => Volatile.Read(ref _estimatedActionCount);

    public int EstimateTransactionCount { // no lock
        get {
            return Volatile.Read(ref _estimatedTransactionCount);
        }
    }
}
