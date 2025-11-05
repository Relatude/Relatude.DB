using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Transactions;

// threadsafe, for read operations, not for write
namespace Relatude.DB.DataStores.Stores;
internal class IndexStore : IDisposable {
    readonly Definition _definition;
    public IndexStore(Definition definition) {
        _definition = definition;
    }
    public bool WillUniqueConstraintsBeViolated(INodeData node, [MaybeNullWhen(false)] out Property property) {
        foreach (var kv in node.Values) {
            var p = _definition.Properties[kv.PropertyId];
            if (p.UniqueValues) {
                if (p is StringProperty sp) {
                    if (sp.IgnoreDuplicateEmptyValues && kv.Value is string s && string.IsNullOrEmpty(s)) {
                        continue;
                    }
                }
                if (p is IPropertyContainsValue pc) {
                    if (pc.ContainsValue(kv.Value)) {
                        property = p;
                        return true;
                    }
                } else {
                    throw new NotSupportedException("UniqueValues is true, but property does not implement IPropertyContainsValue. ");
                }
            }
        }
        property = null;
        return false;
    }
    public void Add(INodeData node) => _definition.IndexNode(node);
    public void Remove(INodeData node) => _definition.DeIndexNode(node);
        static Guid _indexStoreMarker = new Guid("414f9f60-6290-418f-bf18-3c6ee74cc78c");
    static Guid _indexMarker = new Guid("554f531c-16fc-495a-b017-31a7804eb765");
    public void SaveState(IAppendStream stream) {
        var indexes = _definition.GetAllIndexes();
        stream.WriteGuid(_indexStoreMarker);
        stream.WriteVerifiedInt(indexes.Count());
        foreach (var index in indexes) {
            stream.WriteGuid(_indexMarker);
            stream.WriteString(index.UniqueKey);
            index.SaveState(stream);
        }
        stream.WriteGuid(_indexStoreMarker);
    }
    public void ReadState(IReadStream stream, out bool anyIndexesMissing, Action<string?, int?> progress) {
        Dictionary<string, bool> indexesRedByKey = new();
        if (stream.ReadGuid() != _indexStoreMarker) throw new Exception("Error in index state stream. ");
        var noIndexes = stream.ReadVerifiedInt();
        for (var i = 0; i < noIndexes; i++) {
            if (stream.ReadGuid() != _indexMarker) throw new Exception("Error in state stream of index " + i + ". ");
            var indexUniqueKey = stream.ReadString();
            if (_definition.TryGetIndex(indexUniqueKey, out var index)) {
                progress("Reading index " + (i + 1) + " of " + noIndexes, (100 * i / noIndexes));
                index.ReadState(stream);
                indexesRedByKey[indexUniqueKey] = true;
            } else {
                throw new InvalidDataException();
            }
        }
        if (stream.ReadGuid() != _indexStoreMarker) throw new Exception("Error in index state stream. ");
        foreach (var index in _definition.GetAllIndexes()) {
            if (!indexesRedByKey.ContainsKey(index.UniqueKey)) {
                anyIndexesMissing = true;
                return;
            }
        }
        anyIndexesMissing = false;
    }
    public void RegisterActionDuringStateLoad(long transactionTimestamp, PrimitiveNodeAction action, bool throwOnErrors, Action<string, Exception> log) {
        _definition.RegisterActionDuringStateLoad(transactionTimestamp, action, throwOnErrors, log);

    }
    public void Dispose() {
        foreach (var index in _definition.GetAllIndexes()) {
            index.Dispose();
        }
    }
    public void ClearCache() {
        foreach (var index in _definition.GetAllIndexes()) {
            index.ClearCache();
        }
    }
}
