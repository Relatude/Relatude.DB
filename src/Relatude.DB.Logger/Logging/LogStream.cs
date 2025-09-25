using Relatude.DB.IO;
using Relatude.DB.Common;
// Filestructure:
// 1. 8 bytes marker
// 2. 8 bytes first date
// 3. 8 bytes last date
// 4. 1 byte compressed
// 5. 4 bytes length of data
// 6. data
// - > repeat 1-6
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
    readonly FileInterval _fileInterval;
    readonly string _logName;
    readonly FileKeyUtility _fileKeys;
    IIOProvider _io;
    Guid _startMarker = new Guid("e4aaf217-ef1a-4c58-867f-5e6d2337e935");
    Guid _endMarker = new Guid("ef4d58fe-e6ee-4af0-a923-1decca3046c1");
    bool _compressed;
    int _bufferAutoFlushLimit;
    public LogStream(IIOProvider io, string logName, bool compressed, FileInterval fileInterval, FileKeyUtility fileKeys) {
        _io = io;
        _logName = logName;
        _compressed = compressed;
        _fileInterval = fileInterval;
        _fileKeys = fileKeys;
        _bufferAutoFlushLimit = 1 * 1024 * 1024; // 1 mb
    }
    Dictionary<string, List<LogRecord>> _buffer = new();
    int _dataInBuffer = 0;
    public void Record(LogRecord record, bool flushToDisk = false) {
        var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, record.TimeStamp);
        if (_buffer.TryGetValue(fileKey, out var records)) {
            records.Add(record);
        } else {
            _buffer.Add(fileKey, new() { record });
        }
        _dataInBuffer += record.Data.Length + 29;
        if (flushToDisk) {
            flushBuffer(true);
        } else if (_dataInBuffer >= _bufferAutoFlushLimit) {
            flushBuffer(false);
        }
    }
    void flushBufferAndReleaseOpenFiles() {
        flushBuffer(false);
        if (_lastAppendStream != null) {
            _lastAppendStream.Dispose();
            _lastAppendStream = null;
        }
    }
    public IEnumerable<LogRecord> Extract(DateTime from, DateTime until, int skip, int take, bool orderByDescendingDates, out int total) {
        flushBufferAndReleaseOpenFiles();
        if (from.Kind != DateTimeKind.Utc) throw new Exception("DateTime must be UTC. ");
        if (until.Kind != DateTimeKind.Utc) throw new Exception("DateTime must be UTC. ");
        var intervals = getIntervalFileDatesFromFreeRange(from, until); // ensuring a new interval for each new file
        var result = new List<LogRecord>();
        var n = 0;
        foreach (var f in intervals) { // per interval file
            var records = extractInterval(f.Item1, f.Item2);
            foreach (var r in records) {
                var ms = new MemoryStream(r.Records);
                BinaryReader br = new(ms);
                var count = br.ReadInt32();
                for (int i = 0; i < count; i++) {
                    var dt = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
                    var length = br.ReadInt32();
                    if (dt >= from && dt < until) { // if relevant
                        var data = br.ReadBytes(length);
                        result.Add(new(dt, data));
                        // add to result if in range of skip and take
                        //if (n >= skip && n < skip + take) result.Add(new(dt, data));
                        n++;
                    } else { // if not, skip
                        br.BaseStream.Position += length;
                    }
                }
            }
        }
        total = n;
        if (!orderByDescendingDates) return result.OrderBy(r => r.TimeStamp).Skip(skip).Take(take);
        else return result.OrderByDescending(r => r.TimeStamp).Skip(skip).Take(take);
    }
    List<Tuple<DateTime, DateTime>> getIntervalFileDatesFromFreeRange(DateTime from, DateTime to) {
        var dt1 = from.Floor(_fileInterval);
        var result = new List<Tuple<DateTime, DateTime>>();
        while (dt1 < to) {
            result.Add(new(dt1, dt1.Ceiling(_fileInterval)));
            dt1 = dt1.AddInterval(_fileInterval);
        }
        return result;
    }
    IAppendStream? _lastAppendStream;
    //    static int errorTestCounter = 0;
    void flushBuffer(bool flushToDisk) {
        foreach (var fileKey in _buffer.Keys) {
            var records = _buffer[fileKey];
            var dtFirst = records.First().TimeStamp;
            var dtLast = records.Last().TimeStamp;
            var ms = new MemoryStream();
            BinaryWriter bw = new(ms);
            bw.Write(records.Count);
            foreach (var record in records) {
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
            //            if (errorTestCounter++ == 10) _lastAppendStream.WriteDateTimeUtc(dtLast); // simulate disk probelm
            _lastAppendStream.WriteBool(_compressed);
            _lastAppendStream.WriteByteArray(data);
            _lastAppendStream.WriteGuid(_endMarker);
            if (flushToDisk) _lastAppendStream.Flush();
        }
        _buffer.Clear();
        _dataInBuffer = 0;
    }
    List<logRecordData> extractInterval(DateTime fromDt, DateTime toDt) {
        var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, fromDt);
        if (fileKey != _fileKeys.Logger_FileNameBin(_logName, _fileInterval, toDt)) throw new Exception("Data must be in the same interval. ");
        // seek to first matching date
        List<logRecordData> result = new();
        if (_io.DoesNotExistOrIsEmpty(fileKey)) return result;
        using var stream = _io.OpenRead(fileKey, 0);
        while (stream.More()) {
            if (!stream.MoveToNextValidMarker(_startMarker)) break;
            try {
                var dtFirst = stream.ReadDateTimeUtc();
                var dtLast = stream.ReadDateTimeUtc();
                var compressed = stream.ReadBool();
                var data = stream.ReadByteArray();
                stream.ValidateMarker(_endMarker);
                if (toDt < dtFirst) {
                    continue; // before from date                
                } else if (fromDt > dtLast) {
                    break; // past last relevant date
                } else { // is relevant and may contain relevant data fro range
                    if (compressed) data = CompressionUtility.Decompress(data);
                    result.Add(new(dtFirst, dtLast, data));
                }
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

    public int DeleteLogFilesBefore(DateTime dt) {
        flushBufferAndReleaseOpenFiles();
        var filesToDelete = GetLogFileDates().Where(f => dt > f);
        foreach (var fileDt in filesToDelete) {
            _io.DeleteIfItExists(_fileKeys.Logger_FileNameBin(_logName, _fileInterval, fileDt));
        }
        return filesToDelete.Count();
    }
    public DateTime? GetTimestampOfFirstRecord() {
        flushBuffer(false);
        flushBufferAndReleaseOpenFiles();
        var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, GetLogFileDates().FirstOrDefault());
        if (fileKey == null) return null;
        if (_io.DoesNotExistOrIsEmpty(fileKey)) return null;
        using var stream = _io.OpenRead(fileKey, 0);
        while (!stream.More()) return null;
        if (!stream.MoveToNextValidMarker(_startMarker)) return null;
        return stream.ReadDateTimeUtc();
    }
    public DateTime? GetTimestampOfLastRecord() {
        flushBuffer(false);
        flushBufferAndReleaseOpenFiles();
        var fileKey = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, GetLogFileDates().LastOrDefault());
        if (fileKey == null) return null;
        if (_io.DoesNotExistOrIsEmpty(fileKey)) return null;
        using var stream = _io.OpenRead(fileKey, 0);
        DateTime dtLast = DateTime.MinValue;
        while (stream.More()) {
            if (!stream.MoveToNextValidMarker(_startMarker)) break;
            try {
                stream.ReadDateTimeUtc(); // dtFirst
                dtLast = stream.ReadDateTimeUtc();
                var compressed = stream.ReadBool();
                stream.SkipByteArray();
                stream.ValidateMarker(_endMarker);
            } catch (Exception) { // ignore error, just continue                
                break;
            }

        }
        return dtLast;
    }

    public void FlushToDisk() {
        flushBuffer(true);
    }
    public void Delete(DateTime to) {
        flushBufferAndReleaseOpenFiles();
        var files = GetLogFileDates();
        foreach (var f in files) {
            var fileTo = f.AddInterval(_fileInterval);
            if (fileTo <= to)
                _io.DeleteIfItExists(_fileKeys.Logger_FileNameBin(_logName, _fileInterval, f));
        }
    }
    public void Dispose() {
        flushBufferAndReleaseOpenFiles();
    }
    internal void DeleteLargeLog(int maxTotalSizeOfLogFilesInMb) {
        // omitting file for current interval, this is never deleted
        if (maxTotalSizeOfLogFilesInMb == 0) return;
        flushBufferAndReleaseOpenFiles();
        var currentFile = _fileKeys.Logger_FileNameBin(_logName, _fileInterval, DateTime.UtcNow.Floor(_fileInterval));
        var files = GetLogFileDates().Select(d => _fileKeys.Logger_FileNameBin(_logName, _fileInterval, d)).Where(f => f != currentFile).OrderBy(f => f);
        var currentTotalSize = files.Sum(_io.GetFileSizeOrZeroIfUnknown) + _io.GetFileSizeOrZeroIfUnknown(currentFile);
        foreach (var f in files) {
            if (currentTotalSize <= maxTotalSizeOfLogFilesInMb * 1024 * 1024) return;
            currentTotalSize = currentTotalSize - _io.GetFileSizeOrZeroIfUnknown(f);
            _io.DeleteIfItExists(f);
        }
    }
    internal long Size() {
        flushBufferAndReleaseOpenFiles();
        var files = GetLogFileDates().Select(d => _fileKeys.Logger_FileNameBin(_logName, _fileInterval, d));
        return files.Sum(_io.GetFileSizeOrZeroIfUnknown);
    }
}
