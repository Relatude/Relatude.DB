namespace WAF.IO;
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