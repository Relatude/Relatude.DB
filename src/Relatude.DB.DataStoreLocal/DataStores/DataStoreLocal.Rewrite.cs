using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System;
using System.Diagnostics;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    byte[][] threadSafeReadSegments(NodeSegment[] segments, out int diskReads) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _wal.ReadNodeSegments(segments, out diskReads);
        } finally {
            _lock.ExitReadLock();
        }
    }
    readonly object _isRewritingOrCopyingLock = new();
    bool _isRewritingOrCopying = false;
    public void RewriteStore(bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Store rewrite or copy already in progress. ");
            _isRewritingOrCopying = true;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.Rewriting, "Starting rewrite of log file", 0);
        try {
            rewriteStore(activityId, hotSwapToNewFile, newLogFileKey, destinationIO);
        } finally {
            DeRegisterActivity(activityId);
            lock (_isRewritingOrCopyingLock) _isRewritingOrCopying = false;
        }
    }
    void rewriteStore(long activityId, bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null) {
        // written to minimize locking while rewriting store
        validateDatabaseState();
        if (destinationIO == null) destinationIO = _io;
        if (string.IsNullOrEmpty(newLogFileKey)) throw new Exception("New log file name cannot be empty. ");
        if (newLogFileKey == _wal.FileKey) throw new Exception("New log file name cannot be the same as current. ");
        if (_rewriter != null) throw new Exception("Rewriter already initialized. ");
        var sw = Stopwatch.StartNew();
        UpdateActivity(activityId, "Flushing stream before rewrite lock", 1);
        FlushToDisk(true, activityId); // ensuring a flush before starting rewrite and lock to minized time for flush while locked...
        sw.Stop();
        UpdateActivity(activityId, $"Flush completed in {sw.ElapsedMilliseconds} ms", 1);
        LogInfo($"Rewrite first flush completed in {sw.ElapsedMilliseconds} ms");
        _lock.EnterWriteLock();
        try {
            if (LogRewriter.LogRewriterAlreadyInprogress(destinationIO)) {
                throw new Exception("Log rewriter already in progress. ");
            }
        } catch {
            _lock.ExitWriteLock();
            throw;
        }
        var initialNoPrimitiveActionsInLogThatCanBeTruncated = _noPrimitiveActionsInLogThatCanBeTruncated;
        try {
            sw.Restart();
            UpdateActivity(activityId, "Second flushing of stream inside rewrite lock", 2);
            FlushToDisk(true, activityId); // making sure every segment exists in _nodes ( through call back )
            sw.Stop();
            UpdateActivity(activityId, $"Second flush completed in {sw.ElapsedMilliseconds} ms", 2);
            LogInfo($"Rewrite second flush completed in {sw.ElapsedMilliseconds} ms");

            // starting rewrite of log file, requires all writes and reads to be blocked, making sure snaphot is consistent
            LogRewriter.CreateFlagFileToIndicateLogRewriterInprogress(destinationIO, newLogFileKey);
            UpdateActivity(activityId, "Starting rewrite of log file", 5);
            var snapshot = _nodes.Snapshot();
            var streamLen = _wal.FileSize;
            var whereOutSide = snapshot.Where(n => n.segment.AbsolutePosition + n.segment.Length > streamLen);
            if (whereOutSide.Any()) throw new Exception("Some node segments point outside log file. ");
            _rewriter = new LogRewriter(newLogFileKey, _definition, destinationIO, snapshot, _relations.Snapshot(), threadSafeReadSegments, updateNodeDataPositionInLogFile);
            UpdateActivity(activityId, "Starting rewrite of log file", 10);
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Error starting log rewrite. " , err);
        } finally {
            _lock.ExitWriteLock();
        }
        try {
            // no block, allowing simulatenous writes or reads while log is being rewritten
            _rewriter.Step1_RewriteLog_NoLockRequired((string desc, int prg) => UpdateActivity(activityId, desc, prg)); // (10%-80%)
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Error during log rewrite. ", err);
        }
        IOIndex.DeleteIfItExists(_fileKeys.StateFileKey);
        try {
            _lock.EnterWriteLock();
            UpdateActivity(activityId, "Finalizing rewrite", 90);  // (90%-100%)
            FlushToDisk(true, activityId); // ensuring all old and queued writes to old log file are flushed before finalizing rewrite ( so they do not write after hot swap )
            if (_rewriter == null) throw new Exception("Rewriter not initialized. ");
            try {
                _rewriter.Step2_HotSwap_RequiresWriteLock(_wal, hotSwapToNewFile);  // finalizes log rewrite, should be short, but blocks all writes and reads
                if (hotSwapToNewFile) {
                    _noPrimitiveActionsInLogThatCanBeTruncated -= initialNoPrimitiveActionsInLogThatCanBeTruncated;
                    // reset, since we have a new log file
                    SaveIndexStates(true, true); // needed to refresh state file with new log file
                    _index.WriteNewTimestampDueToRewriteHotswap(_wal.LastTimestamp, _wal.FileId);
                    PersistedIndexStore?.UpdateTimestampsDueToHotswap(_wal.LastTimestamp, _wal.FileId); // will update all sub indexes with new timestamp
                }
            } finally {
                _lock.ExitWriteLock();
            }
            LogRewriter.DeleteFlagFileToIndicateLogRewriterStart(destinationIO, _rewriter.FileKey);
            _rewriter = null;
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Error finalizing log rewrite. ", err);
        }
    }
}
