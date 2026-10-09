using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
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
    public void RewriteStore(bool hotSwapToNewFile, string[] newLogFileKey, IIOProvider? destinationIO = null) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Store rewrite or copy already in progress. ");
            _isRewritingOrCopying = true;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.Rewriting, "Rewriting to " + newLogFileKey.AsKeyString(), 0);
        var subActivityId = RegisterChildActvity(activityId, DataStoreActivityCategory.Rewriting);
        try {
            rewriteStore(subActivityId, hotSwapToNewFile, newLogFileKey, destinationIO);
        } finally {
            DeRegisterActivity(subActivityId);
            DeRegisterActivity(activityId);
            lock (_isRewritingOrCopyingLock) _isRewritingOrCopying = false;
        }
    }
    void rewriteStore(long activityId, bool hotSwapToNewFile, string[] newLogFileKey, IIOProvider? destinationIO = null) {
        // written to minimize locking while rewriting store
        validateDatabaseState();
        // a hot swap replaces the log file the revert window's position and file id refer to, and
        // rebinds every engine to the new file � rolling back would then be impossible:
        if (hotSwapToNewFile && RevertWindowIsActive)
            throw new Exception("Log rewrite is not possible while a revert window is active. Commit or roll back the revert window first. ");
        if (destinationIO == null) destinationIO = _io;
        if (newLogFileKey is not { Length: > 0 }) throw new Exception("New log file name cannot be empty. ");
        if (newLogFileKey.IsSameKey(_wal.FileKey)) throw new Exception("New log file name cannot be the same as current. ");
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
                // Only one rewrite or copy runs at a time (see RewriteStore), so in a backup storage of its
                // own the flag is one a failed backup could not remove - the storage was failing at the
                // time. It goes, with the half written file it names, rather than refusing every backup
                // until the next start. In the database's own storage the flag stays a refusal.
                if (hotSwapToNewFile || destinationIO == _io) throw new Exception("Log rewriter already in progress. ");
                LogInfo("Removing what a failed backup left in the backup storage. ");
                LogRewriter.CleanupOldPartiallyCompletedLogRewriteIfAny(destinationIO, deleteStateFiles: false);
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
            UpdateActivity(activityId, "Starting rewrite of log file", 5);
            var snapshot = _nodes.Snapshot();
            var streamLen = _wal.FileSize;
            var whereOutSide = snapshot.Where(n => n.segment.AbsolutePosition + n.segment.Length > streamLen);
            if (whereOutSide.Any()) throw new Exception("Some node segments point outside log file. ");
            var relations = _relations.Snapshot();
            try {
                LogRewriter.CreateFlagFileToIndicateLogRewriterInprogress(destinationIO, newLogFileKey);
                _rewriter = new LogRewriter(newLogFileKey, _definition, destinationIO, snapshot, relations, threadSafeReadSegments);
            } catch (Exception err) when (!hotSwapToNewFile) {
                // nothing of the store has been touched yet: only the storage the copy goes to failed, and
                // what it managed to write there goes (or is cleaned up by the next backup or start)
                try {
                    destinationIO.DeleteFileIfItExists(newLogFileKey);
                    if (LogRewriter.LogRewriterAlreadyInprogress(destinationIO)) LogRewriter.DeleteFlagFileToIndicateLogRewriterStart(destinationIO, newLogFileKey);
                } catch { }
                throw new LogCopyException("Error starting the copy of the log file. ", err);
            }
            UpdateActivity(activityId, "Starting rewrite of log file", 10);
        } catch (LogCopyException err) {
            // a backup whose storage failed fails the backup, not the database
            logError("Error starting log rewrite. ", err, null, false);
            throw;
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Error starting log rewrite. " , err);
        } finally {
            _lock.ExitWriteLock();
        }
        try {
            // no block, allowing simulatenous writes or reads while log is being rewritten
            _rewriter.Step1_RewriteLog_NoLockRequired((string desc, int prg) => UpdateActivity(activityId, desc, prg)); // (10%-80%)
        } catch (Exception err) {
            logError("Error during log rewrite. ", err, null, false);
            abandonFailedRewrite();
            throw new Exception("Error during log rewrite. ", err);
        }
        FileKeyUtility.State_DeleteAll(IOIndex);
        try {
            _lock.EnterWriteLock();
            try {
                UpdateActivity(activityId, "Finalizing rewrite", 90);  // (90%-100%)
                FlushToDisk(true, activityId); // ensuring all old and queued writes to old log file are flushed before finalizing rewrite ( so they do not write after hot swap )
                if (_rewriter == null) throw new Exception("Rewriter not initialized. ");
                _rewriter.Step2_HotSwap_RequiresWriteLock(_wal, hotSwapToNewFile);  // finalizes log rewrite, should be short, but blocks all writes and reads
                if (hotSwapToNewFile) { // every node now lives in the new file
                    var stateEngine = _stateStore.Engine;
                    stateEngine?.BeginTransaction();
                    _nodes.ReplaceAllSegments(_rewriter.NewSegments);
                    stateEngine?.CommitTransaction(_wal.LastTimestamp);
                }
                // the flag file must be deleted while still holding the write lock, before any new transaction can be
                // written to the new log file. If deleted after the lock is released, a crash in between would cause
                // the startup cleanup to delete the new log file, which is now the live log with acknowledged transactions:
                LogRewriter.DeleteFlagFileToIndicateLogRewriterStart(destinationIO, _rewriter.FileKey);
                if (hotSwapToNewFile) {
                    _noPrimitiveActionsInLogThatCanBeTruncated -= initialNoPrimitiveActionsInLogThatCanBeTruncated;
                    // reset, since we have a new log file
                    SaveIndexStates(true, true); // needed to refresh state file with new log file
                    _index.WriteNewTimestampDueToRewriteHotswapJustAfterSaveState(_wal.LastTimestamp, _wal.FileId);
                    Engines.SetWalFileIdAndTimestamp(_wal.LastTimestamp, _wal.FileId); // will update all sub indexes with new timestamp
                }
                // must be cleared while still holding the write lock: after the swap _rewriter.FileKey is the LIVE
                // log file, and a concurrent CancelRunningRewriteIfAny seeing a non-null _rewriter would delete it
                _rewriter = null;
            } finally {
                _lock.ExitWriteLock();
            }
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Error finalizing log rewrite. ", err);
        }
    }
    // A rewrite or backup that failed while writing (a storage that went away, a full disk) must not
    // stay registered: every later one would be refused as "already initialized", and every
    // transaction would keep being collected for it. Its half written file and flag go with it.
    void abandonFailedRewrite() {
        _lock.EnterWriteLock();
        try {
            var rewriter = _rewriter;
            if (rewriter == null) return; // cancelled, which cleaned up already
            _rewriter = null;
            try {
                rewriter.Cancel();
            } catch (Exception err) {
                logError("Could not remove the files of the failed log rewrite. ", err, null, false);
            }
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public string? CancelRunningRewriteIfAny() {
        lock (_isRewritingOrCopyingLock) {
            if (!_isRewritingOrCopying) return null; // cannot cancel if not in progress
        }
        _lock.EnterWriteLock();
        try {
            if (_rewriter != null) {
                var fileKey = _rewriter.FileKey.AsKeyString();
                _rewriter.Cancel();
                _rewriter = null;
                return fileKey;
            }
            return null;
        } finally {
            _lock.ExitWriteLock();
        }
    }
}
