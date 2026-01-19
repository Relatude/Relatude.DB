using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Transactions;
using System.Diagnostics;
using Relatude.DB.DataStores.Indexes;

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
    public void WriteNewTimestampDueToRewriteHotswap(long logTimestamp, Guid walFileId) {
        _definition.GetAllIndexes().ForEach(index => index.WriteNewTimestampDueToRewriteHotswap(logTimestamp, walFileId));
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId, Action<string, int> progress) {
        var count = _definition.GetAllIndexes().Count();
        var i = 0;
        foreach (var index in _definition.GetAllIndexes()) {
            progress("Saving index " + ++i + " of " + count, 100 * i / count);
            index.SaveStateForMemoryIndexes(logTimestamp, walFileId);
        }
    }
    public void ReadStateForMemoryIndexes(Action<string, int> progress, Guid walFileId) {
        var swTotal = Stopwatch.StartNew();
        var count = _definition.GetAllIndexes().Count();
        var i = 0;
        var readIndex = (IIndex index) => {
            var sw = Stopwatch.StartNew();
            //progress("Starting: " + index.FriendlyName, 100 * i / count);
            index.ReadStateForMemoryIndexes(walFileId);
            //progress("Completed: " + index.FriendlyName + " - " + sw.ElapsedMilliseconds.To1000N() + "ms", 100 * i / count);
            progress(index.FriendlyName + " - " + sw.ElapsedMilliseconds.To1000N() + "ms", 100 * i / count);
            i++;
        };
        var allowParallelism = true;
        if (allowParallelism) Parallel.ForEach(_definition.GetAllIndexes(), readIndex);
        else foreach (var index in _definition.GetAllIndexes()) readIndex(index);
        //progress("Completed reading index states - " + swTotal.ElapsedMilliseconds.To1000N() + "ms", 100);
    }
    public void RegisterActionDuringStateLoad(long transactionTimestamp, PrimitiveActionBase action, bool throwOnErrors, Action<string, Exception> log) {
        if (action is not PrimitiveNodeAction na) return;
        _definition.RegisterActionDuringStateLoad(transactionTimestamp, na, throwOnErrors, log);
    }
    public void ClearCache() => _definition.GetAllIndexes().ForEach(index => index.ClearCache());
    internal long GetOldestPersistedTimestamp() => _definition.GetAllIndexes().Min(i => i.PersistedTimestamp);
    public void Dispose() => _definition.GetAllIndexes().ForEach(index => index.Dispose());
}
