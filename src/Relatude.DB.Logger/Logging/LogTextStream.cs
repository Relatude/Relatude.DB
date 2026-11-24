using Relatude.DB.IO;
using Relatude.DB.Common;
using System.Text;

namespace Relatude.DB.Logging;
// not threadsafe!
internal class LogTextStream : IDisposable {
    readonly FileInterval _fileInterval;
    readonly string _logName;
    readonly IIOProvider _io;
    public LogTextStream(IIOProvider io, string logName, FileInterval fileInterval) {
        _io = io;
        _logName = logName;
        _fileInterval = fileInterval;
    }

    // the text copy is a convenience beside the binary file: a line it cannot write is let go of rather than
    // waited for, and the file is tried again after a pause that doubles up to the last
    int _failures;
    long _retryAt;
    volatile LogWriteProblem? _problem;
    long _dropped;
    public LogWriteProblem? Problem => _problem;
    public void Record(LogEntry entry, bool flushToDisk = false) {
        if (_failures > 0 && Environment.TickCount64 < _retryAt) {
            Interlocked.Increment(ref _dropped);
            return;
        }
        try {
            write(entry, flushToDisk);
            if (_failures > 0) {
                _failures = 0;
                _problem = null;
            }
        } catch (Exception err) {
            failed(err);
        }
    }
    void failed(Exception err) {
        try { releaseOpenFiles(); } catch { }
        _failures++;
        _retryAt = Environment.TickCount64 + Math.Min(30_000, 1000 << Math.Min(_failures - 1, 5));
        Interlocked.Increment(ref _dropped);
        if (_problem == null) FileOpenRetry.DefaultLog?.Invoke("The text file of the log \"" + _logName + "\" could not be written, and lines are left out of it until it can: " + err.Message);
        _problem = new(err.Message, _problem?.SinceUtc ?? DateTime.UtcNow, Interlocked.Read(ref _dropped));
    }
    void write(LogEntry entry, bool flushToDisk) {
        var stream = getCorrectStream(entry.Timestamp);
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        foreach (var kv in entry.Values) {
            sb.Append('\t');
            // a position as the two numbers it is, to the store's own centimetre
            var text = kv.Value is GeoCoordinate position ? LogValues.PositionText(position, 7) : kv.Value + string.Empty;
            sb.Append(text.Replace("\t", " ").ReplaceLineEndings("[CR]"));
        }
        sb.AppendLine();
        stream.Append(Encoding.UTF8.GetBytes(sb.ToString()));
        if (flushToDisk) stream.Flush(true);
    }
    IAppendStream? _lastAppendStream;
    // the interval the open file is the file of: an entry inside it goes to the same file without
    // the file's name being worked out again
    DateTime _openFrom = DateTime.MaxValue;
    DateTime _openUntil = DateTime.MinValue;
    IAppendStream getCorrectStream(DateTime timestamp) {
        if (_lastAppendStream != null && timestamp >= _openFrom && timestamp < _openUntil) return _lastAppendStream;
        var floored = timestamp.Floor(_fileInterval);
        var fileKey = FileKeyUtility.Logger_FileNameTxt(_logName, _fileInterval, floored);
        if (_lastAppendStream != null && _lastAppendStream.FileKey != fileKey.AsKeyString()) releaseOpenFiles();
        _lastAppendStream ??= _io.OpenAppend(fileKey, TimeSpan.Zero);
        _openFrom = floored;
        _openUntil = floored.EndOfInterval(_fileInterval);
        return _lastAppendStream;
    }
    internal void ReleaseFiles() {
        try {
            releaseOpenFiles();
        } catch (Exception err) {
            failed(err);
        }
    }
    void releaseOpenFiles() {
        var stream = _lastAppendStream;
        _lastAppendStream = null;
        stream?.Dispose();
    }
    public List<DateTime> GetLogFileDates() {
        return FileKeyUtility.Logger_FileDatesTxt(_io, _logName, _fileInterval);
    }
    public void FlushToDisk() {
        try {
            _lastAppendStream?.Flush(true);
        } catch (Exception err) {
            failed(err);
        }
    }
    public void Delete(DateTime to) {
        ReleaseFiles();
        foreach (var f in GetLogFileDates()) {
            var fileTo = f.AddInterval(_fileInterval);
            if (fileTo <= to) _io.DeleteFileIfItExists(FileKeyUtility.Logger_FileNameTxt(_logName, _fileInterval, f));
        }
    }
    public void Dispose() {
        ReleaseFiles();
    }
    internal long Size() {
        return GetLogFileDates().Select(f => _io.GetFileSizeOrZeroIfUnknown(FileKeyUtility.Logger_FileNameTxt(_logName, _fileInterval, f))).Sum();
    }
}
