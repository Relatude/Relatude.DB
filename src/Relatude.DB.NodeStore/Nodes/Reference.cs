using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Relatude.DB.Nodes;

public interface IReference {
    Guid Id { get; set; }
    bool IsSet();
    int Count();
    bool Contains(Guid id);
    void Initialize(NodeStore store, Guid guid, INodeDataExternal? nodeData);
}
public interface IReference<T> : IReference {
    T Get();
    bool TryGet([MaybeNullWhen(false)] out T value);
}
public class Reference<T> : IEnumerable<T>, IReference<T> where T : notnull {
    Guid _id;
    INodeDataExternal? nodeData; // preloaded nodeData
    public Guid Id {
        get => _id;
        set => Set(value);
    }
    public bool Set(Guid id) {
        if (id == _id) return false;
        _id = id;
        nodeData = null;
        return true;
    }
    public bool Clear() {
        if (_id == Guid.Empty) return false;
        _id = Guid.Empty;
        nodeData = null;
        return true;
    }
    public bool Set(T node, NodeStore? db = null) {
        db = db == null ? _store : db;
        if (db == null) throw new Exception("NodeStore is not initialized. ");
        var id = db.Mapper.GetIdGuid(node);
        return Set(id);
    }
    NodeStore? _store = null;
    NodeStore store => _store ?? throw new Exception("Reference is not initialized. ");
    public void Initialize(NodeStore store, Guid guid, INodeDataExternal? nodeData) {
        this._store = store;
        this.Id = guid;
        this.nodeData = nodeData;
    }
    public bool IsSet() => Id != Guid.Empty;
    public int Count() => IsSet() ? 1 : 0;
    public T Get() {
        if (TryGet(out T? value)) return value;
        throw new Exception("No reference value is set. ");
    }
    public bool TryGet([MaybeNullWhen(false)] out T value) {
        if (IsSet()) {
            // if not preloaded, try to get from store
            if (nodeData == null) nodeData = store.Datastore.Get(Id);
            var nodeO = store.Get(nodeData);
            if (nodeO is T node) {
                value = node;
                return true;
            } else {
                value = default;
                return false;
            }
            //value = store.Get<T>(nodeData);
        }
        value = default;
        return false;
    }
    public bool Contains(Guid id) => id == this.Id;
    public IEnumerator<T> GetEnumerator() {
        // if not preloaded, just return empty enumerator
        if (nodeData != null) {
            var nodeO = store.Get(nodeData); // just calling mapper, not loading
            if (nodeO is T node) yield return node;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}