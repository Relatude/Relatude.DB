using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal delegate long BatchCallback(List<ExecutedPrimitiveTransaction> batch, Action<string, int>? progress, int actionCount, int transactionCount);
internal class LogQueue : IDisposable {
    readonly BatchCallback _workCallback;
    List<ExecutedPrimitiveTransaction> _queue;
    object _lock = new();
    public LogQueue(BatchCallback workCallback) {
        _workCallback = workCallback;
        _queue = [];
    }
    public void Add(ExecutedPrimitiveTransaction work) {
        lock (_lock) {
            _queue.Add(work);
        }
    }
    public void CompleteAddedWork(Action<string, int>? progress, out int transactionCount, out int actionCount, out long bytesWritten) {
        List<ExecutedPrimitiveTransaction> batch;
        lock (_lock) {
            batch = _queue;
            _queue = [];
        }
        actionCount = batch.Sum(x => x.ExecutedActions.Count);
        transactionCount = batch.Count;
        bytesWritten = 0;
        if (transactionCount > 0) bytesWritten = _workCallback(batch, progress, actionCount, transactionCount);
    }
    public void Dispose() {
        CompleteAddedWork(null, out _, out _, out _);
    }
    public int Count {
        get {
            lock (_lock) return _queue.Count;
        }
    }
}
