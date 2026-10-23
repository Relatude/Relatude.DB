using Microsoft.Win32.SafeHandles;
using Relatude.DB.Common;
namespace Relatude.DB.IO;
/// <summary>
/// Thread-safe append stream writing to disc storage
/// </summary>
public class StoreStreamDiscWrite : IAppendStream {
    readonly FileStream _stream;
    // The stream's own handle, kept so reads go straight to it at an offset, beside the stream: a read
    // then neither moves the stream's position nor waits for an append or a flush. Opened for overlapped
    // I/O, as on Windows every call on a synchronous handle queues behind the one before it - a read
    // would wait out an fsync, and reads from several threads would run one at a time.
    readonly SafeFileHandle _handle;
    readonly string _filePath;
    readonly bool _readOnly;
    readonly ChecksumUtil _checkSum = new();
    public string FileKey { get; }
    Action _disposeCallback;
    readonly System.Threading.Lock _lock = new();
    long _lastLength;
    // The logical length, kept here so Length never has to ask the file system. This object is the only
    // writer of the file (FileShare.None), so every byte appended goes through Append below.
    long _length;
    // How much of the file the file system holds. Appends wait in the stream's write buffer until it fills
    // or is flushed, and a read at an offset only sees what has reached the file system - so a read past
    // this point pushes the buffer down first. Never more than _length.
    long _osLength;
    // How much of the file has been made durable: flushed with deepFlush, so it is on the disk and not
    // only in the OS cache. Never more than _osLength. A flush without deepFlush moves _osLength only, so a
    // deep flush after it still reaches the disk. Starts below any length, as what was in the file at open
    // may be in the OS cache only (written by a process that never flushed deep), so the first deep flush
    // always goes down. Only touched under _lock.
    long _durableLength = -1;
    public StoreStreamDiscWrite(string fileKey, string filePath, bool readOnly, Action disposeCallback)
        : this(fileKey, filePath, readOnly, disposeCallback, null) { }
    // streamFactory: for tests, a FileStream of their own on the handle opened here, to see which flushes reach it
    internal StoreStreamDiscWrite(string fileKey, string filePath, bool readOnly, Action disposeCallback, Func<SafeFileHandle, FileAccess, FileStream>? streamFactory) {
        _disposeCallback = disposeCallback;
        _filePath = filePath;
        FileKey = fileKey;
        _readOnly = readOnly;
        var dirPath = Path.GetDirectoryName(_filePath);
        if (dirPath == null) throw new NullReferenceException(nameof(dirPath));
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
        _stream = getStream(_filePath, streamFactory, out _handle);
        _lastLength = _stream.Length;
        _length = _lastLength;
        _osLength = _lastLength;
    }
    long _bytesRead;
    long _bytesWritten;
    readonly IOCounter _io = new();
    public IOCounters Counters => _io.Snapshot;
    public long GetBytesWritten() => Interlocked.Read(ref _bytesWritten);
    public long GetBytesRead() => Interlocked.Read(ref _bytesRead);
    public void ResetByteCounter() {
        Interlocked.Exchange(ref _bytesRead, 0);
        Interlocked.Exchange(ref _bytesWritten, 0);
    }
    // This is the handle that makes the database single-writer: FileShare.None on the log file. It is
    // also the first thing a restart contends with, so it waits for the previous process to let go.
    static readonly TimeSpan _openTimeout = TimeSpan.FromSeconds(90);
    FileStream getStream(string filePath, Func<SafeFileHandle, FileAccess, FileStream>? streamFactory, out SafeFileHandle handle) {
        SafeFileHandle? opened = null;
        var stream = FileOpenRetry.Open(filePath, () => {
            var access = _readOnly ? FileAccess.Read : FileAccess.ReadWrite;
            var h = File.OpenHandle(filePath, FileMode.OpenOrCreate, access, FileShare.None, FileOptions.RandomAccess | FileOptions.Asynchronous);
            try {
                var s = streamFactory?.Invoke(h, access) ?? new FileStream(h, access, 4096 * 10, h.IsAsync);
                s.Position = s.Length;
                opened = h;
                return s;
            } catch {
                h.Dispose();
                throw;
            }
        }, _openTimeout);
        handle = opened!;
        return stream;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Append(byte[] data) {
        lock (_lock) {
            _checkSum.EvaluateChecksumIfRecording(data);
            _stream.Write(data, 0, data.Length);
            _bytesWritten += data.Length;
            _io.Write(data.Length);
            Interlocked.Add(ref _length, data.Length);
        }
    }
    public void Append(byte[] data, int count) {
        lock (_lock) {
            _checkSum.EvaluateChecksumIfRecording(data, count);
            _stream.Write(data, 0, count);
            _bytesWritten += count;
            _io.Write(count);
            Interlocked.Add(ref _length, count);
        }
    }
    public async Task AppendAsyncNoChecksumOrLock(byte[] buffer, int count) {
        await _stream.WriteAsync(buffer, 0, count);
        _io.Write(count);
        Interlocked.Add(ref _length, count); // no lock by contract: the caller is the only writer while this runs
    }
    // Skipped when everything appended has already had the flush asked for: in the OS for a plain flush,
    // on the disk for a deep one. A deep flush covers a plain one, never the other way round.
    public void Flush(bool deepFlush) {
        lock (_lock) {
            var length = Interlocked.Read(ref _length);
            if (length <= (deepFlush ? _durableLength : Interlocked.Read(ref _osLength))) return;
            if (_hasDisposed) return;
            if (_stream.CanRead == false) return; // stream is closed
            try {
                _stream.Flush(deepFlush);
            } catch (ObjectDisposedException) {
                return; // stream was closed/disposed concurrently, neither mark moves so failure is not acknowledged as durable
            }
            // IO errors (disk full etc.) must propagate and leave both marks where they were,
            // so a failed flush is never acknowledged as durable
            Interlocked.Exchange(ref _osLength, length);
            if (deepFlush) _durableLength = length;
        }
    }
    public long Length => Interlocked.Read(ref _length);
    // No lock: everything below _osLength is in the file system and never changes again (the file only
    // grows), so a read at an offset on the handle can run beside appends, flushes and other reads.
    public void Get(long position, int count, byte[] buffer) {
        var length = Interlocked.Read(ref _length);
        if (position < 0 || position >= length) throw new ArgumentOutOfRangeException(nameof(position));
        if (count > length - position) throw new EndOfStreamException("Attempt to read " + count + " bytes at position " + position + " which is past the end of file \"" + _filePath + "\" (" + length + " bytes). ");
        if (count > buffer.Length) throw new ArgumentException("The buffer holds " + buffer.Length + " bytes, " + count + " were asked for. ", nameof(buffer));
        if (position + count > Interlocked.Read(ref _osLength)) pushWritesToFileSystem();
        var target = buffer.AsSpan(0, count);
        var totalRead = 0;
        while (totalRead < count) { // a read may return fewer bytes than requested, a partial read would leave stale bytes in the buffer
            var read = RandomAccess.Read(_handle, target[totalRead..], position + totalRead);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of file \"" + _filePath + "\" while reading " + count + " bytes at position " + position + ". ");
            totalRead += read;
        }
        Interlocked.Add(ref _bytesRead, count);
        _io.ReadThreadSafe(count);
    }
    // the stream's write buffer down to the file system (no fsync), so a read at an offset sees all of it
    void pushWritesToFileSystem() {
        lock (_lock) {
            if (Interlocked.Read(ref _osLength) >= _length) return; // another reader or a flush got there first
            _stream.Flush(false);
            Interlocked.Exchange(ref _osLength, _length);
        }
    }
    public void RecordChecksum() {
        lock (_lock) {
            _checkSum.RecordChecksum();
        }
    }
    public void WriteChecksum() {
        lock (_lock) {
            _checkSum.WriteChecksum(this);
        }
    }
    bool _hasDisposed;
    public void Dispose() {
        try {
            if (_stream.CanRead) _lastLength = _stream.Length;
        } catch { }
        if (_hasDisposed) return;
        _hasDisposed = true;
        _stream.Dispose();
        _disposeCallback();
    }
}
