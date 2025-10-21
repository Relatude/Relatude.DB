using Relatude.DB.DataStores.Indexes;
namespace Relatude.DB.DataStores.Indexes;
/// <summary>
/// A utility class to optimize add/remove operations on indexes.
/// it will queue a remove operation and if the same add operation is called, it will not do the remove.
/// </summary>
/// <param name="index"></param>
public class AddRemoveOptimization(IIndex index) {
    int _lastRemovedNodeId = 0;
    object _lastRemovedValue = default!;
    readonly object _lock = new();
    readonly IIndex _index = index;
    public void Add(int id, object value) {
        if (_lastRemovedNodeId == id && value.Equals(_lastRemovedValue)) {
            _lastRemovedNodeId = 0;// avoiding operation, qued removed is same as add, so just clear it!
        } else {
            dequeue(); // locks not needed as during a add remove, only one thread is allowed
            _index.Add(id, value);
        }
    }
    public void Remove(int id, object value) {
        dequeue();
        _lastRemovedNodeId = id;
        _lastRemovedValue = value;
    }
    public void RegisterAddDuringStateLoad(int id, object value, long timestampId) {
        dequeue();
        _index.RegisterAddDuringStateLoad(id, value, timestampId);
    }
    public void RegisterRemoveDuringStateLoad(int id, object value, long timestampId) {
        dequeue();
        _index.RegisterRemoveDuringStateLoad(id, value, timestampId);
    }
    void dequeue() {
        if (_lastRemovedNodeId == 0) return;
        _index.Remove(_lastRemovedNodeId, _lastRemovedValue);
        _lastRemovedNodeId = 0;
    }
    public void Dequeue() {
        lock (_lock) { // lock needed as during a query multiple threads may call this at once
            if (_lastRemovedNodeId == 0) return;
            _index.Remove(_lastRemovedNodeId, _lastRemovedValue);
            _lastRemovedNodeId = 0;
        }
    }
}