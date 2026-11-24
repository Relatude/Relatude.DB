namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal {
    readonly BasicCounter _basicCounter = new();
    public void StartBasicCountMetric() => _basicCounter.Start();
    public BasicCountMetric EndBasicCountMetric() => _basicCounter.End();
}
/// <summary>
/// The counts of <see cref="IDataStore.StartBasicCountMetric"/>: one set for the store, so counts that overlap share it,
/// and both answer zeros. Counting costs a read of <see cref="_running"/> while none runs.
/// </summary>
internal sealed class BasicCounter {
    readonly Lock _lock = new();
    volatile int _running;
    bool _overlapped;
    long _queries, _nodes, _notCached;
    public void Query() {
        if (_running > 0) Interlocked.Increment(ref _queries);
    }
    public void Nodes(int retrieved, int notCached) {
        if (_running == 0) return;
        Interlocked.Add(ref _nodes, retrieved);
        if (notCached > 0) Interlocked.Add(ref _notCached, notCached);
    }
    public void Start() {
        lock (_lock) {
            if (_running == 0) {
                Interlocked.Exchange(ref _queries, 0);
                Interlocked.Exchange(ref _nodes, 0);
                Interlocked.Exchange(ref _notCached, 0);
                _overlapped = false;
            } else {
                _overlapped = true;
            }
            _running++;
        }
    }
    public BasicCountMetric End() {
        lock (_lock) {
            if (_running == 0) return BasicCountMetric.None;
            _running--;
            if (_overlapped) return new(0, 0, 0, true);
            return new(Interlocked.Read(ref _queries), Interlocked.Read(ref _nodes), Interlocked.Read(ref _notCached), false);
        }
    }
}
