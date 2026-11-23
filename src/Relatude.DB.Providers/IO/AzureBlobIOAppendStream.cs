namespace Relatude.DB.IO {
    // this is optmized for append operations and scattered reads
    // it uses a write buffer to accumulate data before uploading to blob, reducing the effect of latency
    // the write buffer is flushed when it reaches a certain size
    // the write buffer is also used for reading, if the read position is within the write buffer. Ie just reading something that was just written
    // there is also a smaller read buffer, as sometimes the get request are small and sequential. This recudes latency for multiple small reads
    // but in general I do not want to make the read buffer too big, as most reads are scattered and therefore a read buffer would not be very effective
    // it sets a minimum download size, and a larger read buffer, would mean small scattered reads would download a lot of unneeded data

    // in general: the read buffer size should be so small that the blob access latency is comparable to the download time of the read buffer
    // a first appriximation 50kb is a good size for the read buffer, and 2mb for the write buffer
    public class AzureBlobIOAppendStream : IAppendStream {
        readonly AzureBlobRestClient _client;
        readonly string _blobName;
        // the container's lock this stream writes under; null without lockBlob
        readonly AzureBlobContainerLock.Lease? _lease;
        readonly Action<long> _disposeCallback;
        MemoryStream _writeBuffer;
        long _committedLength; // length of the blob on the server, always _length minus what is in the write buffer
        long _maxBufferBeforeFlush = 1024 * 1024 * 20; // 20 mb
        int _readBufferSize = 1024 * 50 * 2; // 100KB
        long _readBufferOffset = 0;
        byte[]? _readBuffer; // up to 100KB
        int _readBufferLength; // how much of _readBuffer holds blob bytes: less than its size near the end of the blob
        readonly object _lock = new();
        ChecksumUtil _checkSum = new();
        public string FileKey { get; }
        // the blob exists and the container is locked by the time this runs: the provider does both, and
        // gives the file back when this throws
        internal AzureBlobIOAppendStream(AzureBlobRestClient client, string blobName, AzureBlobContainerLock.Lease? lease, Action<long> disposeCallback) {
            _disposeCallback = disposeCallback;
            FileKey = blobName;
            _client = client;
            _blobName = blobName;
            _lease = lease;
            _writeBuffer = new MemoryStream();
            var properties = _client.GetProperties(blobName);
            // this version leases no blob but the container's lock, so a lease on this one is an older version's
            if (properties?.LeaseState is "leased" or "breaking") throw leasedByAnOlderVersion(blobName, properties.LeaseDuration);
            _committedLength = properties?.ContentLength ?? 0;
            _length = _committedLength;
        }
        static BlobLeasedException leasedByAnOlderVersion(string blobName, string? duration) {
            var infinite = string.Equals(duration, "infinite", StringComparison.OrdinalIgnoreCase);
            var text = "\"" + blobName + "\" is leased, so it cannot be written. ";
            text += infinite
                ? "The lease never ends on its own: an older version of Relatude.DB left it behind, or another tool took it. Once no other "
                    + "instance is using this storage, break it in the admin UI, on the dashboard of the database or under Files with Leases."
                : "It is held by a process running an older version of Relatude.DB, which leased every blob it wrote, or by another tool. ";
            return new BlobLeasedException(text, null, blobName, infinite);
        }
        long _bytesRead;
        long _bytesWritten;
        readonly IOCounter _io = new();
        public IOCounters Counters => _io.Snapshot;
        public long GetBytesWritten() => _bytesWritten;
        public long GetBytesRead() => _bytesRead;
        public void ResetByteCounter() {
            _bytesRead = 0;
            _bytesWritten = 0;
        }
        long _length = 0;
        public long Length {
            get {
                lock (_lock) {
                    if (_isDisposed) throw new Exception("Stream is closed");
                    return _length;
                }
            }
        }
        public void Append(byte[] data) {
            lock (_lock) {
                _readBuffer = null; // reset read buffer, as new data is appended, that will not be in readbuffer
                _checkSum.EvaluateChecksumIfRecording(data);
                _writeBuffer.Write(data, 0, data.Length);
                _length += data.Length;
                _bytesWritten += data.Length;
                _io.Write(data.Length);
                if (_writeBuffer.Length > _maxBufferBeforeFlush) Flush(true);
            }
        }
        public void Append(byte[] data, int count) {
            lock (_lock) {
                _readBuffer = null; // reset read buffer, as new data is appended, that will not be in readbuffer
                _checkSum.EvaluateChecksumIfRecording(data, count);
                _writeBuffer.Write(data, 0, count);
                _length += count;
                _bytesWritten += count;
                _io.Write(count);
                if (_writeBuffer.Length > _maxBufferBeforeFlush) Flush(true);
            }
        }
        public void Flush(bool deepFlush) {
            lock (_lock) {
                if (_writeBuffer.Length == 0) return;
                var buffer = _writeBuffer.GetBuffer();
                var total = (int)_writeBuffer.Length;
                if (total <= _maxBufferBeforeFlush) {
                    appendBlock(buffer, total);
                } else {
                    var offset = 0;
                    while (offset < total) {
                        var blockSize = (int)Math.Min(total - offset, _maxBufferBeforeFlush);
                        var block = new byte[blockSize];
                        Array.Copy(buffer, offset, block, 0, blockSize);
                        appendBlock(block, blockSize);
                        offset += blockSize;
                    }
                }
                _writeBuffer = new MemoryStream();
            }
        }
        // the append position guard also keeps a process that lost the lock from writing over another's appends
        void appendBlock(byte[] data, int count) {
            _lease?.EnsureWritable(_blobName);
            _client.AppendBlock(_blobName, data, count, _committedLength);
            _committedLength += count;
        }
        async Task appendBlockAsync(byte[] data, int count) {
            _lease?.EnsureWritable(_blobName);
            await _client.AppendBlockAsync(_blobName, data, count, _committedLength);
            _committedLength += count;
        }
        async Task flushAsync(bool deepFlush) {
            if (_writeBuffer.Length == 0) return;
            var buffer = _writeBuffer.GetBuffer();
            var total = (int)_writeBuffer.Length;
            if (total <= _maxBufferBeforeFlush) {
                await appendBlockAsync(buffer, total);
            } else {
                var offset = 0;
                while (offset < total) {
                    var blockSize = (int)Math.Min(total - offset, _maxBufferBeforeFlush);
                    var block = new byte[blockSize];
                    Array.Copy(buffer, offset, block, 0, blockSize);
                    await appendBlockAsync(block, blockSize);
                    offset += blockSize;
                }
            }
            _writeBuffer = new MemoryStream();
        }
        // What is in memory - the write buffer, the read buffer - is copied under the lock; a download runs
        // outside it. Bytes once committed to an append blob never change, so a range below the committed
        // length can be fetched while appends, flushes and other reads go on: a read no longer holds the
        // stream for a network round trip.
        public void Get(long position, int count, byte[] result) {
            int committedCount; // the head of the range that has to come from the blob
            long committedLength;
            lock (_lock) {
                if (count > _length - position) throw new Exception("Read beyond end of file");
                _bytesRead += count;
                if (count == 0) return;
                committedCount = count;
                // the tail, or all of it, may still be in the write buffer
                var writeBufferOffset = _length - _writeBuffer.Length;
                if (position + count > writeBufferOffset) {
                    var from = Math.Max(position, writeBufferOffset);
                    var n = (int)(position + count - from);
                    _writeBuffer.Position = from - writeBufferOffset;
                    _writeBuffer.ReadExactly(result, (int)(from - position), n);
                    _writeBuffer.Position = _writeBuffer.Length;
                    committedCount = (int)(from - position);
                    if (committedCount == 0) return;
                }
                if (_readBuffer != null && position >= _readBufferOffset && position + committedCount <= _readBufferOffset + _readBufferLength) {
                    Array.Copy(_readBuffer, position - _readBufferOffset, result, 0, committedCount);
                    return;
                }
                committedLength = _committedLength;
            }
            if (committedCount <= _readBufferSize) {
                // a small read fetches a read buffer's worth, as small reads tend to come in sequence
                var lengthToRead = (int)Math.Min(committedLength - position, _readBufferSize);
                var buffer = new byte[lengthToRead];
                _client.DownloadRange(_blobName, position, lengthToRead, buffer);
                _io.ReadThreadSafe(lengthToRead);
                Array.Copy(buffer, 0, result, 0, committedCount);
                lock (_lock) {
                    _readBuffer = buffer;
                    _readBufferOffset = position;
                    _readBufferLength = lengthToRead;
                }
            } else { // too big for the read buffer, straight into the result
                _client.DownloadRange(_blobName, position, committedCount, result);
                _io.ReadThreadSafe(committedCount);
            }
        }
        public void RecordChecksum() => _checkSum.RecordChecksum();
        public void WriteChecksum() => _checkSum.WriteChecksum(this);
        bool _isDisposed = false;
        // the file is given back whether or not the last flush gets through: the stream is finished either
        // way, and a failed flush must not leave the file locked
        public void Dispose() {
            if (_isDisposed) return;
            _isDisposed = true;
            try {
                Flush(true);
            } finally {
                _disposeCallback(_length);
            }
        }

        public async Task AppendAsyncNoChecksumOrLock(byte[] buffer, int count) {
            byte[] data;
            if (count == buffer.Length) {
                data = buffer;
            } else {
                data = new byte[count];
                Array.Copy(buffer, 0, data, 0, count);
            }
            _readBuffer = null; // reset read buffer, as new data is appended, that will not be in readbuffer
            await _writeBuffer.WriteAsync(data, 0, data.Length);
            _length += data.Length;
            _bytesWritten += data.Length;
            _io.Write(data.Length);
            if (_writeBuffer.Length > _maxBufferBeforeFlush) await flushAsync(true);
        }
    }
}
