using Relatude.DB.Common;

namespace Relatude.DB.IO;
public class StoreStreamDiscWrite : IAppendStream {
    readonly FileStream _stream;
    readonly string _filePath;
    readonly bool _readOnly;
    readonly ChecksumUtil _checkSum = new();
    public string FileKey { get; }
    Action _disposeCallback;
#if DEBUG
    // measure to detect multithreading bugs, only one thread should access an append thread
    OnlyOneThreadRunning _flagAccessing = new();
#endif
    public StoreStreamDiscWrite(string fileKey, string filePath, bool readOnly, Action disposeCallback) {
        _disposeCallback = disposeCallback;
        _filePath = filePath;
        FileKey = fileKey;
        _readOnly = readOnly;
        var dirPath = Path.GetDirectoryName(_filePath);
        if (dirPath == null) throw new NullReferenceException(nameof(dirPath));
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
        _stream = getStream(_filePath);
    }

    const int numberOfRetries = 5;
    FileStream getStream(string filePath) {
        Exception? lastException = null;
        for (int i = 1; i <= numberOfRetries; ++i) {
            try {
                var s = new FileStream(filePath, FileMode.OpenOrCreate, _readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None, 4096 * 10, FileOptions.RandomAccess);
                s.Position = s.Length;
                return s;
            } catch (Exception e) {
                lastException = e;
                var delayOnRetry = i < 3 ? 1000 : 10000; // in total after 5 retries: 1s + 1s + 10s + 10s + 10s = 32s
                Thread.Sleep(delayOnRetry);
            }
        }
        throw new Exception($"Failed to open file {filePath} after {numberOfRetries} attempts. " + lastException?.Message);
    }
    public void Append(byte[] data) {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            _checkSum.EvaluateChecksumIfRecording(data);
            _stream.Write(data, 0, data.Length);
            if (!_unflushed) _unflushed = true;
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }
    bool _unflushed = true;
    public void Flush(bool deepFlush) {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            if (!_unflushed) return;
            if (_hasDisposed) return;
            if (_stream.CanRead == false) return; // stream is closed
            try {
                _stream.Flush(deepFlush);
            } catch {
                // ignore, stream is closed
            }
            _unflushed = false;
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }
    public long Length {
        get {
#if DEBUG
            _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
            try {
#endif
                return _stream.Length;
#if DEBUG
            } finally {
                _flagAccessing.Reset();
            }
#endif
        }
    }
    public void Get(long position, int count, byte[] buffer) {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            var length = _stream.Length;
            if (position < 0 || position >= length) throw new ArgumentOutOfRangeException(nameof(position));
            if (count > length - position) count = (int)(length - position);
            long position1 = this._stream.Position;
            _stream.Position = position;
            _stream.Read(buffer, 0, count);
            _stream.Position = position1;
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }
    public void RecordChecksum() => _checkSum.RecordChecksum();
    public void WriteChecksum() => _checkSum.WriteChecksum(this);

    bool _hasDisposed;
    public void Dispose() {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            if (_hasDisposed) return;
            _hasDisposed = true;
            _stream.Dispose();
            _disposeCallback();
            _unflushed = false;
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }
}

