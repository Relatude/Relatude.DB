using System.Diagnostics;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Transactions;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    byte[][] readSegments(NodeSegment[] segments, out int diskReads) => _wal.ReadNodeSegments(segments, out diskReads);
    void updateNodeDataPositionInLogFile(int id, NodeSegment seg) {
        // this happens when a node is updated, and the new data is written to the log file
        _nodes.UpdateNodeDataPositionInLogFile(id, seg);
    }
    int _fileVersion = 100;
    Guid getCheckSumForStateFileAndIndexes() {
        // anything that can affect indexes or state file:
        var s = System.Text.Json.JsonSerializer.Serialize(Datamodel);
        s += System.Text.Json.JsonSerializer.Serialize(_fileVersion);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.PersistedTextIndexEngine);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.PersistedValueIndexEngine);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.UsePersistedTextIndexesByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.UsePersistedValueIndexesByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.EnableTextIndexByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.PersistedValueIndexFolderPath);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.EnableSemanticIndexByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.FilePrefix);
        return s.GenerateGuid();
        //var g = s.GenerateGuid();
        //Log(SystemLogEntryType.Info, "Model hash: " + g);
        //File.WriteAllText("C:\\WAF_Temp\\" + g, s);
        //return g;
    }
    void saveState() {
        _io.DeleteIfItExists(_fileKeys.StateFileKey);
        using var stream = _io.OpenAppend(_fileKeys.StateFileKey);
        stream.WriteVerifiedInt(_fileVersion); // fileversion
        stream.WriteVerifiedLong(_wal.LastTimestamp);
        stream.WriteGuid(getCheckSumForStateFileAndIndexes()); // must last checksum of dm
        stream.WriteVerifiedLong(_wal.FileSize);
        stream.WriteGuid(_wal.FileId); // must match log file
        _guids.SaveState(stream);
        _nodes.SaveState(stream);
        _nativeModelStore.SaveState(stream);
        _relations.SaveState(stream);
        _definition.NodeTypeIndex.SaveState(stream);
        stream.WriteLong(_noPrimitiveActionsInLogThatCanBeTruncated);
        _noPrimitiveActionsSinceLastStateSnaphot = 0;
        _noTransactionsSinceLastStateSnaphot = 0;
        _index.SaveStateForMemoryIndexes(_wal.LastTimestamp);
    }
    void readState(bool throwOnErrors, Guid currentModelHash, long activityId) {

        // throwing IndexReadException will cause a delete of all state files and a new try of reload

        long stateFileTimestamp;
        _noPrimitiveActionsSinceLastStateSnaphot = 0;
        _noTransactionsSinceLastStateSnaphot = 0;
        _noPrimitiveActionsInLogThatCanBeTruncated = 0;
        var sw = Stopwatch.StartNew();
        var walFileSize = _wal.FileSize; // while it is open...
        _wal.Close();
        var walFileId = LogReader.ReadFileId(_wal.FileKey, _io);
        LogInfo("Reading indexes...");
        _index.ReadStateForMemoryIndexes(); // could introduce lazy loading of indexes later....
        if (_io.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey)) { // no state file, so read from beginning of log file
            stateFileTimestamp = 0;
            LogInfo("Index state file empty. ");
        } else { // read state, before reading rest from log file
            try {
                LogInfo("Reading state file");
                UpdateActivity(activityId, "Reading state file", 0);
                using var stream = _io.OpenRead(_fileKeys.StateFileKey, 0);
                LogInfo("   State file size: " + stream.Length.ToByteString());
                var version = stream.ReadVerifiedInt();
                if (version != _fileVersion) throw new Exception("   State file version mismatch. ");
                stateFileTimestamp = stream.ReadVerifiedLong();
                var storedModelHash = stream.ReadGuid();
                if (storedModelHash != currentModelHash) throw new Exception("Datamodel have changed, checksum does not match.");
                var logFileSize = stream.ReadVerifiedLong();
                var fileId = stream.ReadGuid();
                if (fileId != walFileId) throw new Exception("Statefile does not belong to log file. It cannot be used. ");
                UpdateActivity(activityId, "Reading id registry", 5);
                _guids.ReadState(stream);
                _nodes.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(5 + p! * 0.03))); // 5-8%
                UpdateActivity(activityId, "Reading native models", 8); // 8%-10%
                _nativeModelStore.ReadState(stream);
                _relations.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(10 + p! * 0.05))); // 10-15%
                _definition.NodeTypeIndex.ReadState(stream);
                _noPrimitiveActionsInLogThatCanBeTruncated = stream.ReadLong();
                var bytesPerSecond = stream.Length / (sw.ElapsedMilliseconds / 1000D);
                LogInfo("   State file read in " + sw.ElapsedMilliseconds.To1000N() + "ms - " + bytesPerSecond.ToByteString() + "/s");
                UpdateActivity(activityId, "State file read", 100);
                validateStateInfoIfDebug();
            } catch (Exception err) {
                var errMsg = "Failed loading index states. " + err.Message; // try to continue with loading from log file
                throw new StateFileReadException(errMsg, err);
            }
        }
        _wal.EnsureTimestamps(stateFileTimestamp); // from statefile, making sure next written transaction is not less than state file

        // figuring out from where to read the log file to reach latest state, building on current read state
        var lowestTimestamp = Math.Min(_index.GetLowestTimestamp(), stateFileTimestamp);
        _wal.FindPositionOfTimestamp(lowestTimestamp, out var positionOfLastTransactionSavedToStateFile);

        int transactionCount = 0;
        int actionCount = 0;
        var readingFrom = stateFileTimestamp > 0 ? "UTC " + new DateTime(stateFileTimestamp, DateTimeKind.Utc) : " the beginning.";
        var positionInPercentage = positionOfLastTransactionSavedToStateFile * 100 / (walFileSize + 1);
        LogInfo("Reading log file from " + positionInPercentage.ToString("0") + "% " + readingFrom);
        UpdateActivity(activityId, "Reading log file", 0);
        sw.Restart();
        var lastProgress = 0D;
        var actionCountInTransaction = 0;
        long sizeOfCurrentTransaction;
        var lastBytesRead = 0D;
        using (var logReader = new LogReader(_wal.FileKey, _definition, _io, positionOfLastTransactionSavedToStateFile, stateFileTimestamp)) {
            LogInfo("   Log file size: " + logReader.FileSize.ToByteString());
            double progressBarFactor = (1 - positionOfLastTransactionSavedToStateFile / logReader.FileSize);
            while (logReader.ReadNextTransaction(out var transaction, throwOnErrors, logCriticalError, out sizeOfCurrentTransaction)) {
                transactionCount++;
                actionCountInTransaction = 0;
                var isTransactionRelevantForStateStores = transaction.Timestamp > stateFileTimestamp;
                // "stateSstores" are: _guids, _nodes, _relations, _nativeModelStore, _definition.NodeTypeIndex,
                // but not _index which may need all actions to build correctly
                foreach (var a in transaction.ExecutedActions) {
                    actionCount++;
                    actionCountInTransaction++;
                    if (actionCount % 100 == 0 && (sw.ElapsedMilliseconds - lastProgress > 200)) {
                        var remainingInTrans = 1D - (double)actionCountInTransaction / transaction.ExecutedActions.Count;
                        var estimatedByteProgressInTransaction = sizeOfCurrentTransaction * remainingInTrans;
                        var readBytes = logReader.Position - estimatedByteProgressInTransaction;
                        var totalBytes = logReader.FileSize;
                        var remainingMs = readBytes > 0 ? (totalBytes - readBytes) * (sw.ElapsedMilliseconds / readBytes) : 0;
                        var remaining = (remainingMs > 0 && sw.ElapsedMilliseconds > 3000) ? (" - " + TimeSpan.FromMilliseconds(remainingMs).ToTimeString()) : "";
                        var estimatedTotalProgress = readBytes * 100D / totalBytes;
                        var deltaBytes = readBytes - lastBytesRead;
                        var deltaSeconds = sw.ElapsedMilliseconds - lastProgress;
                        var bytesPerSecond = deltaBytes / (deltaSeconds / 1000D);
                        lastProgress = (int)sw.ElapsedMilliseconds;
                        var desc = "   - " + (int)estimatedTotalProgress + "% - " + readBytes.ToByteString() + " - " + bytesPerSecond.ToByteString() + "/s" + " - " + actionCount.To1000N() + " actions" + remaining;
                        LogInfo(desc);
                        var progressBar = progressBarFactor > 0 ? (int)(estimatedTotalProgress / progressBarFactor) : 100;
                        UpdateActivity(activityId, desc.Trim(), progressBar);
                        lastBytesRead = readBytes;
                    }
                    _index.RegisterActionDuringStateLoad(transaction.Timestamp, a, throwOnErrors, logCriticalError);
                    if (isTransactionRelevantForStateStores) {
                        _guids.RegisterAction(a);
                        if (a is PrimitiveNodeAction na) {
                            _nodes.RegisterAction_NotThreadsafe(na);
                            _definition.NodeTypeIndex.RegisterActionDuringStateLoad(na, throwOnErrors, logCriticalError);
                        } else if (a is PrimitiveRelationAction ra) {
                            _relations.RegisterActionIfPossible(ra); // Simple validation omits fetching nodes to check types etc, would be slow and cause multiple open stream problems
                        } else throw new NotImplementedException();
                        _nativeModelStore.RegisterActionDuringStateLoad(a, throwOnErrors, logCriticalError);
                        _noPrimitiveActionsSinceLastStateSnaphot++;
                        if (a.Operation == PrimitiveOperation.Remove) _noPrimitiveActionsInLogThatCanBeTruncated++;
                    }
                }
                if (isTransactionRelevantForStateStores) {
                    _noTransactionsSinceLastStateSnaphot++;
                }
                _wal.EnsureTimestamps(transaction.Timestamp);
            }
        }
        if(PersistedIndexStore != null) PersistedIndexStore.FlushAndCommitTimestamp(_wal.LastTimestamp); // ensure persisted indexes have updated timestamp
        _wal.OpenForAppending(); // read for appending again
        validateStateInfoIfDebug();
        if (actionCount > 0) {
            LogInfo("   Read " + actionCount.To1000N() + " actions from log file in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        } else {
            LogInfo("   No actions read from log file.");
        }
        LogInfo(_noPrimitiveActionsInLogThatCanBeTruncated.To1000N() + " actions redundant in log file. ");
        LogInfo(_nodes.Count.To1000N() + " nodes in total");
        LogInfo(_relations.TotalCount().To1000N() + " relations in total");
        LogInfo(_nativeModelStore.CountUsers.To1000N() + " system users");
        LogInfo(_nativeModelStore.CountUserGroups.To1000N() + " user groups");
        LogInfo(_nativeModelStore.CountCultures.To1000N() + " cultures");
        LogInfo(_nativeModelStore.CountCollections.To1000N() + " collections");


    }
}
