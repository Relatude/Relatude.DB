using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Indexes;
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
        // (polymorphic serialization with provenance stripped, so index-affecting property settings
        // count while moving a type between datamodel sources or renaming a source does not)
        var s = Datamodels.DatamodelJson.SerializeForChecksum(Datamodel);
        s += System.Text.Json.JsonSerializer.Serialize(_stateFileVersion);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.PersistedTextIndexEngine);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.PersistedValueIndexEngine);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.UsePersistedTextIndexesByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.UsePersistedValueIndexesByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.EnableTextIndexByDefault);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.PersistedValueIndexFolderPath);
        s += System.Text.Json.JsonSerializer.Serialize(_settings.EnableSemanticIndexByDefault);
        return s.GenerateHashGuid();
        //var g = s.GenerateGuid();
        //Log(SystemLogEntryType.Info, "Model hash: " + g);
        //File.WriteAllText("C:\\WAF_Temp\\" + g, s);
        //return g;
    }
    void saveMainState(long activityId) {
        var oldStateFileKeys = FileKeyUtility.State_GetAllFileKeys(IOIndex);
        var stateFileKey = FileKeyUtility.State_NextFileKey(oldStateFileKeys);
        UpdateActivity(activityId, "Opening " + stateFileKey.AsKeyString() + "...");
        IOIndex.DeleteFileIfItExists(stateFileKey); // safety, the state must never be appended to an existing file
        using (var stream = IOIndex.OpenAppend(stateFileKey)) {
            stream.WriteVerifiedInt(_stateFileVersion); // fileversion
            stream.WriteVerifiedLong(_wal.LastTimestamp);
            stream.WriteVerifiedLong(_wal.GetPositionAfterLastTransaction());
            stream.WriteGuid(getCheckSumForStateFileAndIndexes()); // must last checksum of dm
            stream.WriteVerifiedLong(_wal.FileSize);
            stream.WriteGuid(_wal.FileId); // must match log file
            UpdateActivity(activityId, "Saving guids");
            _guids.SaveState(stream);
            UpdateActivity(activityId, "Saving addresses");
            _addresses.SaveState(stream);
            UpdateActivity(activityId, "Saving segments");
            _nodes.SaveState(stream);
            UpdateActivity(activityId, "Saving native models");
            _nativeModelStore.SaveState(stream);
            UpdateActivity(activityId, "Saving relations");
            _relations.SaveState(stream);
            UpdateActivity(activityId, "Saving node type index");
            _definition.NodeTypeIndex.SaveState(stream);
            stream.WriteLong(_noPrimitiveActionsInLogThatCanBeTruncated);
            UpdateActivity(activityId, "Saving version chains");
            _wal.SaveChainState(stream); // secondary log version-chain heads; primary heads equal the node segments saved above
            // must be the very last bytes of the file: a numbered state file that does not end with
            // the marker was interrupted mid-write and is deleted at the next open
            stream.WriteGuid(FileKeyUtility.StateFileCompletionMarker);
        }
        // the previous state files (including a legacy unnumbered one) are deleted only after the
        // new file is completely written, so a shutdown mid-write cannot lose the last good state
        foreach (var oldKey in oldStateFileKeys) IOIndex.DeleteFileIfItExists(oldKey);
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
    // State and index snapshots are written to a NEW numbered file on every save, ending with the
    // completion marker, and the previous files are deleted only after the marker is written. A
    // numbered file that does not end with the marker was interrupted by a shutdown mid-write:
    // deleting it here, before anything reads state, makes the reads below fall back to the
    // previous complete file instead of failing and rebuilding everything from the start of the
    // log. Unnumbered files in the old naming format (state.bin, index.[id].bin) carry no marker
    // and cannot be trusted the same way; they are deleted too, so the first open after an upgrade
    // rebuilds from the log and saves fresh numbered files.
    void deleteIncompleteAndLegacyStateFiles() {
        foreach (var fileKey in FileKeyUtility.State_GetNumberedFileKeys(IOIndex).Concat(FileKeyUtility.Index_GetAllNumbered(IOIndex))) {
            if (FileKeyUtility.EndsWithStateFileCompletionMarker(IOIndex, fileKey)) continue;
            IOIndex.DeleteFileIfItExists(fileKey);
            LogInfo("Deleted incomplete state file " + fileKey.AsKeyString() + ", falling back to an older state if available. ");
        }
        string[][] legacyKeys = [FileKeyUtility.State_LegacyFileKey, .. FileKeyUtility.Index_GetAll(IOIndex).Where(k => !FileKeyUtility.Index_IsNumberedFileKey(k))];
        foreach (var fileKey in legacyKeys) {
            if (!IOIndex.Exists(fileKey)) continue;
            IOIndex.DeleteFileIfItExists(fileKey);
            LogInfo("Deleted state file " + fileKey.AsKeyString() + " in the old naming format; the state is rebuilt from the log. ");
        }
    }
    void readStateInner(bool throwOnErrors, Guid currentModelHash, long activityId) {

        // throwing IndexReadException will cause a delete of all state files and a new try of reload

        long stateFileTimestamp;
        long stateFilePositionOfLastTransactionSaved = 0;
        WalChainState? persistedChainState = null;
        _noPrimitiveActionsSinceLastStateSnapshot = 0;
        _noTransactionsSinceLastStateSnapshot = 0;
        _noPrimitiveActionsInLogThatCanBeTruncated = 0;
        var sw = Stopwatch.StartNew();
        var walFileSize = _wal.FileSize; // while it is open...
        _wal.Close();
        var walFileId = LogReader.ReadFileId(_wal.FileKey, _io);
        deleteIncompleteAndLegacyStateFiles(); // before any state or index file is opened
        LogInfo("Reading indexes:"); // progress 0-50%
        try {
            var lastIndexReadStart = sw.ElapsedMilliseconds;
            _index.ReadStateForMemoryIndexes((txt, prg) => {
                LogInfo(" - " + txt);
                UpdateActivity(activityId, "Reading index " + txt, prg / 2);
                setStartupProgressEstimate(1 + prg / 2);
            }, walFileId); // could introduce lazy loading of indexes later....
            Engines.BindToWalFile(walFileId, msg => LogInfo(msg));
        } catch (Exception err) {
            var errMsg = "Failed loading memory index states. " + err.Message;
            // A file another process is still holding is not a broken index. Letting it become a
            // StateFileReadException would delete every state and index file and replay the whole log
            // for nothing - and silently, since the rebuild succeeds. Let it out instead, so the
            // caller can wait and open again. See FileOpenRetry.
            if (FileOpenRetry.IsSharingViolation(err)) throw;
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
        var stateFileKey = FileKeyUtility.State_GetNewestFileKey(IOIndex); // incomplete files are already deleted above
        if (stateFileKey == null || IOIndex.DoesNotExistOrIsEmpty(stateFileKey)) { // no state file, so read from beginning of log file
            stateFileTimestamp = 0;
            LogInfo("No state file. ");
        } else { // read state, before reading rest from log file
            try {
                LogInfo("Reading state file " + stateFileKey.AsKeyString());
                UpdateActivity(activityId, "Reading state file", 0);
                setStartupProgressEstimate(50);
                byte[] stateBytes;
                using (var fileStream = IOIndex.OpenRead(stateFileKey, 0)) {
                    stateBytes = new byte[fileStream.Length];
                    var off = 0;
                    while (off < stateBytes.Length) {
                        var n = fileStream.ReadInto(stateBytes.AsSpan(off));
                        if (n <= 0) break;
                        off += n;
                    }
                }
                var stream = new BufferReader(stateBytes);
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
                UpdateActivity(activityId, "Reading addresses", 5);
                _addresses.ReadState(stream);
                UpdateActivity(activityId, "Reading segments", 5);
                _nodes.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(5 + p! * 0.03))); // 5-8%
                setStartupProgressEstimate(52);
                UpdateActivity(activityId, "Reading native models", 8); // 8%-10%
                setStartupProgressEstimate(53);
                _nativeModelStore.ReadState(stream);
                setStartupProgressEstimate(54);
                _relations.ReadState(stream, (d, p) => UpdateActivity(activityId, d, (int)(10 + p! * 0.05))); // 10-15%
                _definition.NodeTypeIndex.ReadState(stream);
                _noPrimitiveActionsInLogThatCanBeTruncated = stream.ReadLong();
                persistedChainState = WALFile.ReadChainState(stream);
                // written as the very last bytes of the file; the cleanup above only verifies the
                // file end, this confirms the body parsed up to exactly that point
                if (stream.ReadGuid() != FileKeyUtility.StateFileCompletionMarker)
                    throw new Exception("State file does not end with the completion marker. ");
                var bytesPerSecond = stream.Length / (Math.Max(sw.ElapsedMilliseconds, 1) / 1000D); // a small state file reads in under 1ms

                setStartupProgressEstimate(55);
                LogInfo("   State file read in " + sw.ElapsedMilliseconds.To1000N() + "ms - " + bytesPerSecond.ToByteString() + "/s");
                UpdateActivity(activityId, "State file read", 100);
            } catch (Exception err) {
                // as above: a held file must not be reported as an unusable state file
                if (FileOpenRetry.IsSharingViolation(err)) throw;
                var errMsg = "Failed loading index states. " + err.Message; // try to continue with loading from log file
                throw new StateFileReadException(errMsg, err);
            }
        }
        _wal.EnsureTimestamps(stateFileTimestamp); // from statefile, making sure next written transaction is not less than state file

        var nodeSnapshot = _nodes.Snapshot();
        var whereOutSide = nodeSnapshot.Where(n => n.segment.AbsolutePosition + n.segment.Length > walFileSize);
        if (whereOutSide.Any()) throw new StateFileReadException("Some node segments point outside log file. ", null);

        // figuring out from where to read the log file to reach latest state, building on current read state

        long readLogFileFrom = stateFilePositionOfLastTransactionSaved;
        if (readLogFileFrom > walFileSize) {
            throw new Exception("   Warning: State file position beyond log file size. Cannot use state file. ");
        }
        var oldestPersistedIndexTimestamp = _index.GetOldestPersistedTimestamp();
        if (stateFileTimestamp > oldestPersistedIndexTimestamp) {
            readLogFileFrom = 0; // need to read all to build indexes correctly ( this could be optimized later, to search from timestamp in log file )
        }

        int transactionCount = 0;
        int actionCount = 0;
        var readingFrom = stateFileTimestamp > 0 ? "UTC " + new DateTime(stateFileTimestamp, DateTimeKind.Utc) : "the beginning.";
        int positionInPercentage = (int)Math.Round(readLogFileFrom * 100d / (walFileSize + 1d));
        long bytesToRead = walFileSize - readLogFileFrom;
        LogInfo("Reading log file from " + positionInPercentage.ToString("0") + "% at " + readingFrom + " (" + bytesToRead.ToByteString() + " to read)");
        UpdateActivity(activityId, "Reading log file", 0);
        var lastProgress = 0D;
        var actionCountInTransaction = 0;
        long sizeOfCurrentTransaction;
        var lastBytesRead = 0D;
        Engines.BeginTransaction();
        var idValidator = new IdValidator(this, throwOnErrors);
        idValidator.Seed(nodeSnapshot.Select(n => n.nodeId)); // ids loaded from the state file snapshot; validation below only runs for actions newer than the snapshot
        using (var logReader = new LogReader(_wal.FileKey, _definition, _io, readLogFileFrom, stateFileTimestamp)) {
            LogInfo("   Log file size: " + logReader.FileSize.ToByteString());
            var noActionsNotCommittedInPersistedIndexes = 0;
            double progressBarFactor = 1 - readLogFileFrom / (double)logReader.FileSize;
            sw.Restart();
            while (logReader.ReadNextTransaction(out var transaction, throwOnErrors, logError, out sizeOfCurrentTransaction)) {
                transactionCount++;
                actionCountInTransaction = 0;
                var isTransactionRelevantForStateStores = transaction.Timestamp > stateFileTimestamp;
                var isTransactionRelevantForIndexes = transaction.Timestamp >= oldestPersistedIndexTimestamp;
                foreach (var a in transaction.ExecutedActions) {
                    // only validate actions that are applied to the state stores; older actions are already reflected
                    // in the seeded snapshot, so validating them again would produce false add/remove errors:
                    if (isTransactionRelevantForStateStores && !idValidator.Validate(a, transaction.Timestamp)) continue;
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
                            LogInfo(desc + (isTransactionRelevantForIndexes?" - i":"") + (isTransactionRelevantForStateStores?" - m":""), null, true);
                            var progressBar = progressBarFactor > 0 ? Math.Clamp((int)((estimatedTotalProgress - positionInPercentage) / progressBarFactor), 0, 100) : 100;
                            UpdateActivity(activityId, desc.Trim(), progressBar);
                            setStartupProgressEstimate(progressBar / 2 + 50, (int)remainingMs);
                            lastBytesRead = readBytes;
                        }
                        if (isTransactionRelevantForStateStores) {
                            _guids.RegisterAction(a);
                            if (a is PrimitiveNodeAction na) {
                                _nodes.RegisterAction_NotThreadsafe(na);
                                _definition.NodeTypeIndex.RegisterActionDuringStateLoad(na, throwOnErrors, logError);
                                _addresses.RegisterActionDuringStateLoad(na, throwOnErrors, logError);
                            } else if (a is PrimitiveRelationAction ra) {
                                _relations.RegisterActionIfPossible(ra); // Simple validation omits fetching nodes to check types etc, would be slow and cause multiple open stream problems
                            } else if (a is PrimitiveRelationReorderAction rra) {
                                _relations.RegisterActionIfPossible(rra);
                            } else throw new NotImplementedException();
                            _nativeModelStore.RegisterActionDuringStateLoad(a, throwOnErrors, logError);
                        }
                        if (isTransactionRelevantForIndexes) {
                            noActionsNotCommittedInPersistedIndexes++;
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
                if (Engines.Any && noActionsNotCommittedInPersistedIndexes > 30000) {
                    // commits, makes durable and reopens the replay transaction of every engine
                    // that is behind this position, bounding replay work lost to a startup crash
                    Engines.CheckpointDuringReplay(transaction.Timestamp);
                    noActionsNotCommittedInPersistedIndexes = 0;
                }
                if (isTransactionRelevantForStateStores) {
                    _noTransactionsSinceLastStateSnapshot++;
                }
                _wal.EnsureTimestamps(transaction.Timestamp);
            }
        }
        // Divergence check: an index claiming a timestamp newer than anything the log contains holds
        // transactions the durable log lost (e.g. a crash dropped a queued WAL batch after the indexes
        // had committed). Replay cannot repair that � the phantom entries would survive � and the
        // commit below would overwrite the too-new timestamp and mask the only evidence, so the check
        // must run here, on the first startup after the damage. _wal.LastTimestamp is at this point
        // max(state file, replayed transactions) = the newest timestamp the durable log covers.
        var lastLogTimestamp = _wal.LastTimestamp;
        string? aheadIndex = null;
        var aheadEngine = Engines.FindEngineAheadOfLog(lastLogTimestamp);
        if (aheadEngine != null) {
            aheadIndex = "The index engine \"" + aheadEngine.Name + "\"";
        } else {
            foreach (var indx in _definition.GetAllIndexes()) {
                if (indx.PersistedTimestamp > lastLogTimestamp) {
                    aheadIndex = "Index \"" + indx.FriendlyName + "\"";
                    break;
                }
            }
        }
        if (aheadIndex != null) {
            logError(aheadIndex + " contains transactions that are missing from the log file. "
                + "This indicates that acknowledged writes were lost in an earlier unclean shutdown. "
                + "All indexes will be reset and rebuilt from the log file. ");
            Engines.RollbackTransaction(); // the replay transaction cannot stay open across the reset and reload
            throw new StateFileReadException(aheadIndex + " is ahead of the log file (its timestamp is newer than the last log timestamp). ", null);
        }
        Engines.CommitTransaction(_wal.LastTimestamp);
        Engines.MakeDurable(_wal.LastTimestamp); // replay work must not stay pending until the first background flush
        // node segments are final here (state + replay, in write order), so the version-chain heads
        // can be established; must happen while the log streams are still closed:
        _wal.SeedChainHeadsWhileClosed(persistedChainState, _nodes.Snapshot(), msg => LogInfo(msg), logError);
        _wal.OpenForAppending(); // read for appending again
        validateStateInfoIfDebug();
        foreach (var e in idValidator.GetErrors()) logError(e);
        if (actionCount > 0) LogInfo("   Read " + actionCount.To1000N() + " actions from log file in " + sw.ElapsedMilliseconds.To1000N() + "ms. ", null, false);
        else LogInfo("   No actions read from log file.", null, true);
        if (stateFileTimestamp > 0) { // persist indexes that are new and never persisted
            foreach (var indx in _definition.GetAllIndexes()) {
                if (indx.PersistedTimestamp == 0) { // this indicates a new index, so persist it
                    LogInfo("Persisting new index \"" + indx.FriendlyName + "\" ");
                    indx.SaveStateForMemoryIndexes(_wal.LastTimestamp, _wal.FileId);
                }
            }
        }

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
    public void Seed(IEnumerable<int> existingIds) {
        foreach (var id in existingIds) ids.Add(id);
    }
    public List<string> errors = [];
    public IEnumerable<string> GetErrors() {
        if (maxErrorCount <= errorCount) {
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