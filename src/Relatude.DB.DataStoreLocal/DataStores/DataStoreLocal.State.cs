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
        stream.WriteVerifiedLong(_wal.GetPositionOfLastTransaction());
        stream.WriteGuid(getCheckSumForStateFileAndIndexes()); // must last checksum of dm
        stream.WriteVerifiedLong(_wal.FileSize);
        stream.WriteGuid(_wal.FileId); // must match log file
        _guids.SaveState(stream);
        _nodes.SaveState(stream);
        _relations.SaveState(stream);
        _index.SaveState(stream);
        stream.WriteLong(_noPrimitiveActionsInLogThatCanBeTruncated);
        _noPrimitiveActionsSinceLastStateSnaphot = 0;
        _noTransactionsSinceLastStateSnaphot = 0;
        if (PersistedIndexStore != null) PersistedIndexStore.Commit(_wal.LastTimestamp);
    }
    void readState(bool throwOnBadStateFile, Guid currentModelHash, long activityId) {

        // throwing IndexReadException will cause a delete of all state files and a new try of reload

        long positionOfLastTransactionSavedToStateFile;
        long lastTimestamp;
        _noPrimitiveActionsSinceLastStateSnaphot = 0;
        _noTransactionsSinceLastStateSnaphot = 0;
        _noPrimitiveActionsInLogThatCanBeTruncated = 0;
        var sw = Stopwatch.StartNew();

        if (PersistedIndexStore != null) {
            if (PersistedIndexStore.LogFileId == Guid.Empty) {
                throw new IndexReadException("Persisted index missing, re-open necessary. ", null);
            }
            if (PersistedIndexStore.LogFileId != _wal.FileId) {
                // will cause a delete of all state files and a new try of reload
                throw new IndexReadException("Index state file does not belong to log file. It cannot be used. ", null);
            }
        } else {  // delete
            if (_io is IOProviderDisk iODisk) {
                var path = iODisk.BaseFolder;
                var prefix = _settings.FilePrefix;
                IPersistedIndexStore.DeleteFilesInDefaultFolder(path, prefix);
            }
        }

        if (_io.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey)) { // no state file, so read from beginning of log file
            lastTimestamp = 0;
            positionOfLastTransactionSavedToStateFile = 0;
            LogInfo("Index state file empty. ");
            if (PersistedIndexStore != null) {
                if (PersistedIndexStore.ModelHash != currentModelHash) {
                    // if there is no state file, but there is persisted index store and the datamodel has changed
                    LogInfo("Resetting persisted index store, datamodel has changed. ");
                    PersistedIndexStore.Reset(_wal.FileId, currentModelHash);
                }
            }
        } else { // read state, before reading rest from log file
            try {
                LogInfo("Reading state file");
                UpdateActivity(activityId, "Reading state file", 0);
                using var stream = _io.OpenRead(_fileKeys.StateFileKey, 0);
                LogInfo("   State file size: " + stream.Length.ToByteString());
                var version = stream.ReadVerifiedInt();
                if (version != _fileVersion) throw new Exception("   State file version mismatch. ");
                lastTimestamp = stream.ReadVerifiedLong();
                positionOfLastTransactionSavedToStateFile = stream.ReadVerifiedLong();
                var storedModelHash = stream.ReadGuid();
                if (storedModelHash != currentModelHash)
                    throw new Exception("Datamodel have changed, checksum does not match.");
                var logFileSize = stream.ReadVerifiedLong();
                var fileId = stream.ReadGuid();
                if (fileId != _wal.FileId) throw new Exception("Statefile does not belong to log file. It cannot be used. ");

                if (PersistedIndexStore != null) {
                    if (PersistedIndexStore.Timestamp < lastTimestamp) {
                        throw new Exception("Statefile was created from a newer log file. State file cannot be used. ");
                    }
                }

                if (_wal.FileSize < logFileSize) throw new Exception("State file was created from a longer log file. Longer means later and therefore newer log file. State file cannot be used. ");
                UpdateActivity(activityId, "Reading id registry", 5);
                _guids.ReadState(stream);
                _nodes.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(5 + p! * 0.05))); // 5-10%
                _relations.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(10 + p! * 0.05))); // 10-15%
                _index.ReadState(stream, out var anyIndexesMissing, (d, p) => UpdateActivity(activityId, d, (int)(15 + p! * 0.85))); // 15-100%
                if (anyIndexesMissing) throw new Exception("Some indexes are missing. "); // causes reload with no state file ( and rebuild of indexes )
                _noPrimitiveActionsInLogThatCanBeTruncated = stream.ReadLong();
                var bytesPerSecond = stream.Length / (sw.ElapsedMilliseconds / 1000D);
                LogInfo("   State file read in " + sw.ElapsedMilliseconds.To1000N() + "ms - " + bytesPerSecond.ToByteString() + "/s");
                UpdateActivity(activityId, "State file read", 100);
            } catch (Exception err) {
                var errMsg = "Failed loading index states. " + err.Message; // try to continue with loading from log file
                throw new IndexReadException(errMsg, err);
            }
        }
        _wal.EnsureTimestamps(lastTimestamp); // from statefile, making sure log is not less than state file        
        LogReader? logReader = null;
        int transactionCount = 0;
        int actionCount = 0;
        validateIndexesIfDebug();
        var readingFrom = lastTimestamp > 0 ? "UTC " + new DateTime(lastTimestamp, DateTimeKind.Utc) : " the beginning.";
        var positionInPercentage = positionOfLastTransactionSavedToStateFile * 100 / _wal.FileSize;
        LogInfo("Reading log file from " + positionInPercentage.ToString("0") + "% " + readingFrom);
        UpdateActivity(activityId, "Reading log file", 0);
        sw.Restart();
        var lastProgress = 0D;
        var actionCountInTransaction = 0;
        long sizeOfCurrentTransaction;
        var lastBytesRead = 0D;
        try {
            logReader = _wal.CreateLogReader(positionOfLastTransactionSavedToStateFile, lastTimestamp);
            LogInfo("   Log file size: " + logReader.FileSize.ToByteString());
            double progressBarFactor = (1 - positionOfLastTransactionSavedToStateFile / logReader.FileSize);
            while (logReader.ReadNextTransaction(out var transaction, throwOnBadStateFile, logCriticalError, out sizeOfCurrentTransaction)) {
                transactionCount++;
                actionCountInTransaction = 0;
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
                        var desc = "   - " + (int)estimatedTotalProgress
                            + "% - " + readBytes.ToByteString()
                            //+ " (" + totalBytes.ToByteString() + ")"
                            + " - " + bytesPerSecond.ToByteString() + "/s"
                            //+ " - " + transactionCount.To1000N() + " transactions"
                            + " - " + actionCount.To1000N() + " actions"
                            + remaining;
                        //    0-100% - 1.2GB (4.5GB) - 12MB/s - 2345 transactions - 34567 actions - 1m23s remaining
                        LogInfo(desc);
                        var progressBar = progressBarFactor > 0 ? (int)(estimatedTotalProgress / progressBarFactor) : 100;
                        UpdateActivity(activityId, desc.Trim(), progressBar);
                        lastBytesRead = readBytes;
                    }
                    _guids.RegisterAction(a);
                    if (a is PrimitiveNodeAction na) {
                        _nodes.RegisterAction_NotThreadsafe(na);
                        _index.RegisterActionDuringStateLoad(transaction.Timestamp, na, throwOnBadStateFile, logCriticalError);
                    } else if (a is PrimitiveRelationAction ra) {
                        _relations.RegisterActionIfPossible(ra); // Simple validation omits fetching nodes to check types etc, would be slow and cause multiple open stream problems
                    } else throw new NotImplementedException();
                    _noPrimitiveActionsSinceLastStateSnaphot++;
                    if (a.Operation == PrimitiveOperation.Remove)
                        _noPrimitiveActionsInLogThatCanBeTruncated++;
                }
                _noTransactionsSinceLastStateSnaphot++;
                _wal.EnsureTimestamps(transaction.Timestamp);
            }
            if (PersistedIndexStore != null) {
                if (PersistedIndexStore.Timestamp > _wal.LastTimestamp) {
                    // will cause a delete of all state files and a new try of reload
                    throw new IndexReadException("Persited state file is older than log file and cannot be used. ", null);
                }
                PersistedIndexStore.Commit(_wal.LastTimestamp);
            }

        } finally {
            _wal.EndLogReader(logReader);
        }
        validateIndexesIfDebug();
        if (actionCount > 0) {
            LogInfo("   Read " + actionCount.To1000N() + " actions from log file in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        } else {
            LogInfo("   No actions read from log file.");
        }
        LogInfo(_noPrimitiveActionsInLogThatCanBeTruncated.To1000N() + " actions redundant in log file. ");
        LogInfo(_nodes.Count.To1000N() + " nodes in total");
        LogInfo(_relations.TotalCount().To1000N() + " relations in total");
    }
}
