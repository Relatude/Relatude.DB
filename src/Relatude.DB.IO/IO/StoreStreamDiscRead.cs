namespace Relatude.DB.IO;

public class StoreStreamDiscRead : IReadStream {
    protected FileStream _stream;
    string _filePath;
    ChecksumUtil _checkSum = new();
    Action _disposeCallback;
    public StoreStreamDiscRead(string filePath, long position, Action disposeCallback) {
        _disposeCallback = disposeCallback;
        _filePath = filePath;
        var dirPath = Path.GetDirectoryName(_filePath);
        if (dirPath == null) throw new NullReferenceException(nameof(dirPath));
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
        _stream = getStream(_filePath);
        _stream.Position = position;
    }
    long _bytesRead;
    public long GetBytesRead() => _bytesRead;
    public void ResetByteCounter() => _bytesRead = 0;
    public string FileKey => Path.GetFileName(_filePath);
    const int numberOfRetries = 5;
    static FileStream getStream(string filePath) {
        Exception? lastException = null;
        for (int i = 1; i <= numberOfRetries; ++i) {
            try {
                return new(filePath, FileMode.OpenOrCreate, FileAccess.Read);
            } catch (Exception e) {
                lastException = e;
                var delayOnRetry = i < 3 ? 1000 : 10000; // in total after 5 retries: 1s + 1s + 10s + 10s + 10s = 32s
                Thread.Sleep(delayOnRetry);
            }
        }
        throw new Exception($"Failed to open file {filePath} after {numberOfRetries} attempts. " + lastException?.Message);
    }

    public long Position { get => _stream.Position; set => _stream.Position = value; }
    public byte[] Read(int length) {
        length = (int)Math.Min(length, Length - Position);
        if (length == 0) return [];
        var block = new byte[length];
        if (_stream.Read(block, 0, length) != length) throw new Exception("Read error");
        _checkSum.EvaluateChecksumIfRecording(block);
        _bytesRead += length;
        return block;
    }
    public long Length => _stream.Length;
    public bool More() {
        return Position < _stream.Length;
    }
    public void Skip(long length) {
        _stream.Seek(length, SeekOrigin.Current);
    }
    public void RecordChecksum() => _checkSum.RecordChecksum();
    public void ValidateChecksum() => _checkSum.ValidateChecksum(this);
    bool _hasDisposed;
    public void Dispose() {
        if (_hasDisposed) return;
        _stream.Dispose();
        _disposeCallback();
        _hasDisposed = true;
    }
}




public class BufferedStreamRead : IReadStream {
    readonly IReadStream _innerStream;
    readonly byte[] _buffer;
    int _bufferOffset = 0;
    int _bufferLength = 0;
    bool _isDisposed;
    long _bytesRead;
    ChecksumUtil _checkSum = new();
    public BufferedStreamRead(IReadStream readStream, int maxBufferSize) {
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
        if (length <= 0) return [];

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
