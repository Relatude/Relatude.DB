using Relatude.DB.Common;

namespace Relatude.DB.IO;

public class StoreStreamDiscWriteTester : IAppendStream {
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
    public StoreStreamDiscWriteTester(string fileKey, string filePath, bool readOnly, Action disposeCallback) {
        _disposeCallback = disposeCallback;
        _filePath = filePath;
        FileKey = fileKey;
        _readOnly = readOnly;
        var dirPath = Path.GetDirectoryName(_filePath);
        if (dirPath == null) throw new NullReferenceException(nameof(dirPath));
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
        _stream = getStream(_filePath);
    }
    long _bytesRead;
    long _bytesWritten;
    public long GetBytesWritten() => _bytesWritten;
    public long GetBytesRead() => _bytesRead;
    public void ResetByteCounter() {
        _bytesRead = 0;
        _bytesWritten = 0;
    }

    // same exclusive handle, and the same wait for another process to let go of it, as StoreStreamDiscWrite
    static readonly TimeSpan _openTimeout = TimeSpan.FromSeconds(90);
    FileStream getStream(string filePath) {
        return FileOpenRetry.Open(filePath, () => {
            var s = new FileStream(filePath, FileMode.OpenOrCreate, _readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None, 4096 * 10, FileOptions.RandomAccess);
            s.Position = s.Length;
            return s;
        }, _openTimeout);
    }
    public void Append(byte[] data) {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            _checkSum.EvaluateChecksumIfRecording(data);
            _stream.Write(data, 0, data.Length);
            _bytesWritten += data.Length;
            _notInOs = _notDurable = true;
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }
    public void Append(byte[] data, int count) {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            _checkSum.EvaluateChecksumIfRecording(data, count);
            _stream.Write(data, 0, count);
            _bytesWritten += count;
            _notInOs = _notDurable = true;
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }
    // Two states, as in StoreStreamDiscWrite: bytes not handed to the OS yet, and bytes not on the disk yet.
    // A flush without deepFlush clears the first only, so a deep flush after it still reaches the disk.
    // Both start set: what was in the file at open may be in the OS cache only.
    bool _notInOs = true;
    bool _notDurable = true;
    public void Flush(bool deepFlush) {
#if DEBUG
        _flagAccessing.FlagToRun_ThrowIfAlreadyRunning();
        try {
#endif
            if (!(deepFlush ? _notDurable : _notInOs)) return;
            if (_hasDisposed) return;
            if (_stream.CanRead == false) return; // stream is closed
            try {
                _stream.Flush(deepFlush);
            } catch (ObjectDisposedException) {
                return; // stream was closed concurrently, nothing is cleared so failure is not acknowledged as durable
            }
            // IO errors (disk full etc.) must propagate and leave both states set,
            // so a failed flush is never acknowledged as durable
            _notInOs = false;
            if (deepFlush) _notDurable = false;
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
            // count is clamped to what is left in the file above, so all of it must arrive:
            // Stream.Read may return fewer bytes and a partial read would leave stale bytes in the buffer
            _stream.ReadExactly(buffer, 0, count);
            _stream.Position = position1;
            _bytesRead += count;
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
#if DEBUG
        } finally {
            _flagAccessing.Reset();
        }
#endif
    }

    public async Task AppendAsyncNoChecksumOrLock(byte[] buffer, int count) {
        await _stream.WriteAsync(buffer, 0, count);
        _notInOs = _notDurable = true;
    }
}

