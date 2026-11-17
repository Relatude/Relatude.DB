using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Nodes;

interface IEmbeddedOne {
}
public class EmbeddedOne<TValue> : IEnumerable<TValue>, IEmbeddedOne where TValue : notnull {
    readonly Embedded<TValue> _inner;
    public EmbeddedOne() {
        _inner = new();
    }
    public EmbeddedOne(TValue value) : this() {
        _inner.Add(value);
    }
    public EmbeddedOne(InnerNodeDataMap<Guid> nodeDataMap, NodeMapper mapper) {
        _inner = new(nodeDataMap, mapper);
    }
    public void Initialize(InnerNodeDataMap<Guid> nodeDataMap, NodeMapper mapper) => _inner.Initialize(nodeDataMap, mapper);
    public PropertyPath? PropertyPath => _inner.PropertyPath;
    public bool HasValue => _inner.Count > 0;
    public TValue? Value {
        get => TryGet(out var value) ? value : default;
        set {
            if (value is null) Clear();
            else Set(value);
        }
    }
    public TValue Get() {
        if (TryGet(out var value)) return value;
        throw new InvalidOperationException("The embedded node is not set. ");
    }
    public bool TryGet([MaybeNullWhen(false)] out TValue value) {
        foreach (var v in _inner) {
            value = v;
            return true;
        }
        value = default;
        return false;
    }
    public void Set(TValue value) {
        ArgumentNullException.ThrowIfNull(value);
        _inner.Clear();
        _inner.Add(value);
    }
    public void Clear() => _inner.Clear();
    public InnerNodeDataMap<Guid> GetNodeDataMap(PropertyPath propPath, Guid keyPropertyId, NodeMapper mapper) => _inner.GetNodeDataMap(propPath, keyPropertyId, mapper);
    public IEnumerator<TValue> GetEnumerator() {
        if (TryGet(out var value)) yield return value;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
