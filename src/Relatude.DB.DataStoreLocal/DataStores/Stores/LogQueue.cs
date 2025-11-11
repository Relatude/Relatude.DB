using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal delegate long BatchCallback(ExecutedPrimitiveTransaction[] batch, Action<string, int>? progress, int actionCount, int transactionCount);
internal class LogQueue : IDisposable {
    readonly BatchCallback _workCallback;
    List<ExecutedPrimitiveTransaction> _queue;
    object _lock = new();
    int _estimatedCount;
    public LogQueue(BatchCallback workCallback) {
        _workCallback = workCallback;
        _queue = [];
    }
    public void Add(ExecutedPrimitiveTransaction work) {
        lock (_lock) {
            _queue.Add(work);            
        }
        System.Threading.Interlocked.Increment(ref _estimatedCount);
    }
    public void DequeAllWork(Action<string, int>? progress, out int transactionCount, out int actionCount, out long bytesWritten) {
        lock (_lock) { // lock is needed to prevent multiple batches running simultaneously, ( would cause problem with disk flushes as they have no lock, and could interleave with db rewrite, that uses flush to ensure all node segments are written)
            actionCount = _queue.Sum(x => x.ExecutedActions.Count);
            transactionCount = _queue.Count;
            bytesWritten = 0;
            if (transactionCount > 0) bytesWritten = _workCallback(_queue.ToArray(), progress, actionCount, transactionCount);
            _queue.Clear();
        }
        System.Threading.Interlocked.Add(ref _estimatedCount, -transactionCount);
    }
    public void Dispose() {
        DequeAllWork(null, out _, out _, out _);
    }
    public int EstimateCount { // no lock
        get {
            return System.Threading.Interlocked.CompareExchange(ref _estimatedCount, 0, 0);
        }
    }
}
