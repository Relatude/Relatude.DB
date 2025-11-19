using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Diagnostics;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    byte[][] readSegments(NodeSegment[] segments, out int diskReads) => _wal.ReadNodeSegments(segments, out diskReads);
    void updateNodeDataPositionInLogFile(int id, NodeSegment seg) {
        // this happens when a node is updated, and the new data is written to the log file
        _nodes.UpdateNodeDataPositionInLogFile(id, seg);
    }
    int _stateFileVersion = 1000;
    Guid getCheckSumForStateFileAndIndexes() {
        // anything that can affect indexes or state file:
        var s = System.Text.Json.JsonSerializer.Serialize(Datamodel);
        s += System.Text.Json.JsonSerializer.Serialize(_stateFileVersion);
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
    void saveMainState(long activityId) {
        IOIndex.DeleteIfItExists(_fileKeys.StateFileKey);
        UpdateActivity(activityId, "Opening " + _fileKeys.StateFileKey + "...");
        using var stream = IOIndex.OpenAppend(_fileKeys.StateFileKey);
        stream.WriteVerifiedInt(_stateFileVersion); // fileversion
        stream.WriteVerifiedLong(_wal.LastTimestamp);
        stream.WriteVerifiedLong(_wal.GetPositionAfterLastTransaction());
        stream.WriteGuid(getCheckSumForStateFileAndIndexes()); // must last checksum of dm
        stream.WriteVerifiedLong(_wal.FileSize);
        stream.WriteGuid(_wal.FileId); // must match log file
        UpdateActivity(activityId, "Saving guids");
        _guids.SaveState(stream);
        UpdateActivity(activityId, "Saving segments");
        _nodes.SaveState(stream);
        UpdateActivity(activityId, "Saving native models");
        _nativeModelStore.SaveState(stream);
        UpdateActivity(activityId, "Saving relations");
        _relations.SaveState(stream);
        UpdateActivity(activityId, "Saving node type index");
        _definition.NodeTypeIndex.SaveState(stream);
        stream.WriteLong(_noPrimitiveActionsInLogThatCanBeTruncated);
        _noPrimitiveActionsSinceLastStateSnaphot = 0;
        _noTransactionsSinceLastStateSnaphot = 0;
    }
    void saveIndexesStates(long activityId) {
        UpdateActivity(activityId, "Saving indexes");
        _index.SaveStateForMemoryIndexes(_wal.LastTimestamp, _wal.FileId, (txt, prg) => {
            UpdateActivity(activityId, "Saving index " + txt, prg);
        });
    }
    void readState(bool throwOnErrors, Guid currentModelHash, long activityId) {

        // throwing IndexReadException will cause a delete of all state files and a new try of reload

        long stateFileTimestamp;
        long stateFilePositionOfLastTransactionSaved = 0;
        _noPrimitiveActionsSinceLastStateSnaphot = 0;
        _noTransactionsSinceLastStateSnaphot = 0;
        _noPrimitiveActionsInLogThatCanBeTruncated = 0;
        var sw = Stopwatch.StartNew();
        var walFileSize = _wal.FileSize; // while it is open...
        _wal.Close();
        var walFileId = LogReader.ReadFileId(_wal.FileKey, _io);

        LogInfo("Reading indexes:");
        try {
            _index.ReadStateForMemoryIndexes((txt, prg) => {
                LogInfo("   " + txt);
                UpdateActivity(activityId, "Reading index " + txt, prg / 10);
            }, walFileId); // could introduce lazy loading of indexes later....
            if (PersistedIndexStore != null) {
                if (PersistedIndexStore.WalFileId == Guid.Empty) {
                    PersistedIndexStore.SetWalFileId(walFileId);
                    LogInfo("   Persisted indexes initialized with log file id.");
                } else if (PersistedIndexStore.WalFileId != walFileId) {
                    PersistedIndexStore.ResetAll();
                    LogInfo("   Persisted indexes reset, log file id different.");
                }
            }
        } catch (Exception err) {
            var errMsg = "Failed loading memory index states. " + err.Message;
            throw new StateFileReadException(errMsg, err);
        }
        if (IOIndex.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey)) { // no state file, so read from beginning of log file
            stateFileTimestamp = 0;
            LogInfo("Index state file empty. ");
        } else { // read state, before reading rest from log file
            try {
                LogInfo("Reading state file");
                UpdateActivity(activityId, "Reading state file", 0);
                using var stream = IOIndex.OpenRead(_fileKeys.StateFileKey, 0);
                LogInfo("   State file size: " + stream.Length.ToByteString());
                var version = stream.ReadVerifiedInt();
                if (version != _stateFileVersion) throw new Exception("   State file version mismatch. ");
                stateFileTimestamp = stream.ReadVerifiedLong();
                stateFilePositionOfLastTransactionSaved = stream.ReadVerifiedLong();
                var storedModelHash = stream.ReadGuid();
                if (storedModelHash != currentModelHash) {
                    LogInfo("   Datamodel and settings have changed.");
                    //throw new Exception("Datamodel have changed, checksum does not match.");
                }
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
            } catch (Exception err) {
                var errMsg = "Failed loading index states. " + err.Message; // try to continue with loading from log file
                throw new StateFileReadException(errMsg, err);
            }
        }
        _wal.EnsureTimestamps(stateFileTimestamp); // from statefile, making sure next written transaction is not less than state file

        var whereOutSide = _nodes.Snapshot().Where(n => n.segment.AbsolutePosition + n.segment.Length > walFileSize);
        if (whereOutSide.Any()) throw new StateFileReadException("Some node segments point outside log file. ", null);

        // figuring out from where to read the log file to reach latest state, building on current read state

        long readLogFileFom = stateFilePositionOfLastTransactionSaved;
        if (readLogFileFom > walFileSize) {
            throw new Exception("   Warning: State file position beyond log file size. Cannot use state file. ");
        }
        var oldestPersistedIndexTimestamp = _index.GetOldestPersistedTimestamp();
        if (stateFileTimestamp > oldestPersistedIndexTimestamp) {
            readLogFileFom = 0; // need to read all to build indexes correctly ( this could be optimized later, to search from timestamp in log file )
        }

        int transactionCount = 0;
        int actionCount = 0;
        var readingFrom = stateFileTimestamp > 0 ? "UTC " + new DateTime(stateFileTimestamp, DateTimeKind.Utc) : " the beginning.";
        int positionInPercentage = (int)Math.Round(readLogFileFom * 100d / (walFileSize + 1d));
        long bytesToRead = walFileSize - readLogFileFom;
        LogInfo("Reading log file from " + positionInPercentage.ToString("0") + "% at " + readingFrom + " (" + bytesToRead.ToByteString() + " to read)");
        UpdateActivity(activityId, "Reading log file", 0);
        sw.Restart();
        var lastProgress = 0D;
        var actionCountInTransaction = 0;
        long sizeOfCurrentTransaction;
        var lastBytesRead = 0D;
        PersistedIndexStore?.StartTransaction();
        using (var logReader = new LogReader(_wal.FileKey, _definition, _io, readLogFileFom, stateFileTimestamp)) {
            LogInfo("   Log file size: " + logReader.FileSize.ToByteString());
            double progressBarFactor = (1 - readLogFileFom / logReader.FileSize);
            while (logReader.ReadNextTransaction(out var transaction, throwOnErrors, logCriticalError, out sizeOfCurrentTransaction)) {
                transactionCount++;
                actionCountInTransaction = 0;
                var isTransactionRelevantForStateStores = transaction.Timestamp > stateFileTimestamp;
                var isTransactionRelevantForIndexes = transaction.Timestamp >= oldestPersistedIndexTimestamp;
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
                    if (isTransactionRelevantForIndexes) {
                        _index.RegisterActionDuringStateLoad(transaction.Timestamp, a, throwOnErrors, logCriticalError);
                    }
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
        PersistedIndexStore?.CommitTransaction(_wal.LastTimestamp);        
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
