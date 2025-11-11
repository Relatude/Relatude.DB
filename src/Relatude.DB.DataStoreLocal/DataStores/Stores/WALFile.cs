using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;
using System.Runtime;

namespace Relatude.DB.DataStores.Stores {
    internal delegate void RegisterNodeSegmentCallbackFunc(int id, NodeSegment seg);
    internal delegate void DiskFlushCallback(long timestamp);
    /// <summary>
    // WAL (Write Ahead Log) store, used to store all changes to the database
    // Threadsafe read operations to support multiple read queries at the same time
    // Write operations are NOT threadsafe, including FlushDisk, but store is designed to be used with a single writer thread
    /// </summary>
    internal class WALFile : IDisposable {
        // File format is designed to detect and repair from a partially completed write
        // and also make it possible to extract data if file is corrupted. 
        // It uses markers to indicate start and end of log file, if an corruption is found, the reader skips to the next transaction start marker.
        public readonly static long _logVersioNumber = 1000; // indicates file format version
        public readonly static Guid _logStartMarker = new Guid("01114d5b-d268-4ece-a498-2b7961c1a3f8"); // just a unique number
        public readonly static Guid _transactionStartMarker = new Guid("a02520c1-60aa-426b-b002-76c76e71a8be"); // just a unique number
        public readonly static Guid _transactionEndMarker = new Guid("8ad9629a-dd38-4641-94f4-c7e10c1e2eea"); // just a unique number
        public readonly static Guid _actionMarker = new Guid("52a6fb71-979e-4184-94c7-93724a5278d8"); // just a unique number
        const long posOfFirstTransaction = 64; // start of first transaction
        internal Guid FileId { get; private set; } // a unique id for the file, created at start up and links til file to a statefile
        internal string FileKey { get; private set; }
        readonly Definition _definition;
        readonly RegisterNodeSegmentCallbackFunc _registerAndConfrimeNodeWrite; // callback to store to register byte position a node in log file
        readonly DiskFlushCallback? _flushCallback; // callback for updating PersistentIndexStore if used, syncing timestamp and committing transactions
        readonly LogQueue _workQueue; // queue for write operations, to make sure they are written in bacthes for better performance
        IIOProvider _io;
        IIOProvider? _ioSecondary;
        IAppendStream _appendStream;
        IAppendStream? _secondaryAppendStream;
        string? _secondaryFileKey;
        long _lastTimestampID;
        public WALFile(string fileKey, Definition definition, IIOProvider io, RegisterNodeSegmentCallbackFunc confirmWrite, DiskFlushCallback? flushCallback, IIOProvider? ioSecondary, string? secondaryFileKey) {
            FileKey = fileKey;
            _io = io;
            _definition = definition;
            _registerAndConfrimeNodeWrite = confirmWrite;
            _workQueue = new(write);
            _lastTimestampID = 0;
            _flushCallback = flushCallback;
            _ioSecondary = ioSecondary;
            _secondaryFileKey = secondaryFileKey;
            _appendStream = getWriteStream(_io, FileKey, false); // open primary log file, to lock file, even though it may not be used right away
        }
        public void Open() {
            _appendStream = getWriteStream(_io, FileKey, false);
            if (_ioSecondary != null && _secondaryFileKey != null) {
                _secondaryAppendStream = getWriteStream(_ioSecondary, _secondaryFileKey, true);
            }
        }
        public void Close() {
            _appendStream.Dispose();
            _secondaryAppendStream?.Dispose();
        }
        static long getFirstTimestampIfAny(IAppendStream appendStream) {
            if (appendStream.Length > posOfFirstTransaction) {
                return appendStream.GetLong(posOfFirstTransaction);
            } else {
                return 0;
            }
        }
        public long FirstTimestamp { get; private set; }
        public long LastTimestamp { get => _lastTimestampID; }
        public long FileSize {
            get {
                if (_appendStream == null) return _io.GetFileSizeOrZeroIfUnknown(FileKey);
                return _appendStream.Length;
            }
        }
        IAppendStream getWriteStream(IIOProvider io, string fileKey, bool isSecondaryLog) {
            IAppendStream? s = null;
            try {
                s = io.OpenAppend(fileKey);
                if (s.Length == 0) {
                    s.WriteMarker(_logStartMarker);// pos 0
                    s.WriteVerifiedLong(_logVersioNumber); // pos 16
                    if (!isSecondaryLog) {
                        FileId = Guid.NewGuid(); // do not create new file id for secondary log
                    }
                    s.WriteGuid(FileId); // pos 32
                    // now at pos 48 
                    FirstTimestamp = 0;
                } else {
                    if (s.GetGuid(0) != _logStartMarker) throw new IOException("Unable to open log file. Data is not compatible. ");
                    var version = s.GetVerifiedLong(16);
                    if (version != _logVersioNumber) throw new IOException("Incompatible log file format version number. Expected version " + _logVersioNumber + " but found " + version + " .");
                    var readFileId = s.GetGuid(32);
                    if (!isSecondaryLog) {
                        if (FileId == Guid.Empty) FileId = readFileId;
                        else if (readFileId != FileId) throw new Exception("FileId mismatch. ");
                        FirstTimestamp = getFirstTimestampIfAny(s);
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
            var written = writeStatic(transactions, _appendStream, _definition.Datamodel, _registerAndConfrimeNodeWrite, progress1, actionCount, transactionCount);
            if (_ioSecondary != null) {
                Action<string, int>? progress2 = progress != null ? (msg, perc) => progress("Secondary: " + msg, 50 + (perc / 2)) : null;
                if (_secondaryAppendStream == null) _secondaryAppendStream = getWriteStream(_ioSecondary, _secondaryFileKey!, true);
                writeStatic(transactions, _secondaryAppendStream, _definition.Datamodel, null, progress, actionCount, transactionCount);
            }
            return written;
        }
        static long writeStatic(ExecutedPrimitiveTransaction[] transactions, IAppendStream stream, Datamodel datamodel, RegisterNodeSegmentCallbackFunc? regCallback, Action<string, int>? progress, int actionCount, int transactionCount) {
            long bytesStartPos = stream.Length;
            if (progress != null) progress("Flushing " + transactionCount + " transactions and " + actionCount + " actions", 0);
            int transactionsWritten = 0;
            int actionsWritten = 0;
            foreach (var transaction in transactions) {
                transactionsWritten++;
                stream.WriteMarker(_transactionStartMarker);  // marking end of a new transaction, making it possible to separate each transaction in a corrupted file
                stream.WriteLong(transaction.Timestamp);
                stream.WriteVerifiedInt(transaction.ExecutedActions.Count);
                foreach (var action in transaction.ExecutedActions) {
                    actionsWritten++;
                    stream.WriteMarker(_actionMarker);
                    var ms = new MemoryStream();
                    PToBytes.ActionBase(action, datamodel, ms, out long nodeSegmentRelativeOffset, out int nodeSegmentLength);
                    var actionData = ms.ToArray();
                    var segmentStreamPosition = stream.Length + 8; // add 8 as first byte is for array length, we only want exact position of node data bytes
                    stream.WriteByteArray(actionData);
                    if (actionsWritten % 93 == 0 && progress != null) // update progress every 93 actions
                        progress("Flushing " + transactionsWritten + " of " + transactionCount + " transactions and " + actionsWritten + " of " + actionCount + " actions", (int)((transactionsWritten / (double)transactionCount) * 100));
                    stream.WriteUInt(actionData.GetChecksum());
                    if (action is PrimitiveNodeAction na && regCallback != null) {
                        var absolutePosition = segmentStreamPosition + nodeSegmentRelativeOffset;
                        if (absolutePosition == 0) throw new Exception();
                        regCallback(na.Node.__Id, new NodeSegment(absolutePosition, nodeSegmentLength));
                    }
                }
                stream.WriteMarker(_transactionEndMarker);  // marking end of a new transaction, making it possible to separate each transaction in a corrupted file
            }
            if (progress != null) progress("Flushed " + transactionCount + " transactions and " + actionCount + " actions", 100);
            long bytesWritten = stream.Length - bytesStartPos;
            return bytesWritten;
        }
        public long GetPositionOfLastTransaction() {
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
        public void DequeuAllTransactionWritesAndFlushStreams(bool deepFlush) => DequeuAllTransactionWritesAndFlushStreams(deepFlush, null, out _, out _, out _);
        public void DequeuAllTransactionWritesAndFlushStreams(bool deepFlush, Action<string, int>? progress, out int transactionCount, out int actionCount, out long bytesWritten) {
            // write everything to stream, no locks needed as _workQueue is threadsafe ( and write method uses locks)
            _workQueue.DequeAllWork(progress, out transactionCount, out actionCount, out bytesWritten);
            _appendStream.Flush(deepFlush);
            if (_secondaryAppendStream != null) _secondaryAppendStream.Flush(deepFlush);
            if (_flushCallback != null) _flushCallback(_lastTimestampID);
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
            var lastEndPos = -1L;
            foreach (var p in segWithPos.OrderBy(i => i.seg.AbsolutePosition)) {
                var deltaNext = lastEndPos > 0 ? p.seg.AbsolutePosition - lastEndPos : 0;
                if (batchSize > batchLimit || deltaNext > deltaLimit) {
                    readBatchAndAddToResult(batch, result, ref batchSize, ref diskReads); // read batch, and start new batch
                }
                batch.Add(p);
                batchSize += p.seg.Length;
                lastEndPos = p.seg.AbsolutePosition + p.seg.Length;
            }
            if (batch.Count > 0) readBatchAndAddToResult(batch, result, ref batchSize, ref diskReads); // read last batch
            return result;
        }
        byte[] _buffer = new byte[batchLimit]; // common buffer for reading segments, ( simultaneous reads are not allowed)
        void readBatchAndAddToResult(List<(int pos, NodeSegment seg)> batch, byte[][] result, ref long batchSize, ref int diskReads) {
            //Console.WriteLine("Reading batch of " + batch.Count);
            var start = batch.First().seg.AbsolutePosition;
            var lastSeg = batch.Last();
            var end = lastSeg.seg.AbsolutePosition + lastSeg.seg.Length;
            var length = (int)(end - start);
            lock (_buffer) { // lock to avoid simultaneous reads to common buffer
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
            DequeuAllTransactionWritesAndFlushStreams(true);
            _appendStream.Dispose();
            if (_secondaryAppendStream != null) _secondaryAppendStream.Dispose();
        }
        internal void ReplaceDataFile(string newFileKey, long lastTimestamp) {
            FirstTimestamp = 0; // 0 means it will be read from file
            _lastTimestampID = lastTimestamp;
            DequeuAllTransactionWritesAndFlushStreams(true);
            Close();
            FileKey = newFileKey;
            FileId = Guid.Empty; // reset file id, so that it is read from new file
            Open();
        }
        internal void StoreTimestamp(long timestamp) {
            if (timestamp < _lastTimestampID) throw new Exception("New timestamp is less than last timestamp. ");
            _lastTimestampID = timestamp;
            QueDiskWrites(new(new(), timestamp));
            DequeuAllTransactionWritesAndFlushStreams(true);
        }
        public void EnsureTimestamps(long readTimestamp) {
            if (readTimestamp <= _lastTimestampID) return;
            _lastTimestampID = readTimestamp;
        }
        internal void AddInfo(StoreStatus s) {
            s.LogWritesQueued = _workQueue.EstimateCount;
            s.LogFileKey = FileKey;
            s.LogFileSize = _appendStream?.Length ?? 0;
        }
        internal void Copy(string newLogFileKey, IIOProvider? destinationIO = null) {
            DequeuAllTransactionWritesAndFlushStreams(true);
            try {
                if (destinationIO == null) destinationIO = _io;
                if (newLogFileKey == FileKey && _io == destinationIO) throw new Exception("Cannot copy to same file. ");
                destinationIO.DeleteIfItExists(newLogFileKey);
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
                Open();
            }
        }
        internal void EnsureSecondaryLogFile(long activityId, DataStoreLocal store, bool resetSecondaryFile) {
            DequeuAllTransactionWritesAndFlushStreams(true);
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
                _ioSecondary.DeleteIfItExists(_secondaryFileKey);
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
                    Open();
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
            _secondaryAppendStream = getWriteStream(_ioSecondary, _secondaryFileKey, true);
            }
        }
    }
}
