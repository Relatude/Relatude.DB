using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Stores;

internal delegate void RegisterNodeSegmentCallbackFunc(int id, NodeSegment seg);
/// <summary>
// WAL (Write Ahead Log) store, used to store all changes to the database
// Threadsafe read operations to support multiple read queries at the same time
// Write operations are NOT threadsafe, including FlushDisk, but store is designed to be used with a single writer thread
/// </summary>
internal class WALFile : IDisposable {
    // File format is designed to detect and repair from a partially completed write
    // and also make it possible to extract data if file is corrupted.
    // It uses markers to indicate start and end of log file, if an corruption is found, the reader skips to the next transaction start marker.
    // Version 1001 added a 20 byte version-chain header to every node action, right before the node
    // data: [transaction timestamp: 8][position of previous add of same node: 8][its length: 4].
    // Positions are per file, so each log file (primary and secondary) carries its own internally
    // consistent chain. Files with version 1000 keep getting 1000-format records appended, so a file
    // never mixes formats; the primary upgrades at the next log rewrite, the secondary when it is
    // reset or recreated.
    public readonly static long _logVersioNumber = 1001; // file format version written to new files
    public readonly static long _logVersionNumberV1000 = 1000; // legacy format without version-chain headers
    internal const int VersionHeaderSize = 20; // v1001 node actions: [timestamp:8][prevAddPos:8][prevAddLen:4] right before the node data
    public readonly static Guid _logStartMarker = new Guid("01114d5b-d268-4ece-a498-2b7961c1a3f8"); // just a unique number
    public readonly static Guid _transactionStartMarker = new Guid("a02520c1-60aa-426b-b002-76c76e71a8be"); // just a unique number
    public readonly static Guid _transactionEndMarker = new Guid("8ad9629a-dd38-4641-94f4-c7e10c1e2eea"); // just a unique number
    public readonly static Guid _actionMarker = new Guid("52a6fb71-979e-4184-94c7-93724a5278d8"); // just a unique number
    readonly static Guid _chainStateMarker = new Guid("b7c9d1f3-4a52-4b1e-9e0d-6c2f8a1d5e73"); // frames the version-chain section of the state file
    const long posOfFirstTransaction = 64; // start of first transaction
    internal Guid FileId { get; private set; } // a unique id for the file, created at start up and links til file to a statefile
    internal string[] FileKey { get; private set; }
    readonly Definition _definition;
    readonly RegisterNodeSegmentCallbackFunc _registerAndConfrimeNodeWrite; // callback to store to register byte position a node in log file
    readonly LogQueue _workQueue; // queue for write operations, to make sure they are written in bacthes for better performance
    IIOProvider _io;
    IIOProvider? _ioSecondary;
    IAppendStream _appendStream;
    IAppendStream? _secondaryAppendStream;
    string[]? _secondaryFileKey;
    long _lastTimestampID;
    long _formatVersion; // format version of the primary log file
    long _secondaryFormatVersion; // format version of the secondary log file
    Guid _secondaryFileId;
    // version-chain heads: the node data position of the last WRITTEN add per node, per file, in
    // write order. The writer links each new node record to the entry here, giving each file a
    // backward chain of the node's versions. Kept by the (single) log writer, read by state saves
    // and version walks on other threads, hence the lock. NodeStore's segments cannot serve this
    // purpose: they are reset at execute time, before the write happens.
    readonly object _chainLock = new();
    Dictionary<int, NodeSegment> _chainHeads = [];
    readonly Dictionary<int, NodeSegment> _secondaryChainHeads = [];
    public WALFile(string[] fileKey, Definition definition, IIOProvider io, RegisterNodeSegmentCallbackFunc confirmWrite, IIOProvider? ioSecondary, string[]? secondaryFileKey) {
        FileKey = fileKey;
        _io = io;
        _definition = definition;
        _registerAndConfrimeNodeWrite = confirmWrite;
        _workQueue = new(write);
        _lastTimestampID = 0;
        _ioSecondary = ioSecondary;
        _secondaryFileKey = secondaryFileKey;
        _appendStream = getWriteStream(_io, FileKey, false, out _formatVersion); // open primary log file, to lock file, even though it may not be used right away
    }
    public void OpenForAppending() {
        _appendStream = getWriteStream(_io, FileKey, false, out _formatVersion);
        if (_ioSecondary != null && _secondaryFileKey != null) {
            _secondaryAppendStream = getWriteStream(_ioSecondary, _secondaryFileKey, true, out _secondaryFormatVersion);
        }
    }
    public void Close() {
        _appendStream.Dispose();
        _secondaryAppendStream?.Dispose();
    }
    static long getFirstTimestampIfAny(IAppendStream appendStream) {
        if (appendStream.Length >= posOfFirstTransaction + 8) { // full 8 byte timestamp must be present ( a torn write may leave a shorter file )
            return appendStream.GetLong(posOfFirstTransaction);
        } else {
            return 0;
        }
    }
    public long FirstTimestamp { get; private set; }
    public long LastTimestamp { get => _lastTimestampID; }
    public long FileSize { // requires file to be opened
        get {
            return _appendStream.Length;
        }
    }
    IAppendStream getWriteStream(IIOProvider io, string[] fileKey, bool isSecondaryLog, out long formatVersion) {
        IAppendStream? s = null;
        try {
            s = io.OpenAppend(fileKey);
            if (s.Length == 0) {
                s.WriteMarker(_logStartMarker);// from pos 0
                formatVersion = _logVersioNumber;
                s.WriteVerifiedLong(formatVersion); // from pos 16
                if (!isSecondaryLog) {
                    FileId = Guid.NewGuid(); // do not create new file id for secondary log
                } else {
                    _secondaryFileId = FileId;
                }
                s.WriteGuid(FileId); // from pos 32
                // now at pos 48
                FirstTimestamp = 0;
            } else {
                if (s.GetGuid(0) != _logStartMarker) throw new IOException("Unable to open log file. Data is not compatible. ");
                var version = s.GetVerifiedLong(16);
                if (version != _logVersioNumber && version != _logVersionNumberV1000) throw new IOException("Incompatible log file format version number. Expected version " + _logVersioNumber + " or " + _logVersionNumberV1000 + " but found " + version + " .");
                formatVersion = version;
                var readFileId = s.GetGuid(32);
                if (!isSecondaryLog) {
                    if (FileId == Guid.Empty) FileId = readFileId;
                    else if (readFileId != FileId) throw new Exception("FileId mismatch. ");
                    FirstTimestamp = getFirstTimestampIfAny(s);
                } else {
                    _secondaryFileId = readFileId;
                }
            }
            return s;
        } catch {
            s?.Dispose();
            throw;
        }
    }
    long write(ExecutedPrimitiveTransaction[] transactions, Action<string, int>? progress, int actionCount, int transactionCount) {
        Action<string, int>? progress1 = progress != null ? (_ioSecondary != null ? (msg, perc) => progress("Primary: " + msg, perc / 2) : progress) : null;
        var written = writeStatic(transactions, _appendStream, _formatVersion, _definition.Datamodel, _registerAndConfrimeNodeWrite,
            _formatVersion >= _logVersioNumber ? _chainHeads : null, _chainLock, progress1, actionCount, transactionCount);
        if (_ioSecondary != null) {
            Action<string, int>? progress2 = progress != null ? (msg, perc) => progress("Secondary: " + msg, 50 + (perc / 2)) : null;
            if (_secondaryAppendStream == null) _secondaryAppendStream = getWriteStream(_ioSecondary, _secondaryFileKey!, true, out _secondaryFormatVersion);
            writeStatic(transactions, _secondaryAppendStream, _secondaryFormatVersion, _definition.Datamodel, null,
                _secondaryFormatVersion >= _logVersioNumber ? _secondaryChainHeads : null, _chainLock, progress2, actionCount, transactionCount);
        }
        return written;
    }
    static long writeStatic(ExecutedPrimitiveTransaction[] transactions, IAppendStream stream, long formatVersion, Datamodel datamodel, RegisterNodeSegmentCallbackFunc? regCallback,
        Dictionary<int, NodeSegment>? chainHeads, object chainLock, Action<string, int>? progress, int actionCount, int transactionCount) {
        long bytesStartPos = stream.Length;
        if (progress != null) progress("Flushing " + transactionCount + " transactions and " + actionCount + " actions", 0);
        int transactionsWritten = 0;
        int actionsWritten = 0;
        foreach (var transaction in transactions) {
            transactionsWritten++;
            stream.WriteMarker(_transactionStartMarker);  // marking end of a new transaction, making it possible to separate each transaction in a corrupted file
            stream.WriteLong(transaction.Timestamp);
            stream.WriteVerifiedInt(transaction.ExecutedActions.Count);
            // an update is written as remove(old) + add(new) within one transaction, and the add must
            // chain to the version the remove ended; entries removed here stay reachable until the
            // transaction ends, so only a delete that stands at transaction end breaks the chain:
            Dictionary<int, NodeSegment>? removedInTransaction = null;
            foreach (var action in transaction.ExecutedActions) {
                actionsWritten++;
                stream.WriteMarker(_actionMarker);
                var ms = new MemoryStream();
                var na = action as PrimitiveNodeAction;
                NodeSegment previousVersion = default;
                if (na != null && chainHeads != null) {
                    lock (chainLock) {
                        if (!chainHeads.TryGetValue(na.Node.__Id, out previousVersion) && removedInTransaction != null)
                            removedInTransaction.TryGetValue(na.Node.__Id, out previousVersion);
                    }
                }
                PToBytes.ActionBase(action, datamodel, ms, formatVersion, transaction.Timestamp, previousVersion, out long nodeSegmentRelativeOffset, out int nodeSegmentLength);
                var actionData = ms.ToArray();
                var segmentStreamPosition = stream.Length + 8; // add 8 as first byte is for array length, we only want exact position of node data bytes
                stream.WriteByteArray(actionData);
                if (actionsWritten % 93 == 0 && progress != null) // update progress every 93 actions
                    progress("Flushing " + transactionsWritten + " of " + transactionCount + " transactions and " + actionsWritten + " of " + actionCount + " actions", (int)((transactionsWritten / (double)transactionCount) * 100));
                stream.WriteUInt(actionData.GetChecksum());
                if (na != null) {
                    var absolutePosition = segmentStreamPosition + nodeSegmentRelativeOffset;
                    if (absolutePosition == 0) throw new Exception();
                    var segment = new NodeSegment(absolutePosition, nodeSegmentLength);
                    if (chainHeads != null) {
                        lock (chainLock) { // chains link adds only
                            if (na.Operation == PrimitiveOperation.Add) {
                                chainHeads[na.Node.__Id] = segment;
                                removedInTransaction?.Remove(na.Node.__Id);
                            } else if (chainHeads.TryGetValue(na.Node.__Id, out var lastAdd)) {
                                (removedInTransaction ??= [])[na.Node.__Id] = lastAdd;
                                chainHeads.Remove(na.Node.__Id);
                            }
                        }
                    }
                    if (regCallback != null) regCallback(na.Node.__Id, segment);
                }
            }
            stream.WriteMarker(_transactionEndMarker);  // marking end of a new transaction, making it possible to separate each transaction in a corrupted file
        }
        if (progress != null) progress("Flushed " + transactionCount + " transactions and " + actionCount + " actions", 100);
        long bytesWritten = stream.Length - bytesStartPos;
        return bytesWritten;
    }
    public long GetPositionAfterLastTransaction() {
        return _appendStream.Length;
    }
    public long NewTimestamp() {
        var nowUtc = DateTime.UtcNow.Ticks;
        return nowUtc > _lastTimestampID ? _lastTimestampID = nowUtc : ++_lastTimestampID;
    }
    public void QueDiskWrites(ExecutedPrimitiveTransaction transaction) {
        // No locks needed, as _workQueue is threadsafe
        if (FirstTimestamp == 0) FirstTimestamp = transaction.Timestamp;
        _workQueue.Add(transaction);
    }
    public void DequeuAllTransactionWritesAndFlushStreamsThreadSafe(bool deepFlush) => DequeuAllTransactionWritesAndFlushStreamsThreadSafe(deepFlush, null, out _, out _, out _);
    public void DequeuAllTransactionWritesAndFlushStreamsThreadSafe(bool deepFlush, Action<string, int>? progress, out int transactionCount, out int actionCount, out long bytesWritten) {
        // write everything to stream, no locks needed as _workQueue is threadsafe ( and write method uses locks)
        _workQueue.DequeAllWorkThreadSafe(progress, out transactionCount, out actionCount, out bytesWritten);
        _appendStream.Flush(deepFlush);
        if (_secondaryAppendStream != null) _secondaryAppendStream.Flush(deepFlush);
    }
    static int batchLimit = 1024 * 1024 * 10; // 10MB. Too low, and we get too many calls to io stream, to high and allocate unnecessary memory
    static int deltaLimit = 1024 * 200; // 200K. Too low and we get to many batches, too high and we read a lot of unnecessary data
    // the whole purpose is to reduce time wasted on io or network latency
    // we get as many as we can that fit in the batch limit, and we group segments that are close into one call,
    // even though we read some unnecessary data, it is still faster than making a lot of calls to the io stream
    // this is particularly important if the io stream is remote or an Azure blob store
    public byte[] ReadOneNodeSegments(NodeSegment segment) {
        var buffer = new byte[segment.Length];
        _appendStream.Get(segment.AbsolutePosition, segment.Length, buffer);
        return buffer;
    }
    public byte[][] ReadNodeSegments(NodeSegment[] segments, out int diskReads) {
        // trying to read segments in batches to reduce number of calls to io stream ( which may have siginificant latency if disk is remote)
        // 1 order segments by position
        // 2 read segments in batches that are close (deltaLimit) 
        // 3 if batch is larger than batchLimit, then read it and start a new batch
        var count = segments.Length;
        //Console.WriteLine("Reading nodes " + count);
        diskReads = 0;
        if (count == 0) return [];
        if (count == 1) {
            var segment = segments.First();
            var buffer = new byte[segment.Length];
            _appendStream.Get(segment.AbsolutePosition, segment.Length, buffer);
            diskReads++;
            return [buffer];
        }
        var result = new byte[count][];
        var segWithPos = new (int pos, NodeSegment seg)[count];
        var i = 0;
        foreach (var segment in segments) segWithPos[i] = new(i++, segment);
        var batch = new List<(int pos, NodeSegment seg)>();
        var batchSize = 0L;
        var batchStart = -1L; // absolute position of first segment in current batch
        var lastEndPos = -1L;
        foreach (var p in segWithPos.OrderBy(i => i.seg.AbsolutePosition)) {
            var deltaNext = lastEndPos > 0 ? p.seg.AbsolutePosition - lastEndPos : 0;
            // spanWithNext is the total byte span (including gaps) the batch would cover if this segment is added,
            // it must be limited as the whole span is read into one buffer ( and cast to int )
            var spanWithNext = batchStart >= 0 ? p.seg.AbsolutePosition + p.seg.Length - batchStart : 0;
            if (batchSize > batchLimit || deltaNext > deltaLimit || spanWithNext > batchLimit) {
                readBatchAndAddToResult(batch, result, ref batchSize, ref diskReads); // read batch, and start new batch
                batchStart = -1;
            }
            if (batchStart < 0) batchStart = p.seg.AbsolutePosition;
            batch.Add(p);
            batchSize += p.seg.Length;
            lastEndPos = p.seg.AbsolutePosition + p.seg.Length;
        }
        if (batch.Count > 0) readBatchAndAddToResult(batch, result, ref batchSize, ref diskReads); // read last batch
        return result;
    }
    readonly object _bufferLock = new(); // dedicated lock object, _buffer itself cannot be used as it is reassigned when it grows
    byte[] _buffer = new byte[batchLimit]; // common buffer for reading segments, ( simultaneous reads are not allowed)
    void readBatchAndAddToResult(List<(int pos, NodeSegment seg)> batch, byte[][] result, ref long batchSize, ref int diskReads) {
        //Console.WriteLine("Reading batch of " + batch.Count);
        var start = batch.First().seg.AbsolutePosition;
        var lastSeg = batch.Last();
        var end = lastSeg.seg.AbsolutePosition + lastSeg.seg.Length;
        var length = (int)(end - start);
        lock (_bufferLock) { // lock to avoid simultaneous reads to common buffer
            if (_buffer.Length < length) _buffer = new byte[length];  // ensure buffer is large enough
            _appendStream.Get(start, length, _buffer);
            diskReads++;
            foreach (var b in batch) {
                var from = (int)(b.seg.AbsolutePosition - start);
                var to = from + b.seg.Length;
                result[b.pos] = _buffer[from..to];
            }
            batch.Clear();
            batchSize = 0;
        }
    }
    public void Dispose() {
        DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
        _appendStream.Dispose();
        if (_secondaryAppendStream != null) _secondaryAppendStream.Dispose();
    }
    internal void ReplaceDataFile(string[] newFileKey, long lastTimestamp, Dictionary<int, NodeSegment> newFileChainHeads) {
        FirstTimestamp = 0; // 0 means it will be read from file
        // transactions queued during a rewrite carry timestamps newer than the new file's last timestamp,
        // so the timestamp may only move forward, otherwise duplicate timestamps could be issued after the swap:
        EnsureTimestamps(lastTimestamp);
        DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
        Close();
        FileKey = newFileKey;
        FileId = Guid.Empty; // reset file id, so that it is read from new file
        OpenForAppending();
        // the primary version chains now live in the new file; adopt the heads the rewriter built.
        // The secondary log is untouched by a rewrite, so its chains simply continue:
        lock (_chainLock) _chainHeads = newFileChainHeads;
    }
    /// <summary>The version-chain heads this file's writer built, for adoption by the live WAL at a hot swap.</summary>
    internal Dictionary<int, NodeSegment> DetachChainHeads() {
        lock (_chainLock) return _chainHeads;
    }
    internal void StoreTimestamp(long timestamp) {
        if (timestamp < _lastTimestampID) throw new Exception("New timestamp is less than last timestamp. ");
        _lastTimestampID = timestamp;
        QueDiskWrites(new(new(), timestamp));
        DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
    }
    public void EnsureTimestamps(long readTimestamp) {
        if (readTimestamp <= _lastTimestampID) return;
        _lastTimestampID = readTimestamp;
    }
    internal int GetQueueActionCount() {
        return _workQueue.GetQueueActionCount();
    }
    internal void AddInfo(DataStoreInfo s) {
        s.LogWritesQueuedTransactions = _workQueue.EstimateTransactionCount;
        s.LogWritesQueuedActions = _workQueue.GetQueueActionCount();
        s.LogFileKey = FileKey.AsKeyString();
        try {
            s.LogFileSize = _appendStream?.Length ?? 0;
        } catch { } // file may be closed....
        try {
            s.SecondaryLogFileSize = _secondaryAppendStream?.Length ?? 0;
        } catch { } // file may be closed....
    }
    internal void Copy(string[] newLogFileKey, IIOProvider? destinationIO = null) {
        DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
        try {
            if (destinationIO == null) destinationIO = _io;
            if (newLogFileKey.IsSameKey(FileKey) && _io == destinationIO) throw new Exception("Cannot copy to same file. ");
            destinationIO.DeleteFileIfItExists(newLogFileKey);
            Close();
            using IReadStream readStream = _io.OpenRead(FileKey, 0);
            using IAppendStream writeStream = destinationIO.OpenAppend(newLogFileKey);
            try {
                var totalLength = readStream.Length;
                var pos = 0L;
                while (pos < totalLength) {
                    int bytesToRead = (int)Math.Min(1024 * 1024, totalLength - pos);
                    var bytes = readStream.Read(bytesToRead);
                    writeStream.Append(bytes);
                    pos += bytes.Length;
                }
            } catch (Exception ex) {
                writeStream.Dispose();
                readStream.Dispose();
                throw new Exception("Error copying log file. ", ex);
            }
            writeStream.Dispose();
            readStream.Dispose();
        } finally {
            OpenForAppending();
        }
    }
    internal void EnsureSecondaryLogFile(long activityId, DataStoreLocal store, bool resetSecondaryFile) {
        if (!store.Settings.SecondaryBackupLog) {
            store.LogInfo("Secondary backup log not enabled. ");
            return;
        }
        if (_ioSecondary == null) throw new Exception("Secondary IO provider not configured. ");
        if (_secondaryFileKey == null) throw new Exception("Secondary file key not configured. ");
        if (_secondaryAppendStream != null) {
            _secondaryAppendStream.Dispose();
            _secondaryAppendStream = null;
        }
        if (resetSecondaryFile) {
            store.LogInfo("Resetting secondary log file. ");
            _ioSecondary.DeleteFileIfItExists(_secondaryFileKey);
        }
        var hasSecondary = _ioSecondary.ExistsAndIsNotEmpty(_secondaryFileKey);
        if (!hasSecondary) {
            store.LogInfo("Creating secondary log file from primary. ");
            store.UpdateActivity(activityId, "Creating secondary log file from primary. ", 0);
            Close();
            try {
                _io.CopyFile(_ioSecondary, FileKey, _secondaryFileKey, progress => {
                    store.UpdateActivity(activityId, "Creating secondary log file from primary. ", progress);
                });
            } finally {
                OpenForAppending();
            }
            // the OpenForAppending above reopened the secondary stream on the fresh copy, so the
            // format version and file id are current here.
            // A byte-identical copy of the primary: the primary version-chain heads are valid
            // positions in it. At store open the heads are empty here and are seeded later
            // (SeedChainHeadsWhileClosed); at a runtime reset they are live and copied now:
            lock (_chainLock) {
                _secondaryChainHeads.Clear();
                if (_secondaryFormatVersion >= _logVersioNumber) {
                    foreach (var kv in _chainHeads) _secondaryChainHeads[kv.Key] = kv.Value;
                }
            }
        } else {
            store.LogInfo("Secondary log file active. ");
            // Add checks for latest timestamp match between primary and secondary log files

            // check if timestamps match?...
            //var latestPrimaryTimestamp = WALFile.GetLastTimestampInLog(_io, fileKey);
            //var latestSecondaryTimestamp = WALFile.GetLastTimestampInLog(_io2, fileKey2);
            //if(latestPrimaryTimestamp!= latestSecondaryTimestamp) {
            //    throw new Exception("Primary and secondary log files are out of sync. ");
            //}
            _secondaryAppendStream = getWriteStream(_ioSecondary, _secondaryFileKey, true, out _secondaryFormatVersion);
        }
    }

