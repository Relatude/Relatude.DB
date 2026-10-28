using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Text.Json;
namespace Relatude.DB.DataStores;

// The indexes of the cultures. A culture-sensitive property has an index per culture (Definition.IndexedCultures),
// but the indexes are made when the store is set up, before it has read its cultures, which are nodes. So the store
// keeps a record of the cultures it had indexes for, makes those at the start, and when it has read its cultures
// makes the indexes of new ones and takes away those of cultures gone: at the open, and after every transaction
// that makes or removes a culture. A new culture's indexes are filled from the nodes, as the index of no culture
// would be, since no node has a revision in it yet. The record also says how the indexes were made: by a version
// that put a node's revisions in the indexes another way, which the open makes all of them again for.
public sealed partial class DataStoreLocal {
    // 2: a node with revisions gives the indexes what its readers read (Definition.IteratePropertyIndexes), where
    // version 1 (no record) gave the first published revision it had of each property
    const int _indexRecordVersion = 2;
    sealed class IndexRecord {
        public int Version { get; set; }
        public Guid[] Cultures { get; set; } = [];
    }
    // set up with the store, before Definition.Initialize makes the indexes (initialize)
    bool _indexesFromOlderVersion;
    void readIndexRecord() {
        IndexRecord? record = null;
        var key = FileKeyUtility.State_IndexRecordFileKey;
        try {
            if (IOIndex.ExistsAndIsNotEmpty(key)) record = JsonSerializer.Deserialize<IndexRecord>(IOIndex.ReadAllBytes(key));
        } catch (Exception err) {
            LogError("Could not read the index record " + key.AsKeyString() + ". ", err);
        }
        _definition.IndexedCultures = record?.Cultures ?? [];
        _indexesFromOlderVersion = (record?.Version ?? 1) < _indexRecordVersion;
    }
    void writeIndexRecord() {
        var record = new IndexRecord { Version = _indexRecordVersion, Cultures = _definition.IndexedCultures };
        IOIndex.WriteAllBytes(FileKeyUtility.State_IndexRecordFileKey, JsonSerializer.SerializeToUtf8Bytes(record));
        _indexesFromOlderVersion = false;
    }
    /// <summary>
    /// Called when the indexes are read, before the log is: indexes an older version made, holding values the
    /// removals of this one would not find, are made again. Throws the exception that has the open start over
    /// from the log, after <see cref="resetStateAndIndexes"/>, which writes the record of this version.
    /// </summary>
    void rebuildIndexesFromOlderVersion() {
        if (!_indexesFromOlderVersion) return;
        var made = _definition.GetAllIndexes().FirstOrDefault(i => i.PersistedTimestamp > 0);
        if (made == null) return; // every index is read from the beginning of the log anyway
        throw new StateFileReadException("The indexes were made by an earlier version, which put the revisions of a node in them differently (index \"" + made.FriendlyName + "\"). ", null);
    }
    /// <summary>
    /// Brings the culture indexes in line with the store's cultures and writes the record. At the open, after the
    /// state is read; at run time, under the write lock, inside the engines' transaction of the one that changed them.
    /// </summary>
    void syncCultureIndexes(bool atOpen) {
        var cultures = _nativeModelStore.CultureIdsInOrder;
        var (made, removed) = _definition.SyncCultureIndexes(cultures);
        foreach (var index in removed) {
            // an engine drops what nothing opens at the next open; a memory index has only its files
            foreach (var file in FileKeyUtility.Index_GetAllFileKeys(IOIndex, index.UniqueKey)) IOIndex.DeleteFileIfItExists(file);
            LogInfo("Removed index \"" + index.FriendlyName + "\" of a culture that is gone. ");
        }
        if (made.Count > 0) {
            if (atOpen) Engines.BeginTransaction();
            fillIndexes(made);
            if (atOpen) {
                Engines.CommitTransaction(_wal.LastTimestamp);
                Engines.MakeDurable(_wal.LastTimestamp);
                foreach (var index in made) index.SaveStateForMemoryIndexes(_wal.LastTimestamp, _wal.FileId);
            }
            // at run time a memory index is saved with the others: until then it is new, and read from the log at an open
        }
        if (atOpen || made.Count > 0 || removed.Count > 0 || _indexesFromOlderVersion) writeIndexRecord();
    }
    /// <summary>Fills new indexes from the nodes that have the properties they belong to.</summary>
    void fillIndexes(List<IIndex> indexes) {
        var sw = Stopwatch.StartNew();
        var filling = indexes.ToHashSet();
        var typeIds = _definition.Properties.Values.Where(p => p.AllIndexes.Any(filling.Contains)).Select(p => p.Model.NodeType).Distinct().ToArray();
        var ids = new HashSet<int>();
        foreach (var typeId in typeIds) ids.UnionWith(_definition.GetAllIdsForTypeNoAccessControl(typeId, true).Enumerate());
        foreach (var batch in ids.Chunk(1000)) {
            foreach (var node in _nodes.Get(batch)) {
                _definition.IteratePropertyIndexes(node, (n, index, value) => {
                    if (filling.Contains(index)) index.RegisterAddDuringStateLoad(n.__Id, value);
                });
            }
        }
        foreach (var index in indexes) index.CompleteStateLoad();
        LogInfo("Made " + indexes.Count + " culture index" + (indexes.Count == 1 ? "" : "es") + " from " + ids.Count.To1000N() + " nodes in " + sw.ElapsedMilliseconds.To1000N() + "ms: "
            + string.Join(", ", indexes.Select(i => i.FriendlyName)));
    }
    /// <summary>
    /// A culture that nodes or their revisions are in cannot be removed: where a reader of another culture falls
    /// back to would change for them, and with it what they put in the indexes, so they would leave the indexes with
    /// other values than they went in with (<see cref="Definitions.Definition.IteratePropertyIndexes"/>).
    /// Called as the transaction ends, so a culture removed and put back (an update) passes.
    /// </summary>
    void validateCultureRemovals(List<PrimitiveActionBase> executed) {
        foreach (var a in executed) {
            if (a is not PrimitiveNodeAction { Operation: PrimitiveOperation.Remove } na) continue;
            if (!_nativeModelStore.IsCultureNode(na.Node.NodeType)) continue;
            var cultureId = na.Node.Id;
            if (_nativeModelStore.IsCulture(cultureId)) continue; // put back
            if (_definition.NodeTypeIndex.AnyInCulture(cultureId)) {
                var code = na.Node.TryGetValue(NodeConstants.NativeCulturePropertyCultureCode, out var c) ? c as string : null;
                throw new ExceptionWithoutIntegrityLoss("The culture " + (string.IsNullOrEmpty(code) ? cultureId.ToString() : code)
                    + " cannot be removed while nodes or revisions are in it. Delete them or move them to another culture first. ");
            }
        }
    }
}
