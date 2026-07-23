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
    public IReadStream InnerStream => _innerStream;
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
        _bytesRead += totalBytesRead;

        return result;

    }

    public int ReadInto(Span<byte> dest) {
        int total = 0;
        while (total < dest.Length) {
            if (_bufferOffset >= _bufferLength) {
                if (!fillBuffer()) break;
            }
            int n = Math.Min(dest.Length - total, _bufferLength - _bufferOffset);
            _buffer.AsSpan(_bufferOffset, n).CopyTo(dest[total..]);
            _bufferOffset += n;
            total += n;
        }
        _checkSum.EvaluateChecksumIfRecording(dest[..total]);
        _bytesRead += total;
        return total;
    }

    public async Task<int> ReadAsync(byte[] buffer, int count) {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return 0;
        int totalBytesRead = 0;
        while (totalBytesRead < count) {
            // If buffer is empty, refill it
            if (_bufferOffset >= _bufferLength) {
                if (!fillBuffer()) break;
            }
            int bytesToCopy = Math.Min(count - totalBytesRead, _bufferLength - _bufferOffset);
            Buffer.BlockCopy(_buffer, _bufferOffset, buffer, totalBytesRead, bytesToCopy);
            _bufferOffset += bytesToCopy;
            totalBytesRead += bytesToCopy;
        }
        _checkSum.EvaluateChecksumIfRecording(buffer.AsSpan(0, totalBytesRead).ToArray());
        _bytesRead += totalBytesRead;
        return totalBytesRead;
    }

    private bool fillBuffer() {
        if (!_innerStream.More()) return false;
        int read = _innerStream.ReadInto(_buffer);
        if (read <= 0) return false;
        _bufferLength = read;
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