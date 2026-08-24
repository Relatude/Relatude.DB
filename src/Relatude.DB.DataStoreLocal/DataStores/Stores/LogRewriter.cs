using Relatude.DB.Common;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Stores;
internal class LogRewriter {
    static readonly string[] _logRewriterStartFile = ["rewrite.flag"];
    public static void CleanupOldPartiallyCompletedLogRewriteIfAny(IIOProvider io, FileKeyUtility keys) {
        if (io.DoesNotExistOrIsEmpty(_logRewriterStartFile)) return;
        string flaggedKey; // the flag file stores the key in its joined form
        using (var stream = io.OpenRead(_logRewriterStartFile, 0)) {
            flaggedKey = stream.ReadString();
        }
        if (string.IsNullOrWhiteSpace(flaggedKey)) throw new Exception("Log rewriter start file does not contain a valid file key. ");
        var fileKey = flaggedKey.SplitKey();
        // A flag written before the folder layout holds a root level key; the startup migration has
        // since moved the file into the data folder, so the flagged key must be mapped with it or
        // the partial rewrite output would survive there and be picked up as the latest log file.
        if (fileKey.Length == 1) {
            var migrated = FileKeyUtility.MapLegacyRootFileKeyToDataFolder(fileKey);
            if (!io.Exists(fileKey) && io.Exists(migrated)) fileKey = migrated;
        }
        // Defensive: never delete the flagged file if it is the only log file present.
        // While a rewrite is running the old log file always exists alongside the new one,
        // so if the flagged file is the only log file the hot swap must have completed
        // and the flagged file is the live log file. Deleting it would lose all data.
        var allLogFiles = keys.WAL_GetAllFileKeys(io);
        var flaggedFileIsOnlyLogFile = allLogFiles.Length == 1 && allLogFiles[0].IsSameKey(fileKey);
        if (!flaggedFileIsOnlyLogFile) io.DeleteFileIfItExists(fileKey);
        io.DeleteFileIfItExists(keys.StateFileKey); // delete state file as well it may contain references to an old log file
        io.DeleteFileIfItExists(_logRewriterStartFile);
    }
    public static bool LogRewriterAlreadyInprogress(IIOProvider io) {
        return !io.DoesNotExistOrIsEmpty(_logRewriterStartFile);
    }
    public static void CreateFlagFileToIndicateLogRewriterInprogress(IIOProvider io, string[] newLogFileKey) {
        if (LogRewriterAlreadyInprogress(io)) throw new Exception("Log rewriter start file already exists. ");
        using var stream = io.OpenAppend(_logRewriterStartFile);
        stream.WriteString(newLogFileKey.AsKeyString()); // stored in its joined form
    }
    public static void DeleteFlagFileToIndicateLogRewriterStart(IIOProvider io, string[] newLogFileKey) {
        if (io.DoesNotExistOrIsEmpty(_logRewriterStartFile)) throw new Exception("Log rewriter start file does not exist. ");
        using var stream = io.OpenRead(_logRewriterStartFile, 0);
        var fileKey = stream.ReadString();
        if (fileKey != newLogFileKey.AsKeyString()) throw new Exception("Log rewriter start file does not match new log file key. ");
        stream.Dispose();
        io.DeleteFileIfItExists(_logRewriterStartFile);
    }
    readonly Definition _definition;
    readonly IIOProvider _destIO;
    public readonly string[] FileKey;
    public volatile bool _cancelled = false; // volatile, set by Cancel() on another thread while Step1 is running
    List<ExecutedPrimitiveTransaction> _newTransactionsWhileRewriting;
    (int nodeId, NodeSegment segment)[] _snapshot;
    public Dictionary<int, NodeSegment> _newSegements;
    (Guid relId, RelData[] relations, PrimitiveRelationReorderAction[] reorders)[] _relations;
    readonly WALFile _newWAL;
    readonly RegisterNodeSegmentCallbackFunc _registerNodeSegment;
    readonly ReadSegmentsFunc _threadSafeReadSegments;
    bool _finalizing = false;
    public LogRewriter(string[] newFileKey, Definition definition,
        IIOProvider destinationIO,
        (int nodeId, NodeSegment segment)[] snapshot,
        (Guid relId, RelData[] relations, PrimitiveRelationReorderAction[] reorders)[] relations,
        ReadSegmentsFunc threadSafeReadSegments, // call back to old log file for reading segment content from old file
        RegisterNodeSegmentCallbackFunc registerNodeSegment // call back to store to register node segments in cache ( NodeStore )
        ) {
        FileKey = newFileKey;
        _definition = definition;
        _destIO = destinationIO;
        _destIO.DeleteFileIfItExists(FileKey);
        _snapshot = snapshot;
        // validate snapshot:
        var whereNull = snapshot.Where(n => n.segment.Length == 0);
        if(whereNull.Any()) throw new Exception("Some node segments have zero length. ");

        _relations = relations;
        _threadSafeReadSegments = threadSafeReadSegments;
        _registerNodeSegment = registerNodeSegment;
        _newSegements = new();
        _newWAL = new WALFile(FileKey, _definition, _destIO, (nodeId, seg) => {
            _newSegements[nodeId] = seg;
        }, null, null); // no ValueIndex store, or secondary log store
        _newTransactionsWhileRewriting = new();
    }
    public void Cancel(FileKeyUtility fileKeys) {
        _cancelled = true;
        _newWAL.Dispose();
        _destIO.DeleteFileIfItExists(FileKey);
        if (LogRewriterAlreadyInprogress(_destIO)) DeleteFlagFileToIndicateLogRewriterStart(_destIO, FileKey); // flag file is already deleted if the hot swap completed
        CleanupOldPartiallyCompletedLogRewriteIfAny(_destIO, fileKeys);
    }
    public void RegisterNewTransactionWhileRewriting(ExecutedPrimitiveTransaction t) {
        lock (_newTransactionsWhileRewriting) _newTransactionsWhileRewriting.Add(t);
    }
    public void Step1_RewriteLog_NoLockRequired(Action<string, int> reportProgress) { // does not block simultaneous writes or reads
        if (_finalizing) throw new Exception("Finalizing already started. ");
        try {
            step1RewriteLog(reportProgress);
        } catch (ObjectDisposedException err) {
            // Cancel() disposes _newWAL from another thread while this method may be writing,
            // treat a disposed stream as a cancellation instead of an unknown error
            if (_cancelled) throw new OperationCanceledException("Log rewrite cancelled. ", err);
            throw;
        }
    }
    void step1RewriteLog(Action<string, int> reportProgress) {
        var dm = _definition.Datamodel;
        var chunkSize = 97;
        var chunks = _snapshot.Chunk(chunkSize).ToArray();
        var i = 0;
        foreach (var chunk in chunks) {
            i++;
            if(_cancelled)  throw new OperationCanceledException("Log rewrite cancelled. ");
            reportProgress("Writing node " + (i * chunkSize).To1000N() + " of " + _snapshot.Length.To1000N(), 10 + (70 * i / chunks.Length));
            var segmentBytes = _threadSafeReadSegments(chunk.Select(c => c.segment).ToArray(), out _);
            var actions = new List<PrimitiveActionBase>(segmentBytes.Length);
            foreach (var bytes in segmentBytes) {
                var node = FromBytes.NodeData(dm, new MemoryStream(bytes), null);
                var action = new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                actions.Add(action);
            }
            var t = new ExecutedPrimitiveTransaction(actions, _newWAL.NewTimestamp());
            _newWAL.QueDiskWrites(t);
            _newWAL.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
        }
        i = 0;
        foreach (var r in _relations) {
            i++;
            if(_cancelled)  throw new OperationCanceledException("Log rewrite cancelled. ");
            reportProgress("Writing relation " + i + " of " + _relations.Length, 80 + (10 * i / _relations.Length));
            var actions = new List<PrimitiveActionBase>(r.relations.Length + r.reorders.Length);
            foreach (var rel in r.relations) {
                var action = new PrimitiveRelationAction(PrimitiveOperation.Add, r.relId, rel.Source, rel.Target, rel.DateTimeUtc);
                actions.Add(action);
            }
            actions.AddRange(r.reorders); // restores list orders that the plain add sequence cannot reproduce
            var t = new ExecutedPrimitiveTransaction(actions, _newWAL.NewTimestamp());
            _newWAL.QueDiskWrites(t);
            _newWAL.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
        }
        // add transactions added while running above code, swap variable to allow new writes to be added while writing
        var d2 = _newTransactionsWhileRewriting;
        lock (_newTransactionsWhileRewriting) _newTransactionsWhileRewriting = new(); // make new so that new transactions can be added while writing
        
        foreach (var t in d2) _newWAL.QueDiskWrites(t);
        _newWAL.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
    }
    public void Step2_HotSwap_RequiresWriteLock(WALFile oldLogStore, bool swapToNewFile) { // does rely on simulatenous writes or reads to be blocked
        if (_finalizing) throw new Exception("Finalizing already started. ");
        _finalizing = true;

        foreach (var t in _newTransactionsWhileRewriting) {
            if (_cancelled) throw new OperationCanceledException("Log rewrite cancelled. ");
            _newWAL.QueDiskWrites(t); // final transactions, added while last step was running
        }
        _newWAL.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true); // flush all writes to disk, so that the new file is ready to be used
        _newWAL.Dispose(); // dispose new store, so that it can be used by the db
        if (swapToNewFile) {
            if (_cancelled) throw new OperationCanceledException("Log rewrite cancelled. ");
            // if swapping to new file, all node segments must be registered, so that the new file is used
            oldLogStore.ReplaceDataFile(FileKey, _newWAL.LastTimestamp, _newWAL.DetachChainHeads()); // replace old log file with new, and allow db to continue
            foreach (var node in _newSegements) {
                if (_cancelled) throw new OperationCanceledException("Log rewrite cancelled. ");
                _registerNodeSegment(node.Key, node.Value); // ensuring that the new segments are registered in segment cache ( NodeStore )
            }
        }
    }
    internal void SetTimestamp(long timestamp) {
        if (_cancelled) throw new OperationCanceledException("Log rewrite cancelled. ");
        _newWAL.StoreTimestamp(timestamp);
    }
}
