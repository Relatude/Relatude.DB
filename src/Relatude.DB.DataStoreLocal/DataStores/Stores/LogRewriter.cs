using Relatude.DB.IO;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal class LogRewriter {
    static readonly string _logRewriterStartFile = "rewrite.flag";
    public static void CleanupOldPartiallyCompletedLogRewriteIfAny(IIOProvider io) {
        if (io.DoesNotExistOrIsEmpty(_logRewriterStartFile)) return;
        using var stream = io.OpenRead(_logRewriterStartFile, 0);
        var fileKey = stream.ReadString();
        if (string.IsNullOrWhiteSpace(fileKey)) throw new Exception("Log rewriter start file does not contain a valid file key. ");
        io.DeleteIfItExists(fileKey);
        stream.Dispose();
        io.DeleteIfItExists(_logRewriterStartFile);
    }
    public static bool LogRewriterAlreadyInprogress(IIOProvider io) {
        return !io.DoesNotExistOrIsEmpty(_logRewriterStartFile);
    }
    public static void CreateFlagFileToIndicateLogRewriterInprogress(IIOProvider io, string newLogFileKey) {
        if (LogRewriterAlreadyInprogress(io)) throw new Exception("Log rewriter start file already exists. ");
        using var stream = io.OpenAppend(_logRewriterStartFile);
        stream.WriteString(newLogFileKey);
    }
    public static void DeleteFlagFileToIndicateLogRewriterStart(IIOProvider io, string newLogFileKey) {
        if (io.DoesNotExistOrIsEmpty(_logRewriterStartFile)) throw new Exception("Log rewriter start file does not exist. ");
        using var stream = io.OpenRead(_logRewriterStartFile, 0);
        var fileKey = stream.ReadString();
        if (fileKey != newLogFileKey) throw new Exception("Log rewriter start file does not match new log file key. ");
        stream.Dispose();
        io.DeleteIfItExists(_logRewriterStartFile);
    }
    readonly Definition _definition;
    readonly IIOProvider _destIO;
    public readonly string FileKey;
    List<ExecutedPrimitiveTransaction> _diff;
    (int nodeId, NodeSegment segment)[] _nodes;
    public Dictionary<int, NodeSegment> _newSegements;
    (Guid relId, RelData[] relations)[] _relations;
    readonly WALFile _newStore;
    readonly RegisterNodeSegmentCallbackFunc _registerNodeSegment;
    readonly ReadSegmentsFunc _threadSafeReadSegments;
    bool _finalizing = false;
    public LogRewriter(string newFileKey, Definition definition,
        IIOProvider destinationIO,
        (int nodeId, NodeSegment segment)[] nodes,
        (Guid relId, RelData[] relations)[] relations,
        ReadSegmentsFunc threadSafeReadSegments, // call back to old log file for reading segment content from old file
        RegisterNodeSegmentCallbackFunc registerNodeSegment // call back to store to register node segments in cache ( NodeStore )
        ) {
        FileKey = newFileKey;
        _definition = definition;
        _destIO = destinationIO;
        _destIO.DeleteIfItExists(FileKey);
        _nodes = nodes;
        _relations = relations;
        _threadSafeReadSegments = threadSafeReadSegments;
        _registerNodeSegment = registerNodeSegment;
        _newSegements = new();
        _newStore = new WALFile(FileKey, _definition, _destIO, (nodeId, seg) => {
            _newSegements[nodeId] = seg;
        }, null); // no ValueIndex store
        _diff = new();
    }
    public void RegisterNewTransactionWhileRewriting(ExecutedPrimitiveTransaction t) {
        lock (_diff) _diff.Add(t);
    }
    public void Step1_RewriteLog_NoLockRequired(Action<string, int> reportProgress) { // does not block simultaneous writes or reads
        if (_finalizing) throw new Exception("Finalizing already started. ");
        var dm = _definition.Datamodel;
        var chunkSize = 97;
        var chunks = _nodes.Chunk(chunkSize).ToArray();
        var i = 0;
        foreach (var chunk in chunks) {
            i++;
            reportProgress("Rewriting node " + i * chunkSize + " of " + _nodes.Length, 10+(70 * i / chunks.Length));
            var segmentBytes = _threadSafeReadSegments(chunk.Select(c => c.segment).ToArray(), out _);
            var actions = new List<PrimitiveActionBase>(segmentBytes.Length);
            foreach (var bytes in segmentBytes) {
                var node = FromBytes.NodeData(dm, new MemoryStream(bytes));
                var action = new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                actions.Add(action);
            }
            var t = new ExecutedPrimitiveTransaction(actions, _newStore.NewTimestamp());
            _newStore.QueDiskWrites(t);
            _newStore.FlushToDisk(true);
        }
        i = 0;
        foreach (var r in _relations) {
            i++;
            reportProgress("Rewriting relation " + i + " of " + _relations.Length, 80 + (10 * i / _relations.Length));
            var actions = new List<PrimitiveActionBase>(r.relations.Count());
            foreach (var rel in r.relations) {
                var action = new PrimitiveRelationAction(PrimitiveOperation.Add, r.relId, rel.Source, rel.Target, rel.DateTimeUtc);
                actions.Add(action);
            }
            var t = new ExecutedPrimitiveTransaction(actions, _newStore.NewTimestamp());
            _newStore.QueDiskWrites(t);
            _newStore.FlushToDisk(true);
        }
        // add transactions added while running above code, swap variable to allow new writes to be added while writing
        var d2 = _diff;
        lock (_diff) _diff = new(); // make new so that new transactions can be added while writing
        foreach (var t in d2) _newStore.QueDiskWrites(t);
        _newStore.FlushToDisk(true);
    }
    public void Step2_HotSwap_RequiresWriteLock(WALFile oldLogStore, bool swapToNewFile) { // does rely on simulatenous writes or reads to be blocked
        if (_finalizing) throw new Exception("Finalizing already started. ");
        _finalizing = true;
        foreach (var t in _diff) _newStore.QueDiskWrites(t); // final transactions, added while last step was running
        _newStore.FlushToDisk(true); // flush all writes to disk, so that the new file is ready to be used
        oldLogStore.FlushToDisk(true); // flush old log file, so that it is ready to be replaced
        _newStore.Dispose(); // dispose new store, so that it can be used by the db
        if (swapToNewFile) {
            // if swapping to new file, all node segments must be registered, so that the new file is used
            foreach (var node in _newSegements) {
                _registerNodeSegment(node.Key, node.Value); // ensuring that the new segments are registered in segment cache ( NodeStore )
            }
            oldLogStore.ReplaceDataFile(FileKey, _newStore.LastTimestamp); // replace old log file with new, and allow db to continue
        }
    }
    internal void SetTimestamp(long timestamp) {
        _newStore.StoreTimestamp(timestamp);
    }
}
