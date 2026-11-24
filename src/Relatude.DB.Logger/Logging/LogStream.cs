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
/// <summary>Why a log's entries are not reaching its files, since when, and how many it has let go of.</summary>
internal sealed record LogWriteProblem(string Message, DateTime SinceUtc, long DroppedRecords);
// not threadsafe!
internal class LogStream : IDisposable {
    static readonly Guid _startMarker = new("e4aaf217-ef1a-4c58-867f-5e6d2337e935");
    static readonly Guid _endMarker = new("ef4d58fe-e6ee-4af0-a923-1decca3046c1");
    const int _bufferAutoFlushLimit = 1024 * 1024; // 1 mb
    // A log never waits for its files: it opens them holding the log's lock, which the pages reading it and,
    // when the backlog is full, those recording to it are waiting for. A file that will not open or take what is
    // written is tried again after a pause that doubles up to the last, and what was recorded meanwhile is kept
    // in memory up to a limit, the oldest let go of first.
    const int _firstRetryMs = 1000;
    const int _maxRetryMs = 30_000;
    const int _maxBufferWhileFailing = 16 * 1024 * 1024;
    readonly FileInterval _fileInterval;
    readonly string _logName;
    readonly IIOProvider _io;
    readonly bool _compressed;
    readonly Dictionary<string, List<LogRecord>> _buffer = new();
    int _dataInBuffer = 0;
    IAppendStream? _lastAppendStream;
    int _failures;
    long _retryAt;
    long _dropped;
    volatile LogWriteProblem? _problem;
    public LogStream(IIOProvider io, string logName, bool compressed, FileInterval fileInterval) {
        _io = io;
        _logName = logName;
        _compressed = compressed;
        _fileInterval = fileInterval;
    }
    /// <summary>Set while what is recorded cannot be written; read without the log's lock.</summary>
    public LogWriteProblem? Problem => _problem;
    public long DroppedRecords => Interlocked.Read(ref _dropped);
    public void Record(LogRecord record, bool flushToDisk = false) {
        // the buffer is keyed by the joined form of the file key
        var fileKey = fileKeyOf(record.TimeStamp);
        if (_buffer.TryGetValue(fileKey, out var records)) records.Add(record);
        else _buffer.Add(fileKey, new() { record });
        _dataInBuffer += sizeOf(record);
        if (flushToDisk) flushBuffer(true);
        else if (_dataInBuffer >= _bufferAutoFlushLimit) flushBuffer(false);
    }
    static int sizeOf(LogRecord record) => record.Data.Length + 29;
    // The file a record goes to changes only when its interval does, so the key is worked out once
    // per interval rather than once per record: formatting the date and joining up the name cost
    // about as much as all the rest of putting a record in the buffer.
    DateTime _keyFrom = DateTime.MaxValue;
    DateTime _keyUntil = DateTime.MinValue;
    string _key = string.Empty;
    string fileKeyOf(DateTime timestamp) {
        if (timestamp < _keyFrom || timestamp >= _keyUntil) {
            _keyFrom = timestamp.Floor(_fileInterval);
            _keyUntil = _keyFrom.EndOfInterval(_fileInterval);
            _key = FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, _keyFrom).AsKeyString();
        }
        return _key;
    }
    void flushBuffer(bool flushToDisk) {
        if (_buffer.Count == 0) return;
        if (_failures > 0 && Environment.TickCount64 < _retryAt) {
            dropIfTooMuch();
            return;
        }
        // in the order the files were first written to, the newest last: its stream is the one left open
        var written = 0;
        foreach (var (fileKey, records) in _buffer) {
            Exception? flushError;
            try {
                flushError = writeSegment(fileKey, records, flushToDisk);
            } catch (Exception err) {
                forgetWritten(written);
                failed(err);
                dropIfTooMuch();
                return;
            }
            written++;
            if (flushError != null) {
                forgetWritten(written);
                failed(flushError);
                return;
            }
        }
        _buffer.Clear();
        _dataInBuffer = 0;
        if (_failures > 0) recovered();
    }
    void forgetWritten(int count) {
        foreach (var fileKey in _buffer.Keys.Take(count).ToList()) {
            foreach (var record in _buffer[fileKey]) _dataInBuffer -= sizeOf(record);
            _buffer.Remove(fileKey);
        }
    }
    // Throws only when nothing reached the stream, so a segment is never written twice: bytes the stream has
    // taken are sent when it lets go of them, or lost with it. A flush that failed after that is returned.
    Exception? writeSegment(string fileKey, List<LogRecord> records, bool flushToDisk) {
        // records may have out of order timestamps, so min/max must be searched for,
        // correct segment bounds are essential as they are used to skip segments on extract
        var dtFirst = DateTime.MaxValue;
        var dtLast = DateTime.MinValue;
        // sized up front, since the size is known: a stream left to grow on its own allocates
        // the batch several times over on its way up
        var size = sizeof(int);
        foreach (var record in records) size += sizeof(long) + sizeof(int) + record.Data.Length;
        var ms = new MemoryStream(size);
        BinaryWriter bw = new(ms);
        bw.Write(records.Count);
        foreach (var record in records) {
            if (record.TimeStamp < dtFirst) dtFirst = record.TimeStamp;
            if (record.TimeStamp > dtLast) dtLast = record.TimeStamp;
            bw.Write(record.TimeStamp.Ticks);
            bw.Write(record.Data.Length);
            bw.Write(record.Data);
        }
        // written from the stream's own buffer: copying it out first would allocate it once more
        var data = ms.GetBuffer();
        var length = (int)ms.Length;
        if (_compressed) {
            data = CompressionUtility.Compress(data.AsSpan(0, length));
            length = data.Length;
        }
        var handedOver = false;
        try {
            if (_lastAppendStream != null && _lastAppendStream.FileKey != fileKey) closeAppendStream();
            _lastAppendStream ??= _io.OpenAppend(fileKey.SplitKey(), TimeSpan.Zero);
            _lastAppendStream.WriteGuid(_startMarker);
            _lastAppendStream.WriteDateTimeUtc(dtFirst);
            _lastAppendStream.WriteDateTimeUtc(dtLast);
            _lastAppendStream.WriteBool(_compressed);
            _lastAppendStream.WriteVerifiedInt(length); // as WriteByteArray writes it
            _lastAppendStream.Append(data, length);
            _lastAppendStream.WriteGuid(_endMarker);
            handedOver = true;
            if (flushToDisk) _lastAppendStream.Flush(true);
            return null;
        } catch (Exception err) {
            closeAppendStreamQuietly();
            if (!handedOver) throw;
            return err;
        }
    }
    void failed(Exception err) {
        _failures++;
        _retryAt = Environment.TickCount64 + Math.Min(_maxRetryMs, _firstRetryMs << Math.Min(_failures - 1, 5));
        var problem = _problem;
        if (problem == null) {
            FileOpenRetry.DefaultLog?.Invoke("The log \"" + _logName + "\" could not write to its file and keeps what it records in memory until it can: " + err.Message);
        }
        _problem = new(err.Message, problem?.SinceUtc ?? DateTime.UtcNow, DroppedRecords);
    }
    void recovered() {
        _failures = 0;
        if (_problem is not { } problem) return;
        _problem = null;
        var dropped = DroppedRecords - problem.DroppedRecords;
        FileOpenRetry.DefaultLog?.Invoke("The log \"" + _logName + "\" writes to its file again, after "
            + (DateTime.UtcNow - problem.SinceUtc).TotalSeconds.ToString("0") + " s" + (dropped > 0 ? "; " + dropped + " entries were let go of meanwhile." : "."));
    }
    // the oldest go first: the newest are what a page about now wants to show
    void dropIfTooMuch() {
        var dropped = 0L;
        while (_dataInBuffer > _maxBufferWhileFailing && _buffer.Count > 0) {
            var (fileKey, records) = _buffer.First();
            var count = Math.Max(1, records.Count / 2);
            for (var i = 0; i < count; i++) _dataInBuffer -= sizeOf(records[i]);
            records.RemoveRange(0, count);
            if (records.Count == 0) _buffer.Remove(fileKey);
            dropped += count;
        }
        if (dropped == 0) return;
        Interlocked.Add(ref _dropped, dropped);
        if (_problem is { } problem) _problem = problem with { DroppedRecords = DroppedRecords };
    }
    void closeAppendStream() {
        var stream = _lastAppendStream;
        _lastAppendStream = null;
        stream?.Dispose();
    }
    void closeAppendStreamQuietly() {
        try {
            closeAppendStream();
        } catch {
            // what it held is lost with it; the next write opens the file again
        }
    }
    void flushBufferAndReleaseOpenFiles() {
        flushBuffer(false);
        try {
            closeAppendStream();
        } catch (Exception err) {
            failed(err);
        }
    }
    /// <summary>The earliest and latest timestamp of what is buffered and not yet on file.</summary>
    internal (DateTime? First, DateTime? Last) BufferedSpan() {
        DateTime? first = null, last = null;
        foreach (var records in _buffer.Values) {
            foreach (var record in records) {
                if (first == null || record.TimeStamp < first) first = record.TimeStamp;
                if (last == null || record.TimeStamp > last) last = record.TimeStamp;
            }
        }
        return (first, last);
    }
    /// <summary>
    /// Every record of a range, one at a time, oldest file first. Records within one file come in
    /// the order they were written, which is nearly but not exactly the order they were recorded
    /// in - a caller that needs them sorted has to sort them.
    ///
    /// This is what a search reads: a record is handed over, tested and let go, so a range far
    /// larger than memory can be searched even though extracting the same range could not be held.
    /// The stream is not threadsafe and this holds no lock of its own, so it has to be walked to
    /// its end (or abandoned) before anything else touches the stream.
    /// </summary>
    public IEnumerable<LogRecord> Enumerate(DateTime from, DateTime until) {
        if (from.Kind != DateTimeKind.Utc) throw new Exception("DateTime must be UTC. ");
        if (until.Kind != DateTimeKind.Utc) throw new Exception("DateTime must be UTC. ");
        flushBufferAndReleaseOpenFiles();
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
                    if (dt >= from && dt < until) yield return new(dt, br.ReadBytes(length));
                    else ms.Position += length; // not relevant, skip
                }
            }
        }
    }
    // a file held elsewhere is reported at once rather than waited for, holding the log
    IReadStream openRead(string[] fileKey) => _io.OpenRead(fileKey, 0, TimeSpan.Zero);
    // reads all segments of one log file with records overlapping [from, until)
    List<logRecordData> extractInterval(DateTime fileDate, DateTime from, DateTime until) {
        var fileKey = FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, fileDate);
        List<logRecordData> result = new();
        if (_io.DoesNotExistOrIsEmpty(fileKey)) return result;
        using var stream = openRead(fileKey);
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
        return FileKeyUtility.Logger_FileDatesBin(_io, _logName, _fileInterval);
    }
    public DateTime? GetTimestampOfFirstRecord() {
        flushBufferAndReleaseOpenFiles();
        return firstRecordIn(GetLogFileDates().Where(hasContent));
    }
    public DateTime? GetTimestampOfLastRecord() {
        flushBufferAndReleaseOpenFiles();
        return lastRecordIn(Enumerable.Reverse(GetLogFileDates()).Where(hasContent));
    }
    bool hasContent(DateTime fileDate) => !_io.DoesNotExistOrIsEmpty(FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, fileDate));
    /// <summary>Writes out what is buffered and lets go of the open file, so a listing sees every record.</summary>
    internal void ReleaseFiles() => flushBufferAndReleaseOpenFiles();
    /// <summary>Writes out what is buffered, keeping the open file open.</summary>
    internal void WriteBuffered() => flushBuffer(false);
    /// <summary>
    /// The first and last record of files the caller has listed itself, after <see cref="ReleaseFiles"/>:
    /// the dates of the files holding anything, oldest first. Saves the listing and a size lookup per file.
    /// </summary>
    internal (DateTime? First, DateTime? Last) GetFirstAndLastRecord(IReadOnlyList<DateTime> datesOfFilesWithContent) {
        var first = firstRecordIn(datesOfFilesWithContent);
        if (first == null) return (null, null);
        return (first, lastRecordIn(Enumerable.Reverse(datesOfFilesWithContent)));
    }
    DateTime? firstRecordIn(IEnumerable<DateTime> fileDates) {
        foreach (var fileDate in fileDates) {
            using var stream = openRead(FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, fileDate));
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
    DateTime? lastRecordIn(IEnumerable<DateTime> fileDatesNewestFirst) {
        foreach (var fileDate in fileDatesNewestFirst) {
            using var stream = openRead(FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, fileDate));
            var dtLast = lastSegmentFromTheEnd(stream);
            if (dtLast == null && stream.Length > _tailWindow) {
                stream.Position = 0;
                dtLast = lastSegmentFromTheStart(stream);
            }
            if (dtLast.HasValue) return dtLast;
        }
        return null;
    }
    // Reading a file from its start to find its last segment downloads all of it on blob storage. A segment's
    // header says how long it is, so the last start marker near the end whose header and end marker agree is the
    // last whole segment written.
    const int _tailWindow = 2 * 1024 * 1024;
    const int _headerLength = 16 + 8 + 8 + 1 + 4 + 4;
    static DateTime? lastSegmentFromTheEnd(IReadStream stream) {
        var length = stream.Length;
        var from = Math.Max(0, length - _tailWindow);
        stream.Position = from;
        var tail = stream.Read((int)(length - from));
        ReadOnlySpan<byte> start = _startMarker.ToByteArray();
        ReadOnlySpan<byte> end = _endMarker.ToByteArray();
        var i = tail.AsSpan().LastIndexOf(start);
        while (i >= 0) {
            if (i + _headerLength + 16 <= tail.Length) {
                var dataLength = BitConverter.ToInt32(tail, i + 33);
                var endAt = (long)i + _headerLength + dataLength;
                if (dataLength >= 0 && BitConverter.ToInt32(tail, i + 37) == -dataLength && endAt + 16 <= tail.Length
                    && tail.AsSpan((int)endAt, 16).SequenceEqual(end)) {
                    return new DateTime(BitConverter.ToInt64(tail, i + 24), DateTimeKind.Utc);
                }
            }
            i = i == 0 ? -1 : tail.AsSpan(0, i).LastIndexOf(start);
        }
        return null;
    }
    static DateTime? lastSegmentFromTheStart(IReadStream stream) {
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
        return dtLast;
    }
    public void FlushToDisk() {
        flushBuffer(true);
    }
    public void Delete(DateTime to) {
        flushBufferAndReleaseOpenFiles();
        foreach (var f in GetLogFileDates()) {
            if (f.AddInterval(_fileInterval) <= to)
                _io.DeleteFileIfItExists(FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, f));
        }
    }
    internal long Size() {
        flushBufferAndReleaseOpenFiles();
        return GetLogFileDates().Sum(d => _io.GetFileSizeOrZeroIfUnknown(FileKeyUtility.Logger_FileNameBin(_logName, _fileInterval, d)));
    }
    public void Dispose() {
        flushBufferAndReleaseOpenFiles();
    }
}
