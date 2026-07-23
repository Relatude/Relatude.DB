namespace Relatude.DB.IO;

public class StoreStreamDiscRead : IReadStream {
    protected FileStream _stream;
    string _filePath;
    ChecksumUtil _checkSum = new();
    Action _disposeCallback;
    public string InnerFilePath => _filePath;
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
    public Task<int> ReadAsync(byte[] buffer, int count) {
        return _stream.ReadAsync(buffer, 0, count);
    }
    public long GetBytesRead() => _bytesRead;
    public void ResetByteCounter() => _bytesRead = 0;
    public string FileKey => Path.GetFileName(_filePath);
    const int numberOfRetries = 100;
    static FileStream getStream(string filePath) {
        Exception? lastException = null;
        for (int i = 1; i <= numberOfRetries; ++i) {
            try {
                // SequentialScan hints the OS cache manager to read ahead and evict already-read pages,
                // which speeds up the forward-only bulk reads this stream is used for (state file, log replay,
                // index files). It never affects correctness if the position is moved; it would only reduce
                // read-ahead value for backward/random seeks - which do not occur on this path: random-access
                // node reads use the append stream's Get(), and the seekable AsStream() path unwraps to a plain
                // FileStream (see ReadStreamWrapper.Wrap).
                return new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            } catch (FileNotFoundException) {
                throw; // No need to retry if the file is not found
            } catch (IOException) {
                var delayOnRetry = i < 3 ? 1000 : 10000; // in total after 100 retries: 1s + 1s + 10s * 98 = 16 minutes, leaving time for azure lock on files during restarts and backups
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
    // Reads up to 'count' bytes into the start of the caller's buffer without allocating a new array.
    // Returns the number actually read (may be less than requested near end of file). Used by
    // StoreStreamBufferedRead to refill its buffer without a per-fill allocation.
    public int ReadInto(byte[] buffer, int count) {
        count = (int)Math.Min(count, Length - Position);
        if (count <= 0) return 0;
        var total = 0;
        while (total < count) {
            var read = _stream.Read(buffer, total, count - total);
            if (read <= 0) break; // unexpected short read; caller refills again
            total += read;
        }
        _checkSum.EvaluateChecksumIfRecording(buffer, total);
        _bytesRead += total;
        return total;
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
