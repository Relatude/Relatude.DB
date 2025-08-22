using WAF.IO;
using WAF.Common;
using System.Text;
using WAF.LogSystem;

namespace WAF.LogSystem;
// not threadsafe!
internal class LogTextStream : IDisposable {
    FileResolution _res;
    string _logName;
    IIOProvider _io;
    public LogTextStream(IIOProvider io, string logName, FileResolution fileResolution) {
        _io = io;
        _logName = logName;
        _res = fileResolution;
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
        if (flushToDisk) stream.Flush();
    }
    IAppendStream? _lastAppendStream;
    IAppendStream getCorrectStream(DateTime timestamp) {
        var floored = timestamp.Floor(_res);
        var fileKey = getStreamFileName(floored);
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
        return _io.Search(getStreamPrefix() + "*").Select(f => parseFileName(f)).OrderBy(i => i).ToList();
    }
    public int DeleteLogFilesBefore(DateTime dt) {
        releaseOpenFiles();
        var filesToDelete = GetLogFileDates().Where(f => dt > f);
        foreach (var fileDt in filesToDelete) {
            _io.DeleteIfItExists(getStreamFileName(fileDt));
        }
        return filesToDelete.Count();
    }
    string getStreamPrefix() => "log_" + _logName + "_txt_" + _res + "_";
    string getStreamFileName(DateTime floored) {
        return (getStreamPrefix() + _res switch {
            FileResolution.Minute => floored.ToString("yyyy-MM-dd-HH-mm"),
            FileResolution.Hour => floored.ToString("yyyy-MM-dd-HH"),
            FileResolution.Day => floored.ToString("yyyy-MM-dd"),
            FileResolution.Month => floored.ToString("yyyy-MM"),
            _ => throw new NotImplementedException(),
        }).ToLower() + ".txt";
    }
    DateTime parseFileName(string fullName) {
        var p = fullName.Substring(0, fullName.Length - 4)[getStreamPrefix().Length..].Split("-").Select(p => int.Parse(p)).ToArray();
        return _res switch {
            FileResolution.Minute => new DateTime(p[0], p[1], p[2], p[3], p[4], 0, DateTimeKind.Utc),
            FileResolution.Hour => new DateTime(p[0], p[1], p[2], p[3], 0, 0, DateTimeKind.Utc),
            FileResolution.Day => new DateTime(p[0], p[1], p[2], 0, 0, 0, DateTimeKind.Utc),
            FileResolution.Month => new DateTime(p[0], p[1], 0, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new NotImplementedException(),
        };
    }
    public void FlushToDisk() {
        if (_lastAppendStream != null) _lastAppendStream.Flush();
    }
    public void Delete(DateTime to) {
        releaseOpenFiles();
        foreach (var f in GetLogFileDates()) {
            var fileTo = f.AddInterval(_res);
            if (fileTo <= to) _io.DeleteIfItExists(getStreamFileName(f));
        }
    }
    public void Dispose() {
        releaseOpenFiles();
    }
    internal long Size() {
        return GetLogFileDates().Select(f => _io.GetFileSizeOrZeroIfUnknown(getStreamFileName(f))).Sum();
    }
}
