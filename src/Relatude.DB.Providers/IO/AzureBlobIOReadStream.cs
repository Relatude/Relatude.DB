namespace Relatude.DB.IO;
public class AzureBlobIOReadStream : IReadStream {
    readonly AzureBlobRestClient _client;
    readonly string _blobName;
    ChecksumUtil _checksum = new();
    readonly long _totalLength = 0;
    readonly long _readAheadBufferSize = 1024 * 1024; // 1 mb read ahead buffer
    long _bufferStartPos;
    byte[] _readAheadBuffer; // mb read ahead buffer...
    readonly Action _disposeCallback;
    long _bytesRead;
    public long GetBytesRead() => _bytesRead;
    public void ResetByteCounter() {
        _bytesRead = 0;
    }
    // the provider reads the properties and takes the lease (with lockBlob) before this runs, and lets
    // go of the lease in the dispose callback: the lease is shared with its other streams on the blob,
    // and reading needs none, so the downloads go without one
    internal AzureBlobIOReadStream(AzureBlobRestClient client, string fileKey, long position, BlobProperties? properties, Action disposeCallback) {
        FileKey = fileKey;
        _disposeCallback = disposeCallback;
        _client = client;
        _blobName = fileKey;
        _readAheadBuffer = Array.Empty<byte>();
        _bufferStartPos = 0;
        if (properties != null) _totalLength = properties.ContentLength;
        if (_readAheadBufferSize > _totalLength) _readAheadBufferSize = _totalLength;
        Position = position;
    }
    public string FileKey { get; }
    public long Position { get; set; }
    public long Length { get => _totalLength; }
    public bool More() {
        return Position < _totalLength;
    }
    public byte[] Read(int length) {
        length = (int)Math.Min(length, _totalLength - Position);
        if (Position + length > _readAheadBuffer.Length + _bufferStartPos) {
            var lengthToRead = Math.Max(length, _readAheadBufferSize);
            if (Position + lengthToRead > _totalLength) lengthToRead = _totalLength - Position;
            _readAheadBuffer = new byte[lengthToRead];
            _client.DownloadRange(_blobName, Position, (int)lengthToRead, null, _readAheadBuffer);
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
        _bytesRead += length;
        return result;
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
        _disposeCallback();
    }

    public async Task<int> ReadAsync(byte[] buffer, int count) {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return 0;

        // Clamp requested size to remaining bytes in the blob.
        count = (int)Math.Min(count, _totalLength - Position);
        if (count <= 0) return 0;

        // Refill read-ahead buffer if position moved outside current window
        // or if requested bytes exceed what is currently buffered.
        if (Position < _bufferStartPos || Position + count > _readAheadBuffer.Length + _bufferStartPos) {
            var lengthToRead = Math.Max((long)count, _readAheadBufferSize);
            if (Position + lengthToRead > _totalLength) lengthToRead = _totalLength - Position;
            _readAheadBuffer = new byte[lengthToRead];
            await _client.DownloadRangeAsync(_blobName, Position, (int)lengthToRead, null, _readAheadBuffer);
            _bufferStartPos = Position;
        }

        // Copy from internal read-ahead buffer into caller-provided buffer.
        Buffer.BlockCopy(_readAheadBuffer, (int)(Position - _bufferStartPos), buffer, 0, count);
        Position += count;
        _checksum.EvaluateChecksumIfRecording(buffer.AsSpan(0, count).ToArray());
        _bytesRead += count;
        return count;
    }
}
