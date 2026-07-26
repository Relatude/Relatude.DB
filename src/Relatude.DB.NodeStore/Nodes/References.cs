using Relatude.DB.Datamodels;
using System.Collections;

namespace Relatude.DB.Nodes;

public interface IReferences {
    Guid[] Ids { get; set; }
    bool IsSet();
    int Count();
    bool Contains(Guid id);
    void Initialize(NodeStore store, Guid[] guids, INodeDataExternal[]? nodeDatas);
}
public interface IReferences<T> : IReferences {
    IEnumerable<T> Get();
}
/// <summary>
/// An ordered, duplicate-preserving series of references to other nodes, stored as a Guid[] on the
/// node. The multi-value twin of <see cref="Reference{T}"/>: enumeration yields preloaded nodes
/// only (populate with .Preload() in a query); <see cref="Get"/> lazily loads every live target.
/// </summary>
public class References<T> : IEnumerable<T>, IReferences<T> where T : notnull {
    Guid[] _ids = Array.Empty<Guid>();
    INodeDataExternal[]? nodeDatas; // preloaded nodeData, in stored order (stale targets omitted)
    NodeStore? _store = null;
    NodeStore store => _store ?? throw new Exception("References is not initialized. ");
    public Guid[] Ids {
        get => _ids;
        set => Set(value);
    }
    public bool Set(Guid[] ids) {
        ids ??= Array.Empty<Guid>();
        if (_ids.SequenceEqual(ids)) return false;
        _ids = ids;
        nodeDatas = null;
        return true;
    }
    public bool Add(Guid id) {
        var ids = new Guid[_ids.Length + 1];
        _ids.CopyTo(ids, 0);
        ids[^1] = id;
        _ids = ids;
        nodeDatas = null;
        return true;
    }
    public bool Add(T node, NodeStore? db = null) {
        db = db == null ? _store : db;
        if (db == null) throw new Exception("NodeStore is not initialized. ");
        return Add(db.Mapper.GetIdGuid(node));
    }
    /// <summary>Removes every occurrence of the id. False when the id is not present.</summary>
    public bool Remove(Guid id) {
        if (!_ids.Contains(id)) return false;
        _ids = _ids.Where(g => g != id).ToArray();
        nodeDatas = null;
        return true;
    }
    public bool Clear() {
        if (_ids.Length == 0) return false;
        _ids = Array.Empty<Guid>();
        nodeDatas = null;
        return true;
    }
    public void Initialize(NodeStore store, Guid[] guids, INodeDataExternal[]? nodeDatas) {
        _store = store;
        // defensive copy: the array comes straight from the store's shared node data cache and must
        // never be aliased by this mutable wrapper (Ids[i] = ... would otherwise write into the cache)
        _ids = guids.Length == 0 ? Array.Empty<Guid>() : (Guid[])guids.Clone();
        this.nodeDatas = nodeDatas;
    }
    public bool IsSet() => _ids.Length > 0;
    public int Count() => _ids.Length;
    public bool Contains(Guid id) => _ids.Contains(id);
    /// <summary>Lazily loads every target in stored order. Deleted or type-mismatched targets are
    /// skipped rather than throwing: with many references per value, stale entries are routine.</summary>
    public IEnumerable<T> Get() {
        foreach (var id in _ids) {
            if (id == Guid.Empty) continue;
            if (!store.Datastore.TryGet(id, out var nodeData)) continue; // target deleted: skip
            if (store.Get(nodeData) is T node) yield return node;
        }
    }
    public IEnumerator<T> GetEnumerator() {
        // if not preloaded, just return empty enumerator (same contract as Reference<T>)
        if (nodeDatas != null) {
            foreach (var nodeData in nodeDatas) {
                if (store.Get(nodeData) is T node) yield return node; // just calling mapper, not loading
            }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
