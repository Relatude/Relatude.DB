using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System;
using System.Diagnostics;
using System.Transactions;
using static System.Runtime.InteropServices.JavaScript.JSType;
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
        _noPrimitiveActionsSinceLastStateSnapshot = 0;
        _noTransactionsSinceLastStateSnapshot = 0;
    }
    void saveIndexesStates(long activityId) {
        UpdateActivity(activityId, "Saving indexes");
        _index.SaveStateForMemoryIndexes(_wal.LastTimestamp, _wal.FileId, (txt, prg) => {
            UpdateActivity(activityId, txt, prg);
        });
    }
    void readState(bool throwOnErrors, Guid currentModelHash, long parentActivityId) {
        var activityId = RegisterActvity(parentActivityId, DataStoreActivityCategory.Opening, "Reading state");
        try {
            readStateInner(throwOnErrors, currentModelHash, activityId);
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    void readStateInner(bool throwOnErrors, Guid currentModelHash, long activityId) {

        // throwing IndexReadException will cause a delete of all state files and a new try of reload

        long stateFileTimestamp;
        long stateFilePositionOfLastTransactionSaved = 0;
        _noPrimitiveActionsSinceLastStateSnapshot = 0;
        _noTransactionsSinceLastStateSnapshot = 0;
        _noPrimitiveActionsInLogThatCanBeTruncated = 0;
        var sw = Stopwatch.StartNew();
        var walFileSize = _wal.FileSize; // while it is open...
        _wal.Close();
        var walFileId = LogReader.ReadFileId(_wal.FileKey, _io);
        LogInfo("Reading indexes:"); // progress 0-50%
        try {
            var lastIndexReadStart = sw.ElapsedMilliseconds;
            _index.ReadStateForMemoryIndexes((txt, prg) => {
                LogInfo(" - " + txt);
                UpdateActivity(activityId, "Reading index " + txt, prg / 2);
                setStartupProgressEstimate(1 + prg / 2);
            }, walFileId); // could introduce lazy loading of indexes later....
            if (PersistedIndexStore != null) {
                if (PersistedIndexStore.WalFileId == Guid.Empty) {
                    PersistedIndexStore.SetWalFileId(walFileId);
                    LogInfo(" - Persisted indexes initialized with log file id.");
                } else if (PersistedIndexStore.WalFileId != walFileId) {
                    PersistedIndexStore.ResetAll();
                    LogInfo(" - Persisted indexes reset, log file id different.");
                }
            }
        } catch (Exception err) {
            var errMsg = "Failed loading memory index states. " + err.Message;
            if (err.CausedByOutOfMemory()) {
                // do not try to continue if out of memory, as it will delete state file 
                // throwing this will abort loading process ( as the open method will rethrow it, and not try again after deleting state file )
                throw new Exception(errMsg, err);
            } else {
                // try to continue with loading from log file
                // throwing IndexReadException will cause a delete of all state files and a new try of reload in the open method
                throw new StateFileReadException(errMsg, err);
            }
        }
        // reading statefile progress 50-55%
        if (IOIndex.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey)) { // no state file, so read from beginning of log file
            stateFileTimestamp = 0;
            LogInfo("Index state file empty. ");
        } else { // read state, before reading rest from log file
            try {
                LogInfo("Reading state file");
                UpdateActivity(activityId, "Reading state file", 0);
                setStartupProgressEstimate(50);
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
                setStartupProgressEstimate(52);
                UpdateActivity(activityId, "Reading native models", 8); // 8%-10%
                setStartupProgressEstimate(53);
                _nativeModelStore.ReadState(stream);
                setStartupProgressEstimate(54);
                _relations.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(10 + p! * 0.05))); // 10-15%
                _definition.NodeTypeIndex.ReadState(stream);
                _noPrimitiveActionsInLogThatCanBeTruncated = stream.ReadLong();
                var bytesPerSecond = stream.Length / (sw.ElapsedMilliseconds / 1000D);
                setStartupProgressEstimate(55);
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
        var readingFrom = stateFileTimestamp > 0 ? "UTC " + new DateTime(stateFileTimestamp, DateTimeKind.Utc) : "the beginning.";
        int positionInPercentage = (int)Math.Round(readLogFileFom * 100d / (walFileSize + 1d));
        long bytesToRead = walFileSize - readLogFileFom;
        LogInfo("Reading log file from " + positionInPercentage.ToString("0") + "% at " + readingFrom + " (" + bytesToRead.ToByteString() + " to read)");
        UpdateActivity(activityId, "Reading log file", 0);
        var lastProgress = 0D;
        var actionCountInTransaction = 0;
        long sizeOfCurrentTransaction;
        var lastBytesRead = 0D;
        PersistedIndexStore?.StartTransaction();
        var idValidator = new IdValidator(this, throwOnErrors);
        using (var logReader = new LogReader(_wal.FileKey, _definition, _io, readLogFileFom, stateFileTimestamp)) {
            LogInfo("   Log file size: " + logReader.FileSize.ToByteString());
            double progressBarFactor = (1 - readLogFileFom / logReader.FileSize);
            sw.Restart();
            while (logReader.ReadNextTransaction(out var transaction, throwOnErrors, logError, out sizeOfCurrentTransaction)) {
                transactionCount++;
                actionCountInTransaction = 0;
                var isTransactionRelevantForStateStores = transaction.Timestamp > stateFileTimestamp;
                var isTransactionRelevantForIndexes = transaction.Timestamp >= oldestPersistedIndexTimestamp;
                foreach (var a in transaction.ExecutedActions) {
                    if (!idValidator.Validate(a, transaction.Timestamp)) continue;
                    try {
                        if (actionCount % 100 == 0 && (sw.ElapsedMilliseconds - lastProgress > 200)) {
                            var remainingInTrans = 1D - (double)actionCountInTransaction / transaction.ExecutedActions.Count;
                            var estimatedByteProgressInTransaction = sizeOfCurrentTransaction * remainingInTrans;
                            var readBytes = logReader.Position - estimatedByteProgressInTransaction;
                            var totalBytes = logReader.FileSize;
                            var remainingMs = readBytes > 0 ? (totalBytes - readBytes) * (sw.ElapsedMilliseconds / readBytes) : 0;
                            var remaining = (remainingMs > 0 && sw.ElapsedMilliseconds > 10000) ? (" - " + TimeSpan.FromMilliseconds(remainingMs).ToTimeString()) : "";
                            var estimatedTotalProgress = readBytes * 100D / totalBytes;
                            var deltaBytes = readBytes - lastBytesRead;
                            var deltaSeconds = sw.ElapsedMilliseconds - lastProgress;
                            var bytesPerSecond = deltaBytes / (deltaSeconds / 1000D);
                            lastProgress = (int)sw.ElapsedMilliseconds;
                            var desc = "   - " + (int)estimatedTotalProgress + "% - " + readBytes.ToByteString() + " - " + bytesPerSecond.ToByteString() + "/s" + " - " + actionCount.To1000N() + " actions" + remaining;
                            LogInfo(desc, null, true);
                            var progressBar = progressBarFactor > 0 ? (int)(estimatedTotalProgress / progressBarFactor) : 100;
                            UpdateActivity(activityId, desc.Trim(), progressBar);
                            setStartupProgressEstimate(progressBar / 2 + 50, (int)remainingMs);
                            lastBytesRead = readBytes;
                        }
                        if (isTransactionRelevantForStateStores) {
                            _guids.RegisterAction(a);
                            if (a is PrimitiveNodeAction na) {
                                _nodes.RegisterAction_NotThreadsafe(na);
                                _definition.NodeTypeIndex.RegisterActionDuringStateLoad(na, throwOnErrors, logError);
                            } else if (a is PrimitiveRelationAction ra) {
                                _relations.RegisterActionIfPossible(ra); // Simple validation omits fetching nodes to check types etc, would be slow and cause multiple open stream problems
                            } else throw new NotImplementedException();
                            _nativeModelStore.RegisterActionDuringStateLoad(a, throwOnErrors, logError);
                        }
                        if (isTransactionRelevantForIndexes) {
                            _index.RegisterActionDuringStateLoad(transaction.Timestamp, a, throwOnErrors, logError);
                        }
                        if (isTransactionRelevantForStateStores) {
                            _noPrimitiveActionsSinceLastStateSnapshot++;
                            if (a.Operation == PrimitiveOperation.Remove) _noPrimitiveActionsInLogThatCanBeTruncated++;
                        }
                        actionCount++;
                        actionCountInTransaction++;
                    } catch (Exception err) {
                        if (throwOnErrors) {
                            throw new Exception("Error processing action in transaction at timestamp " + transaction.Timestamp + ". " + err.Message, err);
                        } else {
                            logError("Error processing action in transaction at timestamp " + transaction.Timestamp + ". ", err);
                        }
                    }
                }
                if (isTransactionRelevantForStateStores) {
                    _noTransactionsSinceLastStateSnapshot++;
                }
                _wal.EnsureTimestamps(transaction.Timestamp);
            }
        }
        PersistedIndexStore?.CommitTransaction(_wal.LastTimestamp);
        _wal.OpenForAppending(); // read for appending again
        validateStateInfoIfDebug();
        foreach (var e in idValidator.GetErrors()) logError(e);
        if (actionCount > 0) LogInfo("   Read " + actionCount.To1000N() + " actions from log file in " + sw.ElapsedMilliseconds.To1000N() + "ms. ", null, false);
        else LogInfo("   No actions read from log file.", null, true);
        //if (_noTransactionsSinceLastStateSnapshot == 0) { // persist indexes that are new and never persisted
        //    foreach (var indx in _definition.GetAllIndexes()) {
        //        if (indx.PersistedTimestamp == 0) { // this indicates a new index, so persist it
        //            LogInfo("Persisting new index '" + indx.FriendlyName + ". ");
        //            // indx.SaveStateForMemoryIndexes(_wal.LastTimestamp, _wal.FileId);
        //        }
        //    }
        //}

        LogInfo(_noPrimitiveActionsInLogThatCanBeTruncated.To1000N() + " actions redundant in log file. ");
        LogInfo(_nodes.Count.To1000N() + " nodes in total");
        LogInfo(_relations.TotalCount().To1000N() + " relations in total");
        LogInfo(_nativeModelStore.CountUsers.To1000N() + " system users");
        LogInfo(_nativeModelStore.CountUserGroups.To1000N() + " user groups");
        LogInfo(_nativeModelStore.CountCultures.To1000N() + " cultures");
        LogInfo(_nativeModelStore.CountCollections.To1000N() + " collections");
    }
    void validateStateInfoIfDebug() {
        return;
        //#if DEBUG
        //        // temporary code, to be deleted later on;
        //        // testing indexes
        //        foreach (var n in _nodes.Snapshot()) {
        //            var uid = n.nodeId;
        //            var node = _nodes.Get(uid);
        //            if (_definition.GetTypeOfNode(uid) != node.NodeType) {
        //                throw new Exception("Node type mismatch. ");
        //            }
        //            if (_guids.GetId(node.Id) != uid) {
        //                throw new Exception("Guid mismatch. ");
        //            }
        //            if (_guids.GetGuid(uid) != node.Id) {
        //                throw new Exception("Guid mismatch. ");
        //            }
        //        }

        //        // validating all relations, to ensure that all nodes exists, this step is not needed for normal operation, but is needed for recovery
        //        foreach (var r in _definition.Relations.Values) {
        //            foreach (var v in r.Values) {
        //                if (!_nodes.Contains(v.Target)) {
        //                    throw new Exception("Relation to node ID : " + v + " refers to a non-existing node. RelationID " + r.Id);
        //                    // r.DeleteIfReferenced(id); // fix
        //                }
        //                if (!_nodes.Contains(v.Source)) {
        //                    throw new Exception("Relation to node ID : " + v + " refers to a non-existing node. RelationID " + r.Id);
        //                    // r.DeleteIfReferenced(id); // fix
        //                }
        //            }
        //        }
        //#endif
    }

}

