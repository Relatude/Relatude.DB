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
    public void SaveStateForMemoryIndexes(long timestamp) => _definition.GetAllIndexes().ForEach(index => index.SaveStateForMemoryIndexes(timestamp));
    public void ReadStateForMemoryIndexes() {
        //Parallel.ForEach(_definition.GetAllIndexes(), index => index.ReadStateForMemoryIndexes());
        _definition.GetAllIndexes().ForEach(index => index.ReadStateForMemoryIndexes());
    }
    public void RegisterActionDuringStateLoad(long transactionTimestamp, PrimitiveActionBase action, bool throwOnErrors, Action<string, Exception> log) {
        if (action is not PrimitiveNodeAction na) return;
        _definition.RegisterActionDuringStateLoad(transactionTimestamp, na, throwOnErrors, log);
    }
    public void ClearCache() => _definition.GetAllIndexes().ForEach(index => index.ClearCache());
    internal long GetLowestTimestamp() => _definition.GetAllIndexes().Min(i => i.Timestamp);
    public void Dispose() => _definition.GetAllIndexes().ForEach(index => index.Dispose());
}
