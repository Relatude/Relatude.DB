namespace Relatude.DB.IO;
public class StoreStreamBufferedRead : IReadStream {
    readonly IReadStream _innerStream;
    readonly byte[] _buffer;
    int _bufferOffset = 0;
    int _bufferLength = 0;
    bool _isDisposed;
    long _bytesRead;
    ChecksumUtil _checkSum = new();
    public StoreStreamBufferedRead(IReadStream readStream, int maxBufferSize) {
        _innerStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        _buffer = new byte[maxBufferSize];
    }
    public bool More() {
        // If we have data in the buffer, we definitely have more
        if (_bufferOffset < _bufferLength) return true;
        // Otherwise, check the underlying stream
        return _innerStream.More();
    }
    public byte[] Read(int length) {
        if (length <= 0) return Array.Empty<byte>();

        byte[] result = new byte[length];
        int totalBytesRead = 0;

        while (totalBytesRead < length) {
            // If buffer is empty, refill it
            if (_bufferOffset >= _bufferLength) {
                if (!fillBuffer()) break;
            }
            int bytesToCopy = Math.Min(length - totalBytesRead, _bufferLength - _bufferOffset);
            Buffer.BlockCopy(_buffer, _bufferOffset, result, totalBytesRead, bytesToCopy);
            _bufferOffset += bytesToCopy;
            totalBytesRead += bytesToCopy;
        }

        // If we couldn't fulfill the whole request, resize the array
        if (totalBytesRead < length) {
            Array.Resize(ref result, totalBytesRead);
        }

        _checkSum.EvaluateChecksumIfRecording(result);
        _bytesRead += length;

        return result;

    }

    private bool fillBuffer() {
        if (!_innerStream.More()) return false;

        // Note: Since IReadStream.Read returns an array rather than taking a buffer,
        // we have to handle the allocation/copying here.
        byte[] rawData = _innerStream.Read(_buffer.Length);

        if (rawData == null || rawData.Length == 0) return false;

        Buffer.BlockCopy(rawData, 0, _buffer, 0, rawData.Length);
        _bufferLength = rawData.Length;
        _bufferOffset = 0;
        return true;
    }

    public void Skip(long length) {
        if (length <= 0) return;

        long remainingInRepo = _bufferLength - _bufferOffset;

        if (length <= remainingInRepo) {
            _bufferOffset += (int)length;
        } else {
            // Clear buffer and skip the remainder in the inner stream
            long remainingToSkip = length - remainingInRepo;
            _bufferOffset = 0;
            _bufferLength = 0;
            _innerStream.Skip(remainingToSkip);
        }
    }

    public long Position {
        get => _innerStream.Position - (_bufferLength - _bufferOffset);
        set {
            // Invalidate buffer if position is moved manually
            _innerStream.Position = value;
            _bufferOffset = 0;
            _bufferLength = 0;
        }
    }

    public string FileKey => _innerStream.FileKey;
    public long Length => _innerStream.Length;
    public void RecordChecksum() => _checkSum.RecordChecksum();
    public void ValidateChecksum() => _checkSum.ValidateChecksum(this);
    public long GetBytesRead() => _bytesRead;
    public void ResetByteCounter() => _bytesRead = 0;

    public void Dispose() {
        if (!_isDisposed) {
            _innerStream.Dispose();
            _isDisposed = true;
        }
    }
}