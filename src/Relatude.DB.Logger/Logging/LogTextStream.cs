using Relatude.DB.IO;
using Relatude.DB.Common;
using System.Text;

namespace Relatude.DB.Logging;
// not threadsafe!
internal class LogTextStream : IDisposable {
    readonly FileInterval _fileInterval;
    readonly string _logName;
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    public LogTextStream(IIOProvider io, string logName, FileInterval fileInterval, FileKeyUtility fileKeys) {
        _io = io;
        _logName = logName;
        _fileInterval = fileInterval;
        _fileKeys = fileKeys;
    }

    public void Record(LogEntry entry, bool flushToDisk = false) {
        var stream = getCorrectStream(entry.Timestamp);
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        foreach (var kv in entry.Values) {
            sb.Append("\t");
            sb.Append((kv.Value + "").ToString().Replace("\t", " ").ReplaceLineEndings("[CR]"));
        }
        sb.AppendLine();
        stream.Append(Encoding.UTF8.GetBytes(sb.ToString()));
        if (flushToDisk) stream.Flush(true);
    }
    IAppendStream? _lastAppendStream;
    IAppendStream getCorrectStream(DateTime timestamp) {
        var floored = timestamp.Floor(_fileInterval);
        var fileKey = _fileKeys.Logger_FileNameTxt(_logName,_fileInterval, floored);
        if (_lastAppendStream == null) {
            _lastAppendStream = _io.OpenAppend(fileKey);
        } else if (_lastAppendStream.FileKey != fileKey) {
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
        return _fileKeys.Logger_FileDatesTxt(_io, _logName, _fileInterval);
    }
    public int DeleteLogFilesBefore(DateTime dt) {
        releaseOpenFiles();
        var filesToDelete = GetLogFileDates().Where(f => dt > f);
        foreach (var fileDt in filesToDelete) {
            _io.DeleteIfItExists(_fileKeys.Logger_FileNameTxt(_logName, _fileInterval, fileDt));
        }
        return filesToDelete.Count();
    }
    public void FlushToDisk() {
        if (_lastAppendStream != null) _lastAppendStream.Flush(true);
    }
    public void Delete(DateTime to) {
        releaseOpenFiles();
        foreach (var f in GetLogFileDates()) {
            var fileTo = f.AddInterval(_fileInterval);
            if (fileTo <= to) _io.DeleteIfItExists(_fileKeys.Logger_FileNameTxt(_logName, _fileInterval, f));
        }
    }
    public void Dispose() {
        releaseOpenFiles();
    }
    internal long Size() {
        return GetLogFileDates().Select(f => _io.GetFileSizeOrZeroIfUnknown(_fileKeys.Logger_FileNameTxt(_logName, _fileInterval, f))).Sum();
    }
}
