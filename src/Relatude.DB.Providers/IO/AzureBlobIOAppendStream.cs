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
        readonly AzureBlobIOProvider _provider;
        readonly AzureBlobRestClient _client;
        readonly string _blobName;
        // the provider's lease on the blob, shared with any other stream it has open on it; null without lockBlob
        readonly AzureBlobIOProvider.HeldLease? _lease;
        readonly Action<long> _disposeCallback;
        MemoryStream _writeBuffer;
        long _committedLength; // length of the blob on the server, always _length minus what is in the write buffer
        long _maxBufferBeforeFlush = 1024 * 1024 * 20; // 20 mb
        int _readBufferSize = 1024 * 50 * 2; // 100KB
        long _readBufferOffset = 0;
        byte[]? _readBuffer; // 100KB
        readonly object _lock = new();
        ChecksumUtil _checkSum = new();
        public string FileKey { get; }
        // the blob exists and is leased by the time this runs: the provider does both, and gives the lease
        // back when this throws
        internal AzureBlobIOAppendStream(AzureBlobIOProvider provider, AzureBlobRestClient client, string blobName, AzureBlobIOProvider.HeldLease? lease, Action<long> disposeCallback) {
            _disposeCallback = disposeCallback;
            FileKey = blobName;
            _provider = provider;
            _client = client;
            _blobName = blobName;
            _lease = lease;
            _writeBuffer = new MemoryStream();
            _committedLength = _client.GetProperties(blobName)?.ContentLength ?? 0;
            _length = _committedLength;
        }
        long _bytesRead;
        long _bytesWritten;
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
        // An append on a leased blob fails with a 412 once the lease is not ours any more. When it merely
        // expired - nobody wrote or leased the blob since - renewing it brings it back and the append is
        // made again; otherwise another process may be writing the blob, and this one must stop.
        void appendBlock(byte[] data, int count) {
            try {
                _client.AppendBlock(_blobName, data, count, _lease?.LeaseId, _committedLength);
            } catch (AzureBlobRequestException err) when (isLeaseFailure(err)) {
                if (!_provider.TryRecoverLease(_lease!)) throw leaseLost(err);
                _client.AppendBlock(_blobName, data, count, _lease!.LeaseId, _committedLength);
            }
            _committedLength += count;
        }
        async Task appendBlockAsync(byte[] data, int count) {
            try {
                await _client.AppendBlockAsync(_blobName, data, count, _lease?.LeaseId, _committedLength);
            } catch (AzureBlobRequestException err) when (isLeaseFailure(err)) {
                if (!_provider.TryRecoverLease(_lease!)) throw leaseLost(err);
                await _client.AppendBlockAsync(_blobName, data, count, _lease!.LeaseId, _committedLength);
            }
            _committedLength += count;
        }
        bool isLeaseFailure(AzureBlobRequestException err) => _lease != null && err.StatusCode == 412
            && err.ErrorCode != null && err.ErrorCode.StartsWith("Lease", StringComparison.Ordinal);
        IOException leaseLost(AzureBlobRequestException err) => new IOException("The lease on \"" + _blobName + "\" is no longer this process's"
            + (_lease?.LostReason is string reason ? " (" + reason.TrimEnd() + ")" : "")
            + ", so nothing more is written to it: another process may be writing it now. The database has to be opened again. " + err.Message, err);
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
        public void Get(long position, int count, byte[] result) {
            lock (_lock) {
                if (count > _length - position) throw new Exception("Read beyond end of file");
                _bytesRead += count;
                get(position, count, result, 0);
            }
        }
        void get(long position, int count, byte[] result, int resultOffset) {
            if (count == 0) return;

            // Entirely in write buffer
            var writeBufferOffset = _length - _writeBuffer.Length;
            if (position >= writeBufferOffset) {
                var bufferOffset = position - writeBufferOffset;
                _writeBuffer.Position = bufferOffset;
                _writeBuffer.Read(result, resultOffset, count);
                _writeBuffer.Position = _writeBuffer.Length;
                return;
            }

            // Spanning flushed data and the write buffer: split at the boundary
            if (position + count > writeBufferOffset) {
                var flushedCount = (int)(writeBufferOffset - position);
                get(position, flushedCount, result, resultOffset);
                get(writeBufferOffset, count - flushedCount, result, resultOffset + flushedCount);
                return;
            }

            // Try using read buffer
            if (_readBuffer != null) {
                var inReadBuffer = position >= _readBufferOffset && position + count <= _readBufferOffset + _readBuffer.Length;
                if (inReadBuffer) {
                    Array.Copy(_readBuffer, position - _readBufferOffset, result, resultOffset, count);
                    return;
                }
            }

            // Download from blob:
            var fitsInReadBuffer = count <= _readBufferSize;
            if (fitsInReadBuffer) {
                _readBufferOffset = position;
                var lengthToRead = (int)Math.Min(_committedLength - position, _readBufferSize);
                if (_readBuffer == null) _readBuffer = new byte[_readBufferSize];
                _client.DownloadRange(_blobName, position, lengthToRead, null, _readBuffer); // a lease guards writes, reading needs none
                Array.Copy(_readBuffer, 0, result, resultOffset, count);
            } else { // too big, download directly. No point in readbuffer
                var block = new byte[count];
                _client.DownloadRange(_blobName, position, count, null, block);
                Array.Copy(block, 0, result, resultOffset, count);
            }
        }
        public void RecordChecksum() => _checkSum.RecordChecksum();
        public void WriteChecksum() => _checkSum.WriteChecksum(this);
        bool _isDisposed = false;
        // the lease and the provider's count are given back whether or not the last flush gets through:
        // the stream is finished either way, and a failed flush must not leave the blob locked
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
            if (_writeBuffer.Length > _maxBufferBeforeFlush) await flushAsync(true);
        }
    }
}
