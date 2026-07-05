using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Nodes;

public interface IReference {
    Guid Id { get; set; }
    bool IsSet();
    int Count();
    bool Contains(Guid id);
    void Initialize(NodeStore store, Guid guid, INodeDataExternal? nodeData);
    //bool IsPreloaded(); // currently not implemented or supported...
}
public class Reference<T> : IEnumerable<T>, IReference where T : notnull {
    Guid _id;
    public Guid Id {
        get => _id;
        set => Set(value);
    }
    public bool Set(T value) {
        if (!store.Mapper.TryGetIdGuid(value, out Guid id)) {
            throw new InvalidOperationException("Unable to identify the node, does it have a valid Id and is already persisted?");
        }
        return Set(id);
    }
    public bool Set(Guid id) {
        if (id == _id) return false;
        _id = id;
        //nodeData = null; // reset nodeData when Id changes
        return true;
    }
    NodeStore? _store = null;
    NodeStore store => _store ?? throw new Exception("Reference is not initialized. ");
    public void Initialize(NodeStore store, Guid guid, INodeDataExternal? nodeData) {
        this._store = store;
        this.Id = guid;
        //this.nodeData = nodeData; // may be set or lacyloaded later
    }
    public bool IsSet() => Id != Guid.Empty;
    public int Count() => IsSet() ? 1 : 0;
    public T Get() {
        if (TryGet(out T? value)) return value;
        throw new Exception("Reference is not set or the referenced node is not loaded. ");
    }
    public bool TryGet([MaybeNullWhen(false)] out T value) {
        if (IsSet()) {
            if (store.TryGet<T>(Id, out value)) {
                return true;
            }
        }
        value = default;
        return false;
    }
    public bool Contains(Guid id) => id == this.Id;
    public IEnumerator<T> GetEnumerator() {
        if (TryGet(out T? value)) yield return value;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}