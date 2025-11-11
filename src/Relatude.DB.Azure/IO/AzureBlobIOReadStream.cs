using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.Diagnostics;
using Relatude.DB.Common;
namespace Relatude.DB.IO;
public class AzureBlobIOReadStream : IReadStream {
    object _lock = new();
    readonly BlobClient _blobClient;
    BlobLeaseClient? _blobLeaseClient;
    ChecksumUtil _checksum = new();
    readonly long _totalLength = 0;
    readonly long _readAheadBufferSize = 1024 * 1024;
    long _bufferStartPos;
    byte[] _readAheadBuffer;// mb read ahead buffer...
    readonly Action _disposeCallback;
    public AzureBlobIOReadStream(BlobContainerClient container, string fileKey, long position, bool lockBlob, Action disposeCallback) {
        FileKey = fileKey;
        _disposeCallback = disposeCallback;
        _blobClient = container.GetBlobClient(fileKey);
        AzureBlobIOProvider.EnsureResetOfLeaseId(container, fileKey);
        if (lockBlob) {
            _blobLeaseClient = _blobClient.GetBlobLeaseClient();
            _blobLeaseClient.Acquire(TimeSpan.FromSeconds(-1));
            AzureBlobIOProvider.SaveLastLeaseId(fileKey, _blobLeaseClient.LeaseId);
        }
        _readAheadBuffer = Array.Empty<byte>();
        _bufferStartPos = 0;
        if (_blobClient.Exists()) _totalLength = _blobClient.GetProperties().Value.ContentLength;
        Position = position;
    }
    public string FileKey { get ; }
    public long Position { get; set; }
    public long Length { get => _totalLength; }
    public bool More() {
        lock (_lock) {
            return Position < _totalLength;
        }
    }
    internal byte[] ReadAll() {
        lock (_lock) {
            Position = 0;
            return Read((int)_totalLength);
        }
    }
    public byte[] Read(int length) {
        lock (_lock) {
            length = (int)Math.Min(length, _totalLength - Position);
            if (Position + length > _readAheadBuffer.Length + _bufferStartPos) {
                var lengthToRead = Math.Max(length, _readAheadBufferSize);
                if (Position + lengthToRead > _totalLength) lengthToRead = _totalLength - Position;
                var conditions = new BlobRequestConditions() { LeaseId = _blobLeaseClient?.LeaseId };
                var options = new BlobDownloadOptions { Range = new HttpRange(Position, lengthToRead), Conditions = conditions };
                var sw = Stopwatch.StartNew();
                _readAheadBuffer = _blobClient.DownloadContent(options).Value.Content.ToArray();
                // _log(" - Reader Downloaded " + _fileKey + " " + lengthToRead.ToTransferString(sw) + " offset:" + Position.To1000N());
                _bufferStartPos = Position;
            }
            byte[] result;
            if (length == _readAheadBuffer.Length) {
                result = _readAheadBuffer;
            } else {
                result = new byte[length];
                Array.Copy(_readAheadBuffer, Position - _bufferStartPos, result, 0, length);
            }
            Position += length;
            _checksum.EvaluateChecksumIfRecording(result);
            return result;
        }
    }
    public void Skip(long length) {
        Position += length;
    }
    public void RecordChecksum() => _checksum.RecordChecksum();
    public void ValidateChecksum() => _checksum.ValidateChecksum(this);
    bool _isDisposed = false;
    public void Dispose() {
        if (_isDisposed) return;
        _isDisposed = true;
        _blobLeaseClient?.Release(); // it can be already released by the caller or deleted
        AzureBlobIOProvider.DeleteLastLeaseId(_blobClient.Name);
        _disposeCallback();
    }
}
