using Relatude.DB.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Relatude.DB.Logging.Statistics;

namespace Relatude.DB.Logging;
// threadsafe
internal class Log : IDisposable {
    // Taken through hold() and tryHold(), never on its own: see there.
    readonly object _lock = new();
    public LogSettings Setting { get => _setting; }
    readonly LogStream _logStream;
    StatisticsCount _rowStat = null!; // initialized in loadAllStatistics
    ReadOnlyDictionary<string, List<IStatistics>> _statByProp = null!; // initialized in loadAllStatistics
    readonly LogSettings _setting;
    readonly IIOProvider _io;
    readonly LogTextStream _logTextStream;
    readonly string[] _statFileKey;
    readonly string[] _backupStatFile;
    // A declared column is found whatever the case it is named in, and kept under the key it was
    // declared with: the statistics and every reader look it up by that one spelling, so "Amount"
    // recorded for a column declared "amount" has to end up as "amount".
    readonly Dictionary<string, string> _declaredKeys;
    static string getStatisticsFileKey(string property, StatisticsInfo info, LogSettings settings) {
        // a unique that prevents collisions between different statistical settings
        // if a change is made to the stat settings it will simply have a different key and last state will be ignored and not corrupt the new state
        return "stat_" + property + "_" + info.Resolution + "_" + settings.FirstDayOfWeek + "_" + info.StatisticsType;
    }
    public Log(LogSettings settings, IIOProvider io) {
        _setting = settings;
        _io = io;
        _logStream = new(_io, _setting.Key, _setting.Compressed, _setting.FileInterval);
        _logTextStream = new(io, _setting.Key, _setting.FileInterval);
        _statFileKey = FileKeyUtility.Logger_GetStatistics(_setting.Key);
        _backupStatFile = FileKeyUtility.Logger_GetStatisticsBackUp(_setting.Key);
        _declaredKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _setting.Properties.Keys) _declaredKeys.TryAdd(key, key);
        loadAllStatistics();
    }
    void loadAllStatistics() {
        var ri = new StatisticsInfo(StatisticsType.Count, _setting.ResolutionRowStats);
        _rowStat = new StatisticsCount(ri, _setting.FirstDayOfWeek, getStatisticsFileKey("r_o_w", ri, _setting));
        var statByProp = new Dictionary<string, List<IStatistics>>();
        foreach (var kv in _setting.Properties) {
            if (kv.Value.Statistics == null) continue;
            foreach (var info in kv.Value.Statistics) {
                if (info == null) continue;
                var stat = createStatisticsIfPossible(kv.Key, kv.Value, info);
                if (stat == null) continue;
                if (!statByProp.TryGetValue(kv.Key, out var stats)) statByProp.Add(kv.Key, stats = new());
                stats.Add(stat);
            }
        }
        _statByProp = new(statByProp);
        try {
            loadStatisticsState();
        } catch {
            // ignore in case of older formats or partially save data...
        }
    }
    IStatistics? createStatisticsIfPossible(string propertyId, LogProperty property, StatisticsInfo info) {
        var f = _setting.FirstDayOfWeek;
        var key = getStatisticsFileKey(propertyId, info, _setting);
        var isNumeric = property.DataType is LogDataType.Integer or LogDataType.Double;
        var isBytes = property.DataType is LogDataType.Bytes;
        return info.StatisticsType switch {
            StatisticsType.Count => new StatisticsCount(info, f, key),
            StatisticsType.Sum when property.DataType is LogDataType.Integer => new StatisticsIntegerSum(info, f, key),
            StatisticsType.Sum when property.DataType is LogDataType.Double => new StatisticsDoubleSum(info, f, key),
            StatisticsType.AvgMinMax when isNumeric => new StatisticsAvgMinMax(info, f, key),
            StatisticsType.CountSumAvgMinMax when isNumeric => new StatisticsCountSumAvgMinMax(info, f, key),
            StatisticsType.UniqueCountWithValues when !isBytes => new StatisticsGroupCount(info, f, key),
            StatisticsType.UniqueCountHashedValues when !isBytes => new StatisticsUniqueCount(info, f, key),
            StatisticsType.UniqueCountEstimate when !isBytes => new StatisticsEstimatedUniqueCount(info, f, key),
            _ => null,
        };
    }
    public void FlushToDiskNow() {
        if (_setting.EnableLog) {
            using (hold()) {
                _logStream.FlushToDisk();
                if (_setting.EnableLogTextFormat) _logTextStream.FlushToDisk();
            }
        }
    }
    /// <summary>
    /// Records one entry. The two switches override the log's own for this one entry: true writes the
    /// entry or counts it although the log is off, false leaves it out although the log is on - which
    /// is how entries that are already counted (moved to a new file layout, say) are written without
    /// being counted twice.
    ///
    /// The caller is not made to wait for a log that is busy - with a page or a search being read,
    /// its statistics being saved, its old files deleted. What is recorded is a query that has just
    /// been answered or a transaction that has just been made, and a page of a large log can take a
    /// good part of a second to read. So an entry that finds the log busy is left in its backlog, and
    /// whoever holds the log next records the backlog before doing anything else: a page, a statistic
    /// or a flush always includes every entry recorded before it was asked for. An entry that has to
    /// be on disk when this returns does wait, and so does one that finds the backlog full.
    /// </summary>
    public void Record(LogEntry entry, bool flushToDisk, bool? forceLogging = null, bool? forceStatistics = null) {
        if (_disposed) return;
        var logging = forceLogging ?? _setting.EnableLog;
        var statistics = forceStatistics ?? _setting.EnableStatistics;
        if (!logging && !statistics) return;
        // The entry as the log declares it, once, so the file and the statistics see the same values.
        // Both are worked out from the entry and the log's settings alone, so neither needs the lock.
        entry = normalize(entry);
        var pending = new Pending(entry, logging ? getRecord(entry) : null, statistics);
        if (flushToDisk || Volatile.Read(ref _backlogCount) >= MaxBacklog) {
            using (hold()) recordNow(pending, flushToDisk);
        } else if (tryHold(out var held)) {
            using (held) recordNow(pending, false);
        } else {
            leave(pending);
        }
    }
    void recordNow(Pending pending, bool flushToDisk) {
        // a log replaced or removed while a caller still held it: its files belong to the log
        // that took its place, and writing here would reopen them behind that one's back
        if (_disposed) return;
        var entry = pending.Entry;
        if (pending.Record != null) {
            _logStream.Record(pending.Record, flushToDisk);
            if (_setting.EnableLogTextFormat) _logTextStream.Record(entry, flushToDisk);
        }
        if (pending.Statistics) {
            _rowStat.RecordIfPossible(entry.Timestamp, _true);
            foreach (var value in entry.Values) {
                if (_statByProp.TryGetValue(value.Key, out var stats)) {
                    foreach (var stat in stats) {
                        stat.RecordIfPossible(entry.Timestamp, value.Value);
                    }
                }
            }
        }
    }
    static readonly object _true = true; // boxed once, rather than once per entry the row statistic counts

    // ---- the lock and the backlog ----

    // An entry recorded while the log was busy, ready to go: as the log declares it, and the record
    // it is written as (null when it is only counted).
    readonly record struct Pending(LogEntry Entry, LogRecord? Record, bool Statistics);
    // Entries that found the log busy, for whoever holds it next. Bounded, so a log that stays busy
    // makes its callers wait again rather than keep a backlog that grows for as long as it is busy.
    readonly ConcurrentQueue<Pending> _backlog = new();
    int _backlogCount;
    internal const int MaxBacklog = 20_000;
    // Takes the log, the way everything in this class takes it: the backlog is recorded first, so
    // whatever is read, saved or flushed holding the log includes every entry recorded before.
    Held hold() {
        Monitor.Enter(_lock);
        return entered();
    }
    bool tryHold(out Held held) {
        held = default;
        if (!Monitor.TryEnter(_lock)) return false;
        held = entered();
        return true;
    }
    // the log just taken, handed over once the backlog is recorded
    Held entered() {
        try {
            recordBacklog();
        } catch {
            Monitor.Exit(_lock);
            throw;
        }
        return new(this);
    }
    void release() {
        try {
            recordBacklog(); // what was left while this held the log
        } finally {
            Monitor.Exit(_lock);
        }
        // ...and what was left in the moment between the two, by a caller that found the log still
        // held: it would otherwise wait for the next caller to come along
        if (!_backlog.IsEmpty && Monitor.TryEnter(_lock)) {
            try {
                recordBacklog();
            } finally {
                Monitor.Exit(_lock);
            }
        }
    }
    readonly struct Held(Log? log) : IDisposable {
        public void Dispose() => log?.release();
    }
    void recordBacklog() {
        while (_backlog.TryDequeue(out var pending)) {
            Interlocked.Decrement(ref _backlogCount);
            recordNow(pending, false);
        }
    }
    // an entry the log was too busy to take: left in the backlog, for whoever holds the log next
    void leave(Pending pending) {
        _backlog.Enqueue(pending);
        Interlocked.Increment(ref _backlogCount);
        // The one holding the log may have let go of it since it was found busy, and then nobody
        // would record this entry until the next one came: whoever gets the log now records it.
        if (tryHold(out var held)) held.Dispose();
    }
    /// <summary>
    /// The entry the way the log keeps it: the timestamp in UTC, and every declared value in its
    /// declared type (see <see cref="LogValues"/>). A value that is null, or that has no reading as
    /// the declared type, is left out - a zero written in its place would be counted by the
    /// statistics as a measurement nobody made. Values the log declares nothing for are kept as
    /// they are and stored as the type they have, or as their text.
    /// </summary>
    LogEntry normalize(LogEntry entry) {
        var timestamp = entry.Timestamp.Kind switch {
            DateTimeKind.Utc => entry.Timestamp,
            DateTimeKind.Local => entry.Timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(entry.Timestamp, DateTimeKind.Utc),
        };
        var values = new Dictionary<string, object>(entry.Values.Count);
        foreach (var kv in entry.Values) {
            if (kv.Value == null) continue;
            if (_declaredKeys.TryGetValue(kv.Key, out var declared) && _setting.Properties.TryGetValue(declared, out var property)) {
                if (LogValues.TryConvert(kv.Value, property.DataType, out var converted)) values[declared] = converted;
            } else {
                values[kv.Key] = forceToLegalType(kv.Value);
            }
        }
        return new LogEntry { Timestamp = timestamp, Values = values };
    }
    /// <summary>
    /// A page of a range, and how many entries the range holds.
    ///
    /// Every record of the range is read - counting them is reading them - but only a page's worth
    /// is held while that happens: the rest are counted and let go, the way a search keeps its
    /// matches. The newest hundred of a million entries is then a hundred records in memory, not a
    /// million of them sorted to find the hundred, which is what a page of a large log opened (and
    /// refreshed live) used to cost. Records sharing a timestamp come out in the order they were
    /// read, as a stable sort of the whole range would give them.
    /// </summary>
    public IEnumerable<LogEntry> Extract(DateTime from, DateTime to, int skip, int take, bool orderByDescendingDates, out int total) {
        using (hold()) {
            if (skip < 0) skip = 0;
            if (take < 0) take = 0;
            var wanted = (long)skip + take;
            // asked for everything (an export, a rebuild, a move), there is nothing to leave out: all
            // of it is held and sorted once, a stable sort by timestamp, as it always was
            if (wanted > int.MaxValue / 4) {
                var all = _logStream.Enumerate(from, to).ToList();
                total = all.Count;
                var ordered = orderByDescendingDates ? all.OrderByDescending(r => r.TimeStamp) : all.OrderBy(r => r.TimeStamp);
                return ordered.Skip(skip).Take(take).Select(getEntry).ToList(); // materialized inside lock
            }
            var keep = (int)wanted;
            // sorting and trimming costs something, so it is done in batches rather than per record
            var trimAt = Math.Max(keep * 2, 1024);
            var kept = new List<(LogRecord Record, int Ordinal)>();
            var count = 0;
            foreach (var record in _logStream.Enumerate(from, to)) {
                kept.Add((record, count));
                count++;
                if (kept.Count >= trimAt) sortAndTrim(kept, orderByDescendingDates, keep);
            }
            total = count;
            sortAndTrim(kept, orderByDescendingDates, int.MaxValue);
            return kept.Skip(skip).Take(take).Select(k => getEntry(k.Record)).ToList(); // materialized inside lock
        }
    }
    static void sortAndTrim(List<(LogRecord Record, int Ordinal)> kept, bool orderByDescendingDates, int keep) {
        kept.Sort((a, b) => {
            var c = orderByDescendingDates ? b.Record.TimeStamp.CompareTo(a.Record.TimeStamp) : a.Record.TimeStamp.CompareTo(b.Record.TimeStamp);
            return c != 0 ? c : a.Ordinal.CompareTo(b.Ordinal);
        });
        if (keep < kept.Count) kept.RemoveRange(keep, kept.Count - keep);
    }
    /// <summary>
    /// The entries of a range that a search matches, and how many there were of them.
    ///
    /// Nothing is indexed: every record in the range is read and tested, which is what lets a
    /// search ask anything of a log that was written without knowing the question. The reading is
    /// the cost, and it is the cost of the range - so a caller bounds the range, not the search.
    ///
    /// Only the page asked for is held. A search matching more entries than fit in memory is
    /// therefore answerable: the rest are counted and let go.
    /// </summary>
    public IEnumerable<LogEntry> Search(LogSearch search, DateTime from, DateTime to, int skip, int take, bool orderByDescendingDates, out int total) {
        using (hold()) {
            if (search.IsEmpty) return Extract(from, to, skip, take, orderByDescendingDates, out total);
            if (skip < 0) skip = 0;
            if (take < 0) take = 0;
            var wanted = (long)skip + take;
            var keep = wanted > int.MaxValue ? int.MaxValue : (int)wanted;
            // sorting and trimming costs something, so it is done in batches rather than per match
            var trimAt = wanted > int.MaxValue / 4 ? int.MaxValue : Math.Max(keep * 2, 1024);
            var matches = new List<(LogEntry Entry, int Ordinal)>();
            var count = 0;
            foreach (var record in _logStream.Enumerate(from, to)) {
                var entry = getEntry(record);
                if (!search.Matches(entry, _setting)) continue;
                matches.Add((entry, count));
                count++;
                if (matches.Count >= trimAt) sortAndTrim(matches, orderByDescendingDates, keep);
            }
            total = count;
            sortAndTrim(matches, orderByDescendingDates, int.MaxValue);
            return matches.Skip(skip).Take(take).Select(m => m.Entry).ToList();
        }
    }
    // Records of one file come out in the order they were written, which is not quite the order
    // they were recorded in: the ordinal keeps entries sharing a timestamp in the order extracting
    // them would have given, so a page boundary does not fall differently between the two.
    static void sortAndTrim(List<(LogEntry Entry, int Ordinal)> matches, bool orderByDescendingDates, int keep) {
        matches.Sort((a, b) => {
            var c = orderByDescendingDates ? b.Entry.Timestamp.CompareTo(a.Entry.Timestamp) : a.Entry.Timestamp.CompareTo(b.Entry.Timestamp);
            return c != 0 ? c : a.Ordinal.CompareTo(b.Ordinal);
        });
        if (keep < matches.Count) matches.RemoveRange(keep, matches.Count - keep);
    }
    public long GetTotalFileSize() => GetLogFileSize() + GetStatisticsFileSize();
    public long GetLogFileSize() {
        using (hold()) {
            return _logStream.Size() + _logTextStream.Size();
        }
    }
    public long GetStatisticsFileSize() {
        using (hold()) {
            return _io.GetFileSizeOrZeroIfUnknown(_statFileKey);
        }
    }
    static byte[] getStatBytes(IStatistics s) {
        var io = new IOProviderMemory();
        using (var stream = io.OpenAppend(["stat"])) s.SaveState(stream);
        using var read = io.OpenRead(["stat"], 0);
        return read.Read((int)read.Length);
    }
    static void loadStatStateFromBytes(IStatistics s, byte[] bytes) {
        var io = new IOProviderMemory();
        using (var stream = io.OpenAppend(["stat"])) stream.Append(bytes);
        using var read = io.OpenRead(["stat"], 0);
        s.LoadState(read);
    }
    void loadStatisticsState() {
        using (hold()) {
            if (!_setting.EnableStatistics) return;
            if (_io.DoesNotExistOrIsEmpty(_statFileKey)) return;

            if (canConfirmFileIsNotValid(_io, _statFileKey)) { // confirmed corrupted file
                _io.DeleteFileIfItExists(_statFileKey); // delete corrupted file
                if (_io.ExistsAndIsNotEmpty(_backupStatFile)) {
                    if (canConfirmFileIsNotValid(_io, _backupStatFile)) {
                        _io.DeleteFileIfItExists(_backupStatFile); // delete corrupted bkup file
                        return; // both files corrupted, cannot restore
                    } else {
                        _io.CopyFile(_backupStatFile, _statFileKey);
                        // ok, continue to load from restored backup file
                    }
                } else {
                    return; // no backup file, no way to restore
                }
            }

            using var stream = _io.OpenRead(_statFileKey, 0);
            var rowKey = stream.ReadString();
            var rowBytesLength = stream.ReadVerifiedInt();
            if (rowKey == _rowStat.Key) {
                var rowBytes = stream.Read(rowBytesLength);
                loadStatStateFromBytes(_rowStat, rowBytes);
            } else {
                stream.Skip(rowBytesLength);
            }
            var noStats = stream.ReadVerifiedInt();
            var all = _statByProp.Values.SelectMany(s => s);
            var allStats = new Dictionary<string, IStatistics>();
            foreach (var s in all) {
                if (!allStats.ContainsKey(s.Key)) {
                    allStats.Add(s.Key, s);
                } else {
                    // bad internal error - should never happen
                    // but better to just ignore it than crash log store
                }
            }
            for (int i = 0; i < noStats; i++) {
                var statKey = stream.ReadString();
                var statBytesLength = stream.ReadVerifiedInt();
                if (allStats.TryGetValue(statKey, out var stat)) {  // key must match
                    loadStatStateFromBytes(stat, stream.Read(statBytesLength));
                } else {
                    stream.Skip(statBytesLength);
                }
            }
        }
    }
    static readonly Guid _hasEndMarker = Guid.Parse("95a2c0ae-c9f2-4e2a-b2c0-0b65991f759f");
    static readonly Guid _endMarker = Guid.Parse("f44e7f3f-5a86-4739-9b10-229cc624776c");
    // returns true if file is confirmed to be invalid, but only if it can be confirmed ( is new format and has end markers)
    static bool canConfirmFileIsNotValid(IIOProvider io, string[] fileKey) {
        var bytesForTwoGuids = 16 + 16;
        var fileLength = io.GetFileSizeOrZeroIfUnknown(fileKey);
        if (fileLength < bytesForTwoGuids) return false; // indeterminate, so cannot confirm invalid
        using var stream = io.OpenRead(fileKey, fileLength - bytesForTwoGuids);
        var g1 = stream.ReadGuid();
        var g2 = stream.ReadGuid();
        if (g1 != _hasEndMarker) return false; // indeterminate, so cannot confirm invalid
        return g2 != _endMarker; // true if invalid
    }
    public void SaveStatisticsState() {
        using (hold()) {
            if (!_setting.EnableStatistics) return;
            var allStats = _statByProp.Values.SelectMany(s => s).ToList();
            var anyDirty = allStats.Any(s => s.IsDirty) || _rowStat.IsDirty;
            if (!anyDirty) return;
            // make backup first, but never overwrite a good backup with a file that is
            // confirmed corrupt (a previous save may have failed halfway without a restart)
            if (!canConfirmFileIsNotValid(_io, _statFileKey))
                _io.CopyIfItExistsAndOverwrite(_statFileKey, _backupStatFile);
            _io.DeleteFileIfItExists(_statFileKey);
            using var stream = _io.OpenAppend(_statFileKey);
            stream.WriteString(_rowStat.Key);
            var rowBytes = getStatBytes(_rowStat);
            stream.WriteVerifiedInt(rowBytes.Length);
            stream.Append(rowBytes);
            stream.WriteVerifiedInt(allStats.Count);
            foreach (var stat in allStats) {
                stream.WriteString(stat.Key);
                var statBytes = getStatBytes(stat);
                stream.WriteVerifiedInt(statBytes.Length);
                stream.Append(statBytes);
            }
            // new format end markers to detect corrupted files
            stream.WriteGuid(_hasEndMarker);
            stream.WriteGuid(_endMarker);
        }
    }
    // A record is written in a buffer its thread keeps for the next one, and copied out when it is
    // done. It is written by the thread recording the entry, outside the lock, so no two records
    // ever share one - and one grown by an unusually large entry is let go of, not kept for good.
    [ThreadStatic] static BinaryWriter? _recordWriter;
    const int maxKeptRecordBuffer = 64 * 1024;
    static LogRecord getRecord(LogEntry entry) {
        var bw = _recordWriter ??= new BinaryWriter(new MemoryStream(512));
        var ms = (MemoryStream)bw.BaseStream;
        ms.SetLength(0);
        bw.Write(entry.Timestamp.Ticks);
        bw.Write(entry.Values.Count);
        foreach (var kv in entry.Values) {
            var dataType = getDataType(kv.Value);
            bw.Write(kv.Key);
            bw.Write((byte)dataType);
            switch (dataType) {
                case LogDataType.DateTime:
                    bw.Write(((DateTime)kv.Value).Ticks);
                    break;
                case LogDataType.TimeSpan:
                    bw.Write(((TimeSpan)kv.Value).Ticks);
                    break;
                case LogDataType.String:
                    bw.Write(kv.Value + string.Empty); // ensureing even objects with no ToString() can be stored and never return null
                    break;
                case LogDataType.Integer:
                    bw.Write((int)kv.Value);
                    break;
                case LogDataType.Double:
                    bw.Write((double)kv.Value);
                    break;
                case LogDataType.Bytes:
                    bw.Write(((byte[])kv.Value).Length);
                    bw.Write((byte[])kv.Value);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
        var record = new LogRecord(entry.Timestamp, ms.ToArray());
        if (ms.Capacity > maxKeptRecordBuffer) _recordWriter = null;
        return record;
    }
    static LogDataType getDataType(object value) => LogValues.StoredTypeOf(value);
    object forceToLegalType(object value) {
        if (value is double) return value;
        if (value is int) return value;
        if (value is string) return value;
        if (value is DateTime) return value;
        if (value is TimeSpan) return value;
        if (value is byte[]) return value;
        return value + string.Empty;
    }
    LogEntry getEntry(LogRecord record) {
        var entry = new LogEntry();
        var ms = new MemoryStream(record.Data);
        var br = new BinaryReader(ms);
        entry.Timestamp = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
        var noValues = br.ReadInt32();
        for (int i = 0; i < noValues; i++) {
            var key = br.ReadString();
            var value = getNextValue(br);
            if (_declaredKeys.TryGetValue(key, out var declared) && _setting.Properties.TryGetValue(declared, out var prop)) {
                // a value recorded before the column changed type is read as the type it has now,
                // and kept as it was stored when it has no reading as that type: showing a zero in
                // its place would show something that was never recorded
                entry.Values[declared] = LogValues.TryConvert(value, prop.DataType, out var converted) ? converted : value;
            } else {
                entry.Values[key] = forceToLegalType(value);
            }
        }
        return entry;
    }
    object getNextValue(BinaryReader br) {
        var dtype = (LogDataType)br.ReadByte();
        switch (dtype) {
            case LogDataType.DateTime:
                return new DateTime(br.ReadInt64(), DateTimeKind.Utc);
            case LogDataType.TimeSpan:
                return new TimeSpan(br.ReadInt64());
            case LogDataType.String:
                return br.ReadString();
            case LogDataType.Integer:
                return br.ReadInt32();
            case LogDataType.Double:
                return br.ReadDouble();
            case LogDataType.Bytes:
                var len = br.ReadInt32();
                return br.ReadBytes(len);
            default:
                throw new NotImplementedException();
        }

    }
    public void DeleteAll() {
        using (hold()) {
            EnforceDateLimit(DateTime.MaxValue);
            DeleteStatistics();
        }
    }
    public void DeleteStatistics() {
        using (hold()) {
            _io.DeleteFileIfItExists(_statFileKey);
            _io.DeleteFileIfItExists(_backupStatFile);
            loadAllStatistics();
        }
    }
    public void EnforceDateLimit(DateTime to) {
        using (hold()) {
            _logStream.Delete(to);
            _logTextStream.Delete(to);
        }
    }
    public DateTime? GetTimestampOfFirstRecord() {
        using (hold()) {
            return _logStream.GetTimestampOfFirstRecord();
        }
    }
    public DateTime? GetTimestampOfLastRecord() {
        using (hold()) {
            return _logStream.GetTimestampOfLastRecord();
        }
    }
    public Dictionary<string, List<StatisticsInfo>> GetAvailableStatisticsByProperty() {
        using (hold()) {
            var result = new Dictionary<string, List<StatisticsInfo>>();
            if (_statByProp != null) {
                foreach (var kv in _statByProp) {
                    var propName = kv.Key;
                    foreach (var stat in kv.Value) {
                        if (!result.ContainsKey(propName)) result.Add(propName, new());
                        result[propName].Add(stat.Info);
                    }
                }
            }
            return result;
        }
    }
    public IEnumerable<Interval<int>> AnalyseRows(IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            return _rowStat.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<int>> AnalyseCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<int>> AnalyseIntegerSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsIntegerSum>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<double>> AnalyseDoubleSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<double>>();
            var cn = stats.OfType<StatisticsDoubleSum>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<double>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<AvgMinMax<double>>>();
            var cn = stats.OfType<StatisticsAvgMinMax>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<AvgMinMax<double>>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => new AvgMinMax<double>(i.Average, i.Min, i.Max))).ToList();
        }
    }
    public IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<CountSumAvgMinMax<double>>>();
            var cn = stats.OfType<StatisticsCountSumAvgMinMax>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<CountSumAvgMinMax<double>>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => new CountSumAvgMinMax<double>(i.RecordCount, i.Sum, i.Average, i.Min, i.Max))).ToList();
        }
    }
    public IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<Dictionary<string, int>>>();
            var cn = stats.OfType<StatisticsGroupCount>().FirstOrDefault();
            // ensureing dictionary is copied to avoid concurrency issues
            return cn == null ? new() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => i.Values.ToDictionary(k => k.Key, v => v.Value))).ToList();
        }
    }
    public IEnumerable<Interval<int>> AnalyseUniqueCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsUniqueCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated).Select(c => c.Map(i => i.HashCount())).ToList();
        }
    }
    public IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsEstimatedUniqueCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated).Select(c => c.Map(i => i.EstimateCount())).ToList();
        }
    }
    public Interval<int> AnalyseCombinedRows(IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (hold()) {
            return _rowStat.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<int> AnalyseCombinedCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsCount>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<int> AnalyseCombinedIntegerSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsIntegerSum>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<double> AnalyseCombinedDoubleSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsDoubleSum>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsAvgMinMax>().FirstOrDefault();
            var i = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return i.Map(i => new AvgMinMax<double>(i.Average, i.Min, i.Max));
        }
    }
    public Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval = true, DateTime? nowSimulated = null) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsCountSumAvgMinMax>().FirstOrDefault();
            var i = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return i.Map(i => new CountSumAvgMinMax<double>(i.RecordCount, i.Sum, i.Average, i.Min, i.Max));
        }
    }
    public Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (hold()) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsGroupCount>().FirstOrDefault();
            var value = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return value.Map(v => v.Values.ToDictionary(k => k.Key, v => v.Value));
        }
    }
    public void RebuildStatistics() {
        if (!_setting.EnableStatistics) return;
        var firstRecord = GetTimestampOfFirstRecord();
        var lastRecord = GetTimestampOfLastRecord();
        if (firstRecord == null || lastRecord == null) return; // no records return
        DeleteStatistics();
        var filesize = GetTotalFileSize();
        var chunkCount = filesize / (10 * 1024 * 1024);
        // around 10MB chunks, assuming linear distribution of data ( which is a big assumption...).
        // More work neeed later to handle large dataset.
        var end = lastRecord.Value.AddTicks(1); // Extract range is exclusive, include records at the exact last timestamp
        var deltaTimePerChunk = (end - firstRecord.Value).Ticks / (chunkCount + 1);
        if (deltaTimePerChunk < 1) deltaTimePerChunk = 1;
        var currentFrom = firstRecord.Value;
        while (currentFrom < end) {
            var currentTo = new DateTime(Math.Min(currentFrom.Ticks + deltaTimePerChunk, end.Ticks), DateTimeKind.Utc);
            var entries = Extract(currentFrom, currentTo, 0, int.MaxValue, false, out _);
            using (hold()) {
                foreach (var entry in entries) {
                    _rowStat.RecordIfPossible(entry.Timestamp, _true);
                    foreach (var value in entry.Values) {
                        if (_statByProp.TryGetValue(value.Key, out var stats)) {
                            foreach (var stat in stats) {
                                stat.RecordIfPossible(entry.Timestamp, value.Value);
                            }
                        }
                    }
                }
            }
            currentFrom = currentTo;
        }
        SaveStatisticsState();
    }
    volatile bool _disposed; // read without the lock by Record, to turn an entry away early
    public void Dispose() {
        using (hold()) {
            if (_disposed) return;
            SaveStatisticsState();
            _logStream.Dispose();
            _logTextStream.Dispose();
            _disposed = true;
        }
    }
    internal void EnforceSizeLimit(int maxTotalSizeOfLogFilesInMb) {
        using (hold()) {
            _logStream.DeleteLargeLog(maxTotalSizeOfLogFilesInMb);
        }
    }
}
