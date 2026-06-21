using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Nodes;

public interface IReference {
    bool IsSet();
    int Count();
    bool Contains(int id);
    bool Contains(Guid id);
    bool HasIncludedData();
}
public class Reference<T> : IEnumerable<T>, IReference where T : notnull {
    NodePath? _parentId;
    NodePath parentId => _parentId ?? throw new Exception("Reference is not initialized. ");
    Guid propertyId;
    Guid reference; 
    INodeDataExternal? nodeData;
    NodeStore? _store = null;
    NodeStore store => _store ?? throw new Exception("Reference is not initialized. ");
    public void Initialize(NodeStore store, NodePath parentId, Guid propertyId, Guid reference, INodeDataExternal? nodeData) {
        this._store = store;
        this._parentId = parentId;
        this.propertyId = propertyId;
        this.nodeData = nodeData;
        this.reference = reference;
    }
    public bool IsSet() => reference != Guid.Empty;
    public int Count() => IsSet() ? 1 : 0;
    public T Get() {
        if (TryGet(out T? value)) return value;
        throw new Exception($"Relation {store.Datastore.Datamodel.Properties[propertyId].CodeName} is not set and empty. ");
    }
    bool tryGet([MaybeNullWhen(false)] out INodeDataExternal value) {
        if (reference != Guid.Empty) {
            value = nodeData!; // _node is guaranteed to be not null if _isSet is true
            return true;
        }
        value = default;
            return false;
        }
        if (store.Datastore.TryGetRelatedNodeFromPropertyId(propertyId, parentId, out nodeData)) {
            isSet = true;
            value = nodeData;
            return true;
        }
        isSet = false;
        value = default;
        return false;
    }
    public bool TryGet([MaybeNullWhen(false)] out T value) {
        if (tryGet(out INodeDataExternal? nodeData)) {
            value = store.Get<T>(nodeData);
            return true;
        }
        value = default;
        return false;
    }
    public bool Contains(int id) => tryGet(out var nodeData) ? nodeData.__Id == id : false;
    public bool Contains(Guid id) => tryGet(out var nodeData) ? nodeData.Id == id : false;

    public bool HasIncludedData() => isSet.HasValue;
    public object? GetIncludedData() {
        if (!isSet.HasValue) throw new Exception("No included data. ");
        if (nodeData == null) return null;
        return store.Get<T>(nodeData);
    }

    public IEnumerator<T> GetEnumerator() {
        if (!HasIncludedData()) yield break;
        if (TryGet(out T? value)) yield return value;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}