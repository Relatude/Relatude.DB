using Relatude.DB.DataStores.Transactions;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal delegate long BatchCallback(List<ExecutedPrimitiveTransaction> batch);
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
    public void CompleteAddedWork(out int transactionCount, out int actionCount, out long bytesWritten) {
        List<ExecutedPrimitiveTransaction> batch;
        lock (_lock) {
            batch = _queue;
            _queue = [];
        }
        actionCount = batch.Sum(x => x.ExecutedActions.Count);
        transactionCount = batch.Count;
        bytesWritten = 0;
        if (transactionCount > 0) bytesWritten = _workCallback(batch);
    }
    public void Dispose() {
        CompleteAddedWork(out _, out _, out _);
    }
    public int Count {
        get {
            lock (_lock) return _queue.Count;
        }
    }
}