class IdValidator(DataStoreLocal store, bool throwOnErrors) {
    // simple validator to check that node ids are not added or removed multiple times
    HashSet<int> ids = [];
    int maxErrorCount = 256;
    int errorCount = 0;
    public List<string> errors = [];
    public IEnumerable<string> GetErrors() {
        if(maxErrorCount <= errorCount) {
            yield return errorCount + " ID errors found! Listing first " + maxErrorCount + ":";
        }
        foreach (var e in errors) {
            yield return e;
        }
    }
    string typeName(PrimitiveNodeAction pna) => store._definition.NodeTypes.TryGetValue(pna.Node.NodeType, out var t) ? t.Model.FullName : "Unknown type: " + pna.Node.NodeType;
    string date(long timestamp) => new DateTime(timestamp, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.fff") + " UTC";
    public bool Validate(PrimitiveActionBase a, long timestamp) {
        if (a is PrimitiveNodeAction pna) {
            if (pna.Operation == PrimitiveOperation.Add && ids.Add(pna.Node.__Id) == false) {
                errorCount++;
                if (errorCount < maxErrorCount) errors.Add("Node " + pna.Node.__Id + " (" + typeName(pna) + ") added twice at " + date(timestamp));
                if (throwOnErrors) throw new Exception(errors.First());
                return false;
            } else if (pna.Operation == PrimitiveOperation.Remove && ids.Remove(pna.Node.__Id) == false) {
                errorCount++;
                if (errorCount < maxErrorCount) errors.Add("Node " + pna.Node.__Id + " (" + typeName(pna) + ") removed twice at " + date(timestamp));
                if (throwOnErrors) throw new Exception(errors.First());
                return false;
            }
        }
        return true;
    }
}