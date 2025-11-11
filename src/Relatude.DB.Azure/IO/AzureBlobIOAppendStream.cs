using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.Diagnostics;
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
        readonly AppendBlobClient _appendBlobClient;
        readonly BlobLeaseClient? _blobLeaseClient;
        readonly Action<long> _disposeCallback;
        MemoryStream _writeBuffer;
        long _maxBufferBeforeFlush = 1024 * 1024 * 2; //2 mb
        int _readBufferSize = 1024 * 50 * 2; // 50KB
        long _readBufferOffset = 0;
        byte[]? _readBuffer; // 50KB
        object _lock = new();
        ChecksumUtil _checkSum = new();
        public string FileKey { get; }
        public AzureBlobIOAppendStream(BlobContainerClient container, string blobName, string fileKey, bool lockBlob, Action<long> disposeCallback) {
            _disposeCallback = disposeCallback;
            FileKey = fileKey;
            _appendBlobClient = container.GetAppendBlobClient(blobName);
            AzureBlobIOProvider.EnsureResetOfLeaseId(container, blobName);
            if (lockBlob) _blobLeaseClient = _appendBlobClient.GetBlobLeaseClient();
            var conditions = new AppendBlobRequestConditions() { LeaseId = _blobLeaseClient?.LeaseId };
            var options = new AppendBlobCreateOptions() { Conditions = conditions };
            _appendBlobClient.CreateIfNotExists(options);
            _blobLeaseClient?.Acquire(TimeSpan.FromSeconds(-1));
            if (_blobLeaseClient != null) AzureBlobIOProvider.SaveLastLeaseId(blobName, _blobLeaseClient.LeaseId);
            _writeBuffer = new MemoryStream();
            _length = _appendBlobClient.GetProperties().Value.ContentLength;
        }
        long _length = 0;
        public long Length {
            get {
                lock (_lock) {
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
                if (_writeBuffer.Length > _maxBufferBeforeFlush) Flush(true);
            }
        }
        public void Flush(bool deepFlush) {
            lock (_lock) {
                if (_writeBuffer.Length == 0) return;
                _writeBuffer.Position = 0;
                var conditions = new AppendBlobRequestConditions() { LeaseId = _blobLeaseClient?.LeaseId };
                var options = new AppendBlobAppendBlockOptions() { Conditions = conditions };
                if (_writeBuffer.Length < _maxBufferBeforeFlush) {
                    Stopwatch sw = Stopwatch.StartNew();
                    _appendBlobClient.AppendBlock(_writeBuffer, options);
                    // _log("Uploaded " + FileKey + " " + _writeBuffer.Length.ToTransferString(sw));
                } else {
                    var segment = new byte[_maxBufferBeforeFlush];
                    var segmengStream = new MemoryStream((int)_maxBufferBeforeFlush);
                    while (true) {
                        var read = _writeBuffer.Read(segment, 0, segment.Length);
                        if (read == 0) break;
                        segmengStream.Position = 0;
                        segmengStream.Write(segment, 0, read);
                        segmengStream.Position = 0;
                        segmengStream.SetLength(read);
                        Stopwatch sw = Stopwatch.StartNew();
                        _appendBlobClient.AppendBlock(segmengStream, options);
                        // _log("Uploaded " + FileKey + " " + _writeBuffer.Length.ToTransferString(sw));
                    }
                }
                _writeBuffer = new MemoryStream();
            }
        }
        public void Get(long position, int count, byte[] result) {
            lock (_lock) {

                if (count > _length - position) throw new Exception("Read beyond end of file");

                // Try using write buffer
                var writeBufferOffset = _length - _writeBuffer.Length;
                var inWriteBuffer = position >= writeBufferOffset;
                if (inWriteBuffer) {
                    var bufferOffset = position - writeBufferOffset;
                    _writeBuffer.Position = bufferOffset;
                    _writeBuffer.Read(result, 0, count);
                    _writeBuffer.Position = _writeBuffer.Length;
                    return;
                }

                // Try using read buffer
                if (_readBuffer != null) {
                    var inReadBuffer = position >= _readBufferOffset && position + count <= _readBufferOffset + _readBuffer.Length;
                    if (inReadBuffer) { 
                        Array.Copy(_readBuffer, position - _readBufferOffset, result, 0, count);
                        return;
                    }
                }

                // Download from blob:
                var fitsInReadBuffer = count <= _readBufferSize;
                var conditions = new BlobRequestConditions() { LeaseId = _blobLeaseClient?.LeaseId };
                var sw = Stopwatch.StartNew();
                if (fitsInReadBuffer) { 
                    _readBufferOffset = position;
                    var lengthToRead = (int)Math.Min(this._length - position, _readBufferSize);
                    if (_readBuffer == null) _readBuffer = new byte[_readBufferSize];
                    download(_readBuffer, position, lengthToRead);
                    Array.Copy(_readBuffer, 0, result, 0, count);
                } else { // too big, download directly. No point in readbuffer
                    download(result, position, count);
                }
            }
        }
        void download(byte[] buffer, long offset, int length) {
            var sw = Stopwatch.StartNew();
            var options = new BlobDownloadOptions { Range = new HttpRange(offset, length), Conditions = new BlobRequestConditions() { LeaseId = _blobLeaseClient?.LeaseId } };
            var binaryData = _appendBlobClient.DownloadContent(options).Value.Content;
            var stream = binaryData.ToStream();
            stream.Read(buffer, 0, length);
            // _log(" - Appender Downloaded " + FileKey + " " + length.ToTransferString(sw)+ " offset:" + offset.To1000N());
        }
        public void RecordChecksum() => _checkSum.RecordChecksum();
        public void WriteChecksum() => _checkSum.WriteChecksum(this);
        bool _isDisposed = false;
        public void Dispose() {
            if (_isDisposed) return;
            _isDisposed = true;
            Flush(true);
            _blobLeaseClient?.Release();
            _disposeCallback(_length);
            AzureBlobIOProvider.DeleteLastLeaseId(_appendBlobClient.Name);
        }
    }
}
