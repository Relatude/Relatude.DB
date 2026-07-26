using Relatude.DB.IO;
using Relatude.DB.Common;
// Filestructure, repeated per segment:
// 1. 16 bytes start marker
// 2. 8 bytes first date
// 3. 8 bytes last date
// 4. 1 byte compressed
// 5. 4 bytes length of data + data
// 6. 16 bytes end marker
namespace Relatude.DB.Logging;
internal class logRecordData {
    internal logRecordData(DateTime dtFirst, DateTime dtLast, byte[] records) {
        DtFirst = dtFirst;
        DtLast = dtLast;
        Records = records;
    }
    public readonly DateTime DtFirst;
    public readonly DateTime DtLast;
    public readonly byte[] Records;
}
// not threadsafe!
internal class LogStream : IDisposable {
    static readonly Guid _startMarker = new("e4aaf217-ef1a-4c58-867f-5e6d2337e935");
    static readonly Guid _endMarker = new("ef4d58fe-e6ee-4af0-a923-1decca3046c1");
    const int _bufferAutoFlushLimit = 1024 * 1024; // 1 mb
    readonly FileInterval _fileInterval;
    readonly string _logName;
    readonly FileKeyUtility _fileKeys;
    readonly IIOProvider _io;
    readonly bool _compressed;
    readonly Dictionary<string, List<LogRecord>> _buffer = new();
    int _dataInBuffer = 0;
    IAppendStream? _lastAppendStream;
    public LogStream(IIOProvider io, string logName, bool compressed, FileInterval fileInterval, FileKeyUtility fileKeys) {
        _io = io;
        _logName = logName;
        _compressed = compressed;
        _fileInterval = fileInterval;
        _fileKeys = fileKeys;
    }
    public void Record(LogRecord record, bool flushToDisk = false) {
        var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, record.TimeStamp);
        if (_buffer.TryGetValue(fileKey, out var records)) records.Add(record);
        else _buffer.Add(fileKey, new() { record });
        _dataInBuffer += record.Data.Length + 29;
        if (flushToDisk) flushBuffer(true);
        else if (_dataInBuffer >= _bufferAutoFlushLimit) flushBuffer(false);
    }
    void flushBuffer(bool flushToDisk) {
        foreach (var (fileKey, records) in _buffer) {
            // records may have out of order timestamps, so min/max must be searched for,
            // correct segment bounds are essential as they are used to skip segments on extract
            var dtFirst = DateTime.MaxValue;
            var dtLast = DateTime.MinValue;
            var ms = new MemoryStream();
            BinaryWriter bw = new(ms);
            bw.Write(records.Count);
            foreach (var record in records) {
                if (record.TimeStamp < dtFirst) dtFirst = record.TimeStamp;
                if (record.TimeStamp > dtLast) dtLast = record.TimeStamp;
                bw.Write(record.TimeStamp.Ticks);
                bw.Write(record.Data.Length);
                bw.Write(record.Data);
            }
            var data = ms.ToArray();
            if (_compressed) data = CompressionUtility.Compress(data);
            if (_lastAppendStream == null) {
                _lastAppendStream = _io.OpenAppend(fileKey);
            } else if (_lastAppendStream.FileKey != fileKey) {
                _lastAppendStream.Dispose();
                _lastAppendStream = _io.OpenAppend(fileKey);
            }
            _lastAppendStream.WriteGuid(_startMarker);
            _lastAppendStream.WriteDateTimeUtc(dtFirst);
            _lastAppendStream.WriteDateTimeUtc(dtLast);
            _lastAppendStream.WriteBool(_compressed);
            _lastAppendStream.WriteByteArray(data);
            _lastAppendStream.WriteGuid(_endMarker);
            if (flushToDisk) _lastAppendStream.Flush(true);
        }
        _buffer.Clear();
        _dataInBuffer = 0;
    }
    void flushBufferAndReleaseOpenFiles() {
        flushBuffer(false);
        if (_lastAppendStream != null) {
            _lastAppendStream.Dispose();
            _lastAppendStream = null;
        }
    }
    public IEnumerable<LogRecord> Extract(DateTime from, DateTime until, int skip, int take, bool orderByDescendingDates, out int total) {
        if (from.Kind != DateTimeKind.Utc) throw new Exception("DateTime must be UTC. ");
        if (until.Kind != DateTimeKind.Utc) throw new Exception("DateTime must be UTC. ");
        flushBufferAndReleaseOpenFiles();
        var result = new List<LogRecord>();
        foreach (var fileDate in GetLogFileDates()) { // sorted ascending, existing files only
            if (fileDate >= until) break;
            if (fileDate.AddInterval(_fileInterval) <= from) continue; // file entirely before range
            foreach (var segment in extractInterval(fileDate, from, until)) {
                var ms = new MemoryStream(segment.Records);
                BinaryReader br = new(ms);
                var count = br.ReadInt32();
                for (int i = 0; i < count; i++) {
                    var dt = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
                    var length = br.ReadInt32();
                    if (dt >= from && dt < until) result.Add(new(dt, br.ReadBytes(length)));
                    else ms.Position += length; // not relevant, skip
                }
            }
        }
        total = result.Count;
        var ordered = orderByDescendingDates ? result.OrderByDescending(r => r.TimeStamp) : result.OrderBy(r => r.TimeStamp);
        return ordered.Skip(skip).Take(take).ToList();
    }
    // reads all segments of one log file with records overlapping [from, until)
    List<logRecordData> extractInterval(DateTime fileDate, DateTime from, DateTime until) {
        var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, fileDate);
        List<logRecordData> result = new();
        if (_io.DoesNotExistOrIsEmpty(fileKey)) return result;
        using var stream = _io.OpenRead(fileKey, 0);
        while (stream.More()) {
            if (!stream.MoveToNextValidMarker(_startMarker)) break;
            try {
                var dtFirst = stream.ReadDateTimeUtc();
                var dtLast = stream.ReadDateTimeUtc();
                var compressed = stream.ReadBool();
                if (dtLast < from || dtFirst >= until) { // no overlap, skip data without reading it
                    stream.SkipByteArray();
                    stream.ValidateMarker(_endMarker);
                    continue;
                }
                var data = stream.ReadByteArray();
                stream.ValidateMarker(_endMarker);
                if (compressed) data = CompressionUtility.Decompress(data);
                result.Add(new(dtFirst, dtLast, data));
            } catch {
                // ignore, just skip invalid or partially written segments
            }
        }
        return result;
    }
    public List<DateTime> GetLogFileDates() {
        flushBuffer(false);
        return _fileKeys.Logger_FileDatesBin(_io, _logName, _fileInterval);
    }
    public DateTime? GetTimestampOfFirstRecord() {
        flushBufferAndReleaseOpenFiles();
        foreach (var fileDate in GetLogFileDates()) {
            var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, fileDate);
            if (_io.DoesNotExistOrIsEmpty(fileKey)) continue;
            using var stream = _io.OpenRead(fileKey, 0);
            if (!stream.More()) continue;
            if (!stream.MoveToNextValidMarker(_startMarker)) continue;
            try {
                return stream.ReadDateTimeUtc();
            } catch {
                // truncated segment, try next file
            }
        }
        return null;
    }
    public DateTime? GetTimestampOfLastRecord() {
        flushBufferAndReleaseOpenFiles();
        var fileDates = GetLogFileDates();
        for (var i = fileDates.Count - 1; i >= 0; i--) {
            var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, fileDates[i]);
            if (_io.DoesNotExistOrIsEmpty(fileKey)) continue;
            using var stream = _io.OpenRead(fileKey, 0);
            DateTime? dtLast = null;
            while (stream.More()) {
                if (!stream.MoveToNextValidMarker(_startMarker)) break;
                try {
                    stream.ReadDateTimeUtc(); // dtFirst
                    var dt = stream.ReadDateTimeUtc();
                    stream.ReadBool(); // compressed
                    stream.SkipByteArray();
                    stream.ValidateMarker(_endMarker);
                    dtLast = dt; // only trust fully written segments
                } catch {
                    // ignore, just skip invalid or partially written segments
                }
            }
            if (dtLast.HasValue) return dtLast;
        }
        return null;
    }
    public void FlushToDisk() {
        flushBuffer(true);
    }
    public void Delete(DateTime to) {
        flushBufferAndReleaseOpenFiles();
        foreach (var f in GetLogFileDates()) {
            if (f.AddInterval(_fileInterval) <= to)
                _io.DeleteFileIfItExists(_fileKeys.Logger_FileNameBin(_logName, _fileInterval, f));
        }
    }
    internal void DeleteLargeLog(int maxTotalSizeOfLogFilesInMb) {
        // omitting file for current interval, this is never deleted
        if (maxTotalSizeOfLogFilesInMb == 0) return;
        flushBufferAndReleaseOpenFiles();
        var maxTotalSize = maxTotalSizeOfLogFilesInMb * 1024L * 1024L;
        var currentFile = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, DateTime.UtcNow.Floor(_fileInterval));
        var files = GetLogFileDates().Select(d => _fileKeys.Logger_FileNameBin(_logName, _fileInterval, d)).Where(f => f != currentFile).OrderBy(f => f).ToList();
        var currentTotalSize = files.Sum(_io.GetFileSizeOrZeroIfUnknown) + _io.GetFileSizeOrZeroIfUnknown(currentFile);
        foreach (var f in files) { // oldest first
            if (currentTotalSize <= maxTotalSize) return;
            currentTotalSize -= _io.GetFileSizeOrZeroIfUnknown(f);
            _io.DeleteFileIfItExists(f);
        }
    }
    internal long Size() {
        flushBufferAndReleaseOpenFiles();
        return GetLogFileDates().Sum(d => _io.GetFileSizeOrZeroIfUnknown(_fileKeys.Logger_FileNameBin(_logName, _fileInterval, d)));
    }
    public void Dispose() {
        flushBufferAndReleaseOpenFiles();
    }
}