    #region Version chains

    /// <summary>
    /// Establishes the version-chain heads after a store open. Called while the log streams are
    /// closed, after the state file and the log replay have produced the final node segments. The
    /// primary heads are the node segments themselves (they reflect write order after a replay).
    /// The secondary heads come from the persisted chain state brought up to date by replaying the
    /// secondary's tail; without usable persisted state they fall back to the primary heads when
    /// the secondary is a byte-identical copy of the primary, and otherwise start empty (chains in
    /// the file stay intact but new records start fresh chains).
    /// </summary>
    internal void SeedChainHeadsWhileClosed(WalChainState? persisted, (int nodeId, NodeSegment segment)[] nodeSegments, Action<string> logInfo, Action<string, Exception?> logError) {
        lock (_chainLock) {
            _chainHeads = new Dictionary<int, NodeSegment>(_formatVersion >= _logVersioNumber ? nodeSegments.Length : 0);
            if (_formatVersion >= _logVersioNumber) {
                foreach (var n in nodeSegments) if (n.segment.Length > 0) _chainHeads[n.nodeId] = n.segment;
            }
            _secondaryChainHeads.Clear();
            if (_ioSecondary == null || _secondaryFileKey == null) return;
            var secondarySize = _ioSecondary.GetFileSizeOrZeroIfUnknown(_secondaryFileKey);
            if (secondarySize < posOfFirstTransaction) return; // missing, or holds no transactions yet
            try {
                using var s = _ioSecondary.OpenRead(_secondaryFileKey, 0);
                s.ValidateMarker(_logStartMarker);
                _secondaryFormatVersion = s.ReadVerifiedLong();
                _secondaryFileId = s.ReadGuid();
            } catch (Exception err) {
                logError("Unable to read the secondary log file header. Node version chains in the secondary log restart from here. ", err);
                return;
            }
            if (_secondaryFormatVersion < _logVersioNumber) return; // legacy format, carries no chains
            if (persisted != null && persisted.SecondaryFileId == _secondaryFileId && persisted.SecondaryLength <= secondarySize) {
                foreach (var kv in persisted.Heads) {
                    if (kv.Value.AbsolutePosition + kv.Value.Length <= secondarySize) _secondaryChainHeads[kv.Key] = kv.Value;
                }
                if (persisted.SecondaryLength < secondarySize) replaySecondaryTail(persisted.SecondaryLength, logError);
            } else if (_secondaryFileId == FileId && secondarySize == _io.GetFileSizeOrZeroIfUnknown(FileKey)) {
                // the secondary is a byte-identical copy of the primary (same file id, same length,
                // and identical files receive identical appends), so the primary heads are valid in it
                foreach (var kv in _chainHeads) _secondaryChainHeads[kv.Key] = kv.Value;
            } else {
                logInfo("No usable version-chain state for the secondary log file. Node version chains in the secondary log restart from here; older chains in the file stay readable but unreachable. ");
            }
        }
    }
    // brings persisted secondary heads up to date with records written after the last state save
    // (an unclean shutdown leaves the state file behind the log files)
    void replaySecondaryTail(long fromPosition, Action<string, Exception?> logError) {
        using var reader = new LogReader(_secondaryFileKey!, _definition, _ioSecondary!, fromPosition, 0);
        while (reader.ReadNextTransaction(out var transaction, false, logError, out _)) {
            foreach (var a in transaction.ExecutedActions) {
                if (a is not PrimitiveNodeAction na || na.Segment == null) continue;
                if (na.Operation == PrimitiveOperation.Add) _secondaryChainHeads[na.Node.__Id] = na.Segment.Value;
                else _secondaryChainHeads.Remove(na.Node.__Id);
            }
        }
    }
    /// <summary>Persists the secondary version-chain heads as part of the state file. The primary
    /// heads are not written: they equal the node segments, which the state file already holds.
    /// Requires the same quiescence as the rest of the state save (queue drained, under the write lock).</summary>
    internal void SaveChainState(IAppendStream stream) {
        stream.WriteMarker(_chainStateMarker);
        stream.RecordChecksum();
        var hasSecondary = _secondaryAppendStream != null && _secondaryFormatVersion >= _logVersioNumber;
        stream.WriteBool(hasSecondary);
        if (hasSecondary) {
            stream.WriteGuid(_secondaryFileId);
            stream.WriteVerifiedLong(_secondaryAppendStream!.Length);
            lock (_chainLock) {
                stream.WriteVerifiedInt(_secondaryChainHeads.Count);
                foreach (var kv in _secondaryChainHeads) {
                    stream.WriteUInt((uint)kv.Key);
                    stream.WriteLong(kv.Value.AbsolutePosition);
                    stream.WriteVerifiedInt(kv.Value.Length);
                }
            }
        }
        stream.WriteChecksum();
        stream.WriteGuid(_chainStateMarker);
    }
    internal static WalChainState? ReadChainState(BufferReader stream) {
        stream.ValidateMarker(_chainStateMarker);
        stream.RecordChecksum();
        WalChainState? result = null;
        if (stream.ReadBool()) {
            var fileId = stream.ReadGuid();
            var length = stream.ReadVerifiedLong();
            var count = stream.ReadVerifiedInt();
            var heads = new Dictionary<int, NodeSegment>(count);
            for (var i = 0; i < count; i++) {
                var id = (int)stream.ReadUInt();
                var pos = stream.ReadLong();
                var len = stream.ReadVerifiedInt();
                heads[id] = new NodeSegment(pos, len);
            }
            result = new WalChainState { SecondaryFileId = fileId, SecondaryLength = length, Heads = heads };
        }
        stream.ValidateChecksum();
        stream.ValidateMarker(_chainStateMarker);
        return result;
    }
    /// <summary>
    /// Collects versions of a node strictly older than the current one by walking the version
    /// chains backwards, first in the primary log (history since the last rewrite), then in the
    /// secondary log (history across rewrites). Reads straight from the log files — one read per
    /// version, nothing cached. Duplicates (the overlap between the files) are removed by
    /// timestamp; the caller merges, orders and caps the result. Caller must hold the store's read lock.
    /// </summary>
    internal List<NodeVersionRecord> CollectOlderVersions(int id, Guid nodeGuid, NodeSegment currentSegment, int maxCount) {
        var results = new List<NodeVersionRecord>();
        var seenTimestamps = new HashSet<long>();
        if (_formatVersion >= _logVersioNumber) {
            NodeSegment start;
            bool skipFirst;
            if (currentSegment.AbsolutePosition > 0) {
                start = currentSegment; // the head is the current version; only its back pointer is used
                skipFirst = true;
            } else {
                // the current version is still queued and not in the file yet, so the last written
                // add is itself an older version and is included
                lock (_chainLock) _chainHeads.TryGetValue(id, out start);
                skipFirst = false;
            }
            walkChain(_appendStream, FileKey.AsKeyString(), start, skipFirst, nodeGuid, maxCount, seenTimestamps, results);
        }
        if (_secondaryAppendStream != null && _secondaryFormatVersion >= _logVersioNumber && _secondaryFileKey != null) {
            NodeSegment head;
            lock (_chainLock) _secondaryChainHeads.TryGetValue(id, out head);
            // the secondary head is the current version, or one the primary walk already covered
            // (the primary is written first within a flush), so it is always skipped
            walkChain(_secondaryAppendStream, _secondaryFileKey.AsKeyString(), head, skipFirst: true, nodeGuid, maxCount, seenTimestamps, results);
        }
        return results;
    }
    void walkChain(IAppendStream stream, string source, NodeSegment segment, bool skipFirst, Guid nodeGuid, int maxCount, HashSet<long> seenTimestamps, List<NodeVersionRecord> results) {
        var pos = segment.AbsolutePosition;
        var len = segment.Length;
        var lastTimestamp = long.MaxValue;
        var first = true;
        var collected = 0;
        while (pos > VersionHeaderSize && len > 0 && collected < maxCount) {
            if (pos + len > stream.Length) break;
            var buffer = new byte[VersionHeaderSize + len];
            stream.Get(pos - VersionHeaderSize, buffer.Length, buffer);
            var timestamp = BitConverter.ToInt64(buffer, 0);
            var prevPos = BitConverter.ToInt64(buffer, 8);
            var prevLen = BitConverter.ToInt32(buffer, 16);
            if (timestamp <= 0 || timestamp >= lastTimestamp) break; // chain timestamps decrease strictly; anything else is a broken chain
            if (!(first && skipFirst)) {
                INodeDataInternal node;
                try {
                    node = FromBytes.NodeData(_definition.Datamodel, new MemoryStream(buffer, VersionHeaderSize, len, false), null);
                } catch {
                    break; // stop at anything unreadable and return what was found
                }
                if (node.Id != nodeGuid) break; // internal node ids can be reused; the chain has crossed into another node's history
                if (seenTimestamps.Add(timestamp)) results.Add(new NodeVersionRecord(timestamp, source, node));
                collected++;
            }
            lastTimestamp = timestamp;
            first = false;
            pos = prevPos;
            len = prevLen;
        }
    }

    #endregion

}

/// <summary>The secondary log's version-chain heads as persisted in (and read back from) the state file.</summary>
internal sealed class WalChainState {
    public required Guid SecondaryFileId { get; init; }
    /// <summary>The secondary log file's length at the state save; the tail after it is replayed at open to bring the heads up to date.</summary>
    public required long SecondaryLength { get; init; }
    public required Dictionary<int, NodeSegment> Heads { get; init; }
}

/// <summary>One version of a node found by a chain walk: its transaction timestamp, the log file it
/// was read from and the deserialized node.</summary>
internal readonly record struct NodeVersionRecord(long Timestamp, string Source, INodeDataInternal Node);
