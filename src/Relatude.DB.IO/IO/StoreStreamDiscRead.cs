using Relatude.DB.Common;
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
    // Generous on purpose: this is the path that reads the state file and replays the log, and it has
    // to outlast a backup agent holding the file, not just a process handover.
    static readonly TimeSpan _openTimeout = TimeSpan.FromMinutes(16);
    static FileStream getStream(string filePath) {
        return FileOpenRetry.Open(filePath, () => {
                // SequentialScan hints the OS cache manager to read ahead and evict already-read pages,
                // which speeds up the forward-only bulk reads this stream is used for (state file, log replay,
                // index files). It never affects correctness if the position is moved; it would only reduce
                // read-ahead value for backward/random seeks - which do not occur on this path: random-access
                // node reads use the append stream's Get(), and the seekable AsStream() path unwraps to a plain
                // FileStream (see ReadStreamWrapper.Wrap).
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        }, _openTimeout);
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
    public int ReadInto(Span<byte> buffer) {
        var count = (int)Math.Min(buffer.Length, Length - Position);
        if (count <= 0) return 0;
        var dest = buffer[..count];
        var total = 0;
        while (total < count) {
            var read = _stream.Read(dest[total..]);
            if (read <= 0) break; // unexpected short read; caller refills again
            total += read;
        }
        _checkSum.EvaluateChecksumIfRecording(dest[..total]);
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
