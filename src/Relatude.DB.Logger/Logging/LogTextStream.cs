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

    public void Record(LogEntry entry, bool flushToDisk = false) {
        var stream = getCorrectStream(entry.Timestamp);
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        foreach (var kv in entry.Values) {
            sb.Append('\t');
            sb.Append((kv.Value + string.Empty).Replace("\t", " ").ReplaceLineEndings("[CR]"));
        }
        sb.AppendLine();
        stream.Append(Encoding.UTF8.GetBytes(sb.ToString()));
        if (flushToDisk) stream.Flush(true);
    }
    IAppendStream? _lastAppendStream;
    IAppendStream getCorrectStream(DateTime timestamp) {
        var floored = timestamp.Floor(_fileInterval);
        var fileKey = FileKeyUtility.Logger_FileNameTxt(_logName, _fileInterval, floored);
        if (_lastAppendStream == null) {
            _lastAppendStream = _io.OpenAppend(fileKey);
        } else if (_lastAppendStream.FileKey != fileKey.AsKeyString()) {
            _lastAppendStream.Dispose();
            _lastAppendStream = _io.OpenAppend(fileKey);
        }
        return _lastAppendStream;
    }
    void releaseOpenFiles() {
        if (_lastAppendStream != null) {
            _lastAppendStream.Dispose();
            _lastAppendStream = null;
        }
    }
    public List<DateTime> GetLogFileDates() {
        return FileKeyUtility.Logger_FileDatesTxt(_io, _logName, _fileInterval);
    }
    public void FlushToDisk() {
        if (_lastAppendStream != null) _lastAppendStream.Flush(true);
    }
    public void Delete(DateTime to) {
        releaseOpenFiles();
        foreach (var f in GetLogFileDates()) {
            var fileTo = f.AddInterval(_fileInterval);
            if (fileTo <= to) _io.DeleteFileIfItExists(FileKeyUtility.Logger_FileNameTxt(_logName, _fileInterval, f));
        }
    }
    public void Dispose() {
        releaseOpenFiles();
    }
    internal long Size() {
        return GetLogFileDates().Select(f => _io.GetFileSizeOrZeroIfUnknown(FileKeyUtility.Logger_FileNameTxt(_logName, _fileInterval, f))).Sum();
    }
}
