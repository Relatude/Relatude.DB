using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal delegate long BatchCallback(ExecutedPrimitiveTransaction[] batch, Action<string, int>? progress, int actionCount, int transactionCount);
internal class LogQueue : IDisposable {
    readonly BatchCallback _workCallback;
    List<ExecutedPrimitiveTransaction> _queue;
    object _workLock = new();
    object _queueLock = new();
    int _estimatedTransactionCount;
    public LogQueue(BatchCallback workCallback) {
        _workCallback = workCallback;
        _queue = [];
    }
    public void Add(ExecutedPrimitiveTransaction work) {
        lock (_queueLock) {
            _queue.Add(work);
        }
        System.Threading.Interlocked.Increment(ref _estimatedTransactionCount);
    }
    public void DequeAllWorkThreadSafe(Action<string, int>? progress, out int transactionCount, out int actionCount, out long bytesWritten) {
        ExecutedPrimitiveTransaction[] batch;
        lock (_queueLock) {
            actionCount = _queue.Sum(x => x.ExecutedActions.Count);
            batch = _queue.ToArray();
            transactionCount = _queue.Count;
            _queue = [];
        }
        lock (_workLock) { 
            // _workLock is needed to prevent multiple batches running simultaneously
            // ( would cause problem with disk flushes as they have no lock,
            // and could interleave with db rewrite, that uses flush to ensure all node segments are written)
            bytesWritten = 0;
            if (transactionCount > 0) bytesWritten = _workCallback(batch, progress, actionCount, transactionCount);
        }
        System.Threading.Interlocked.Add(ref _estimatedTransactionCount, -transactionCount);
    }
    public void Dispose() {
        DequeAllWorkThreadSafe(null, out _, out _, out _);
    }

    internal int GetQueueActionCount() {
        lock(_queueLock) {
            return _queue.Sum(x => x.ExecutedActions.Count);
        }
    }

    public int EstimateTransactionCount { // no lock
        get {
            return System.Threading.Interlocked.CompareExchange(ref _estimatedTransactionCount, 0, 0);
        }
    }
}
