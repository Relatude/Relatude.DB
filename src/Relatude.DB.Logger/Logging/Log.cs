using Relatude.DB.Common;
using Relatude.DB.Hash.xxHash;
using Relatude.DB.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Relatude.DB.Logging.Statistics;

namespace Relatude.DB.Logging;
/// <summary>How long the logs of one store wait for each other's callers. Shared by the store and its logs.</summary>
internal sealed class LogOptions {
    // a page, a graph or a search waits this long for a log that is busy, then says what it is busy with
    public TimeSpan ReadWait = TimeSpan.FromSeconds(10);
    // the sizes and first and last entry the overview pages poll: last known figures beat a wait
    public TimeSpan SummaryWait = TimeSpan.FromSeconds(1);
    // one recording to a log whose backlog is full waits this long, then is let go of and counted
    public TimeSpan BacklogWait = TimeSpan.FromSeconds(1);
    // a log being closed waits this long for whoever holds it, then is abandoned (see Log.Abandon)
    public TimeSpan DisposeWait = TimeSpan.FromSeconds(10);
}
// threadsafe
internal class Log : IDisposable {
    // Taken through hold() and tryHold(), never on its own: see there.
    readonly object _lock = new();
    readonly LogOptions _options;
    static readonly TimeSpan _statisticsOpenWait = TimeSpan.FromSeconds(5);
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
        var key = "stat_" + property + "_" + info.Resolution + "_" + settings.FirstDayOfWeek + "_" + info.StatisticsType;
        // ...and the same goes for what a statistic about positions is measured against: distances
        // from another reference point, or cells of another size, are another statistic. Statistics
        // without parameters keep the key they always had.
        var parameters = info.ParameterSignature();
        return parameters.Length == 0 ? key : key + "_" + parameters.XXH64Hash().ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
    public Log(LogSettings settings, IIOProvider io, LogOptions? options = null) {
        _setting = settings;
        _io = io;
        _options = options ?? new();
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
        var isGeo = property.DataType is LogDataType.GeoCoordinate;
        // a statistic about positions without what it measures against is left out, like any other
        // statistic the column cannot keep (the settings say so where they are validated)
        var usable = info.ProblemWithParameters() == null;
        return info.StatisticsType switch {
            StatisticsType.Count => new StatisticsCount(info, f, key),
            StatisticsType.Sum when property.DataType is LogDataType.Integer => new StatisticsIntegerSum(info, f, key),
            StatisticsType.Sum when property.DataType is LogDataType.Double => new StatisticsDoubleSum(info, f, key),
            StatisticsType.AvgMinMax when isNumeric => new StatisticsAvgMinMax(info, f, key),
            StatisticsType.CountSumAvgMinMax when isNumeric => new StatisticsCountSumAvgMinMax(info, f, key),
            StatisticsType.UniqueCountWithValues when !isBytes => new StatisticsGroupCount(info, f, key),
            StatisticsType.UniqueCountHashedValues when !isBytes => new StatisticsUniqueCount(info, f, key),
            StatisticsType.UniqueCountEstimate when !isBytes => new StatisticsEstimatedUniqueCount(info, f, key),
            StatisticsType.GeoSpread when isGeo => new StatisticsGeoSpread(info, f, key),
            StatisticsType.GeoDistance when isGeo && usable => new StatisticsGeoDistance(info, f, key),
            StatisticsType.GeoDistanceBands when isGeo && usable => new StatisticsGeoBands(info, f, key),
            StatisticsType.GeoZones when isGeo && usable => new StatisticsGeoZones(info, f, key),
            StatisticsType.GeoCoverage when isGeo && usable => new StatisticsGeoCoverage(info, f, key),
            StatisticsType.GeoHeatmap when isGeo && usable => new StatisticsGeoHeatmap(info, f, key),
            _ => null,
        };
    }
    public void FlushToDiskNow() {
        if (_setting.EnableLog) {
            using (hold("writing to disk")) flushNow();
        }
    }
    void flushNow() {
        _logStream.FlushToDisk();
        if (_setting.EnableLogTextFormat) _logTextStream.FlushToDisk();
    }
    // set by FlushWhenFree for whoever lets go of the log next
    volatile bool _flushWanted;
    /// <summary>
    /// Writes what is buffered to disk now if the log is free, or has the one holding it do so as it lets go:
    /// never waits. For entries worth having on disk soon, but not worth holding up whoever recorded them.
    /// </summary>
    public void FlushWhenFree() {
        if (!_setting.EnableLog) return;
        _flushWanted = true;
        if (tryHold(out var held, "writing to disk")) held.Dispose();
    }
    void flushIfWanted() {
        if (!_flushWanted || _disposed) return;
        _flushWanted = false;
        flushNow();
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
    /// be on disk when this returns does wait. One that finds the backlog full waits a moment
    /// (<see cref="LogOptions.BacklogWait"/>) and is then let go of and counted: a log held up for
    /// longer - a request to blob storage that does not come back - must not hold up the queries and
    /// transactions it is recording.
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
        if (flushToDisk) {
            using (hold("recording")) recordNow(pending, true);
        } else if (tryHold(out var held, "recording")) {
            using (held) recordNow(pending, false);
        } else if (Volatile.Read(ref _backlogCount) < MaxBacklog) {
            leave(pending);
        } else if (tryHold(out held, "recording", _options.BacklogWait)) {
            using (held) recordNow(pending, false);
        } else {
            Interlocked.Increment(ref _droppedEntries);
        }
    }
    long _droppedEntries;
    void recordNow(Pending pending, bool flushToDisk) {
        // a log replaced or removed while a caller still held it: its files belong to the log
        // that took its place, and writing here would reopen them behind that one's back
        if (_disposed) return;
        var entry = pending.Entry;
        if (pending.Record != null) {
            _noFiles = false;
            _logStream.Record(pending.Record, flushToDisk);
            if (_setting.EnableLogTextFormat) _logTextStream.Record(entry, flushToDisk);
            if (_spanKnown) widenSpan(entry.Timestamp, entry.Timestamp);
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
    // does not keep a backlog that grows for as long as it is busy: past it, entries wait a moment
    // for the log and are then let go of (see Record).
    readonly ConcurrentQueue<Pending> _backlog = new();
    int _backlogCount;
    internal const int MaxBacklog = 20_000;
    // Takes the log, the way everything in this class takes it: the backlog is recorded first, so
    // whatever is read, saved or flushed holding the log includes every entry recorded before. What it
    // is taken for is kept, so a caller that finds it busy can say what it is busy with.
    Held hold(string what) {
        Monitor.Enter(_lock);
        return entered(what);
    }
    bool tryHold(out Held held, string what, TimeSpan wait = default) {
        held = default;
        if (!Monitor.TryEnter(_lock, wait)) return false;
        held = entered(what);
        return true;
    }
    // what a reader takes the log with: it waits a while for one that is busy, then says why it gave up
    Held holdToRead(string what) {
        var wait = _options.ReadWait;
        if (tryHold(out var held, what, wait)) return held;
        throw new LogBusyException(_setting.Key, _setting.Name, _busyWith, busySinceUtc(), wait);
    }
    // only touched holding the lock, by the thread holding it; the two busy fields are read without it
    int _depth;
    volatile string? _busyWith;
    long _busySinceTicks;
    DateTime? busySinceUtc() {
        var ticks = Volatile.Read(ref _busySinceTicks);
        return _busyWith != null && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }
    // the log just taken, handed over once the backlog is recorded
    Held entered(string what) {
        if (_depth++ == 0) {
            Volatile.Write(ref _busySinceTicks, DateTime.UtcNow.Ticks);
            _busyWith = what;
        }
        try {
            recordBacklog();
        } catch {
            exit();
            throw;
        }
        return new(this);
    }
    void exit() {
        if (--_depth == 0) _busyWith = null;
        Monitor.Exit(_lock);
    }
    void release() {
        try {
            recordBacklog(); // what was left while this held the log
            flushIfWanted();
        } finally {
            exit();
        }
        // ...and what was left in the moment between the two, by a caller that found the log still
        // held: it would otherwise wait for the next caller to come along
        if ((!_backlog.IsEmpty || _flushWanted) && tryHold(out _, "recording")) {
            try {
                flushIfWanted();
            } finally {
                exit(); // not release(): that would come back here
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
        if (tryHold(out var held, "recording")) held.Dispose();
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
            // an empty position is how a GeoCoordinate says there is none: no value, like a null
            if (kv.Value == null || kv.Value is GeoCoordinate { IsEmpty: true }) continue;
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
        using (holdToRead("reading entries")) {
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
        using (holdToRead("searching entries")) {
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
    /// <summary>The log's files and its statistics, from one listing of its folder; the last size known
    /// when the log is busy, as for <see cref="GetFilesSummary"/>.</summary>
    public long GetTotalFileSize() {
        if (!tryHold(out var held, "listing its files", _options.SummaryWait)) return _lastSummary?.TotalBytes ?? 0;
        using (held) {
            var files = listFiles();
            return files.LogBytes + files.StatisticsBytes;
        }
    }
    LogFilesSummary? _lastSummary;
    /// <summary>
    /// Sizes and first and last record from one listing of the log's folder, where asking for each on its
    /// own lists it four times and looks up every file's size twice. The first and last record are read
    /// off the files once and then kept up as entries are written. What the overview pages poll, so a log
    /// that stays busy for more than a moment answers with the figures it gave last, marked stale.
    /// </summary>
    public LogFilesSummary GetFilesSummary() {
        if (!tryHold(out var held, "listing its files", _options.SummaryWait)) return (_lastSummary ?? LogFilesSummary.Empty) with { Stale = true };
        using (held) {
            var files = listFiles();
            DateTime? first, last;
            try {
                ensureSpan([.. files.Binary.Where(f => f.File.Size > 0).Select(f => f.Date)]);
                (first, last) = (_first, _last);
            } catch {
                // a file held elsewhere: the sizes are still worth having
                (first, last) = (_lastSummary?.FirstRecordUtc, _lastSummary?.LastRecordUtc);
            }
            return _lastSummary = new(first, last, files.LogBytes, files.StatisticsBytes);
        }
    }
    // The first and last record on file, read off the files once and widened as entries are written after
    // that; forgotten when files are deleted. Only touched holding the log.
    bool _spanKnown;
    DateTime? _first, _last;
    void ensureSpan(IReadOnlyList<DateTime>? datesOfFilesWithContent = null) {
        if (_spanKnown) return;
        if (datesOfFilesWithContent == null) datesOfFilesWithContent = [.. listFiles().Binary.Where(f => f.File.Size > 0).Select(f => f.Date)];
        (_first, _last) = _logStream.GetFirstAndLastRecord(datesOfFilesWithContent);
        _spanKnown = true;
        // what could not be written yet is recorded all the same
        var (bufferedFirst, bufferedLast) = _logStream.BufferedSpan();
        if (bufferedFirst is DateTime f && bufferedLast is DateTime l) widenSpan(f, l);
    }
    void widenSpan(DateTime first, DateTime last) {
        if (_first == null || first < _first) _first = first;
        if (_last == null || last > _last) _last = last;
    }
    void forgetSpan() => _spanKnown = false;
    (List<(DateTime Date, FileMeta File)> Binary, long LogBytes, long StatisticsBytes) listFiles() {
        _logStream.ReleaseFiles();
        var listing = _io.GetFiles(FileKeyUtility.Logger_FolderKey(_setting.Key));
        var binary = FileKeyUtility.Logger_FilesBin(listing, _setting.Key, _setting.FileInterval);
        var text = FileKeyUtility.Logger_FilesTxt(listing, _setting.Key, _setting.FileInterval);
        var statisticsBytes = listing.FirstOrDefault(f => f.KeyOf().IsSameKey(_statFileKey))?.Size ?? 0;
        return (binary, binary.Sum(f => f.File.Size) + text.Sum(f => f.File.Size), statisticsBytes);
    }
    public long GetLogFileSize() {
        using (holdToRead("listing its files")) {
            return _logStream.Size() + _logTextStream.Size();
        }
    }
    public long GetStatisticsFileSize() {
        using (holdToRead("listing its files")) {
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
        using (hold("reading its statistics file")) {
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

            using var stream = _io.OpenRead(_statFileKey, 0, _statisticsOpenWait);
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
        using var stream = io.OpenRead(fileKey, fileLength - bytesForTwoGuids, _statisticsOpenWait);
        var g1 = stream.ReadGuid();
        var g2 = stream.ReadGuid();
        if (g1 != _hasEndMarker) return false; // indeterminate, so cannot confirm invalid
        return g2 != _endMarker; // true if invalid
    }
    // the statistics file is small and held only while it is saved: a short wait for it, never the long one
    // the database's own files wait for a host handing over
    void copyStatisticsFile(string[] from, string[] to) {
        if (_io.DoesNotExistOrIsEmpty(from)) return;
        _io.DeleteFileIfItExists(to);
        using var read = _io.OpenRead(from, 0, _statisticsOpenWait);
        using var write = _io.OpenAppend(to, _statisticsOpenWait);
        write.Append(read.Read((int)read.Length));
    }
    public void SaveStatisticsState() {
        using (hold("saving its statistics")) {
            if (!_setting.EnableStatistics) return;
            var allStats = _statByProp.Values.SelectMany(s => s).ToList();
            var anyDirty = allStats.Any(s => s.IsDirty) || _rowStat.IsDirty;
            if (!anyDirty) return;
            // make backup first, but never overwrite a good backup with a file that is
            // confirmed corrupt (a previous save may have failed halfway without a restart)
            if (!canConfirmFileIsNotValid(_io, _statFileKey)) copyStatisticsFile(_statFileKey, _backupStatFile);
            _io.DeleteFileIfItExists(_statFileKey);
            using var stream = _io.OpenAppend(_statFileKey, _statisticsOpenWait);
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
                case LogDataType.GeoCoordinate:
                    // the position's own lossless 8 bytes: the Morton code the geo index sorts by
                    bw.Write(((GeoCoordinate)kv.Value).StorageValue);
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
        if (value is GeoCoordinate) return value;
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
            case LogDataType.GeoCoordinate:
                return GeoCoordinate.TryFromStorageValue(br.ReadUInt64(), out var position) ? position : GeoCoordinate.Empty;
            default:
                throw new NotImplementedException();
        }

    }
    public void DeleteAll() {
        using (hold("deleting its files")) {
            EnforceDateLimit(DateTime.MaxValue);
            DeleteStatistics();
        }
    }
    public void DeleteStatistics() {
        using (hold("deleting its statistics")) {
            _io.DeleteFileIfItExists(_statFileKey);
            _io.DeleteFileIfItExists(_backupStatFile);
            loadAllStatistics();
        }
    }
    public void EnforceDateLimit(DateTime to) {
        using (hold("deleting its files")) {
            forgetSpan();
            _logStream.Delete(to);
            _logTextStream.Delete(to);
        }
    }
    public DateTime? GetTimestampOfFirstRecord() {
        using (holdToRead("reading its first entry")) {
            ensureSpan();
            return _first;
        }
    }
    public DateTime? GetTimestampOfLastRecord() {
        using (holdToRead("reading its last entry")) {
            ensureSpan();
            return _last;
        }
    }
    public Dictionary<string, List<StatisticsInfo>> GetAvailableStatisticsByProperty() {
        using (holdToRead("reading its statistics")) {
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
        using (holdToRead("reading its statistics")) {
            return _rowStat.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<int>> AnalyseCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<int>> AnalyseIntegerSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsIntegerSum>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<double>> AnalyseDoubleSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<double>>();
            var cn = stats.OfType<StatisticsDoubleSum>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<double>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<AvgMinMax<double>>>();
            var cn = stats.OfType<StatisticsAvgMinMax>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<AvgMinMax<double>>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => new AvgMinMax<double>(i.Average, i.Min, i.Max))).ToList();
        }
    }
    public IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null, StatisticsType? statistic = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<CountSumAvgMinMax<double>>>();
            var cn = pick<StatisticsCountSumAvgMinMax>(stats, statistic);
            return cn == null ? Array.Empty<Interval<CountSumAvgMinMax<double>>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => new CountSumAvgMinMax<double>(i.RecordCount, i.Sum, i.Average, i.Min, i.Max))).ToList();
        }
    }
    public IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null, StatisticsType? statistic = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<Dictionary<string, int>>>();
            var cn = pick<StatisticsGroupCount>(stats, statistic);
            // ensureing dictionary is copied to avoid concurrency issues
            return cn == null ? new() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => i.Values.ToDictionary(k => k.Key, v => v.Value))).ToList();
        }
    }
    /// <summary>
    /// The statistic of a CLR type a column keeps. Several statistics about positions are kept by the
    /// aggregators of others (distances by a CountSumAvgMinMax, zones by a count per value), so the
    /// one meant is found by its type when a caller names it, and otherwise the plain one wins - which
    /// is what every caller asking without a name has always got.
    /// </summary>
    static T? pick<T>(List<IStatistics> stats, StatisticsType? statistic) where T : class, IStatistics {
        if (statistic is StatisticsType wanted) return stats.OfType<T>().FirstOrDefault(s => s.Info.StatisticsType == wanted);
        return stats.OfType<T>().FirstOrDefault(s => s.GetType() == typeof(T)) ?? stats.OfType<T>().FirstOrDefault();
    }
    public IEnumerable<Interval<GeoSpread>> AnalyseGeoSpread(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<GeoSpread>>();
            var cn = pick<StatisticsGeoSpread>(stats, null);
            return cn == null ? Array.Empty<Interval<GeoSpread>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(m => m.Summarize())).ToList();
        }
    }
    public Interval<GeoSpread> AnalyseCombinedGeoSpread(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = pick<StatisticsGeoSpread>(stats, null);
            var value = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return value.Map(m => m.Summarize());
        }
    }
    public IEnumerable<Interval<GeoHeatmap>> AnalyseGeoHeatmap(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<GeoHeatmap>>();
            var cn = pick<StatisticsGeoHeatmap>(stats, null);
            // copied out while the log is held: the grids go on being counted into
            return cn == null ? Array.Empty<Interval<GeoHeatmap>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(g => g.Snapshot())).ToList();
        }
    }
    public Interval<GeoHeatmap> AnalyseCombinedGeoHeatmap(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, int maxCells) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = pick<StatisticsGeoHeatmap>(stats, null);
            if (cn == null) return new(fromUtc, toUtc);
            return cn.GetCombinedValue(intervalType, fromUtc, toUtc, maxCells).Map(g => g.Snapshot());
        }
    }
    public IEnumerable<Interval<int>> AnalyseUniqueCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsUniqueCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated).Select(c => c.Map(i => i.HashCount())).ToList();
        }
    }
    public IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null, StatisticsType? statistic = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = pick<StatisticsEstimatedUniqueCount>(stats, statistic);
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated).Select(c => c.Map(i => i.EstimateCount())).ToList();
        }
    }
    public Interval<int> AnalyseCombinedRows(IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (holdToRead("reading its statistics")) {
            return _rowStat.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<int> AnalyseCombinedCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsCount>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<int> AnalyseCombinedIntegerSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsIntegerSum>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<double> AnalyseCombinedDoubleSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsDoubleSum>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsAvgMinMax>().FirstOrDefault();
            var i = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return i.Map(i => new AvgMinMax<double>(i.Average, i.Min, i.Max));
        }
    }
    public Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval = true, DateTime? nowSimulated = null, StatisticsType? statistic = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = pick<StatisticsCountSumAvgMinMax>(stats, statistic);
            var i = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return i.Map(i => new CountSumAvgMinMax<double>(i.RecordCount, i.Sum, i.Average, i.Min, i.Max));
        }
    }
    public Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, StatisticsType? statistic = null) {
        using (holdToRead("reading its statistics")) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = pick<StatisticsGroupCount>(stats, statistic);
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
            using (hold("rebuilding its statistics")) {
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
        if (!tryHold(out var held, "closing", _options.DisposeWait)) {
            Abandon();
            return;
        }
        using (held) {
            if (_disposed) return;
            _disposed = true;
            try { SaveStatisticsState(); }
            finally { closeFiles(); }
        }
    }
    void closeFiles() {
        _logStream.Dispose();
        _logTextStream.Dispose();
    }
    /// <summary>
    /// Lets go of a log without waiting for whoever holds it - a page read off blob storage whose request
    /// does not come back, say. It takes no more entries at once, and closes its files once it is let go
    /// of. Its statistics since they were last saved are not saved: the log that takes its place has read
    /// the file already, and saving them later would write over what that one saves.
    /// </summary>
    internal void Abandon() {
        if (_disposed) return;
        _disposed = true;
        var busy = _busyWith;
        FileOpenRetry.DefaultLog?.Invoke("The log \"" + _setting.Key + "\" was let go of while busy" + (busy == null ? "" : " " + busy)
            + ": its files are closed once that is done, and its statistics since they were last saved are not kept.");
        _ = Task.Run(async () => {
            for (var attempt = 0; attempt < 3600; attempt++) {
                if (Monitor.TryEnter(_lock)) {
                    try { closeFiles(); } catch { } finally { Monitor.Exit(_lock); }
                    return;
                }
                await Task.Delay(1000);
            }
        });
    }
    /// <summary>
    /// Writes what is buffered and closes the log's files, if the log can be had within <paramref name="wait"/>:
    /// what lets another process, or another log, open them. False when it stayed busy.
    /// </summary>
    internal bool TryReleaseFiles(TimeSpan wait) {
        if (!tryHold(out var held, "letting go of its files", wait)) return false;
        using (held) {
            _logStream.ReleaseFiles();
            _logTextStream.ReleaseFiles();
        }
        return true;
    }
    /// <summary>What the log is busy with and since when, and whether its entries reach its files. Read without waiting.</summary>
    internal LogCondition GetCondition() {
        var busy = _busyWith;
        var since = busySinceUtc();
        var problem = _logStream.Problem ?? _logTextStream.Problem;
        return new(busy, busy == null ? null : since, problem?.Message, problem?.SinceUtc, _logStream.DroppedRecords + Interlocked.Read(ref _droppedEntries));
    }
    // set when the last enforcement found no files and cleared by the next record written: a log that writes
    // nothing has nothing to age out, and its folder is not listed again every minute
    bool _noFiles;
    /// <summary>
    /// Deletes the files that ended before <paramref name="deleteBefore"/>, then the oldest binary files
    /// until the rest fit <paramref name="maxTotalSizeOfLogFilesInMb"/> (0 for no limit) - the current
    /// interval's file is never deleted. Decided from one listing of the log's folder; the open files are
    /// let go of only when something is deleted.
    /// </summary>
    internal void EnforceLimits(DateTime? deleteBefore, int maxTotalSizeOfLogFilesInMb) {
        using (hold("deleting old files")) {
            if (_noFiles) return;
            _logStream.WriteBuffered();
            var interval = _setting.FileInterval;
            var listing = _io.GetFiles(FileKeyUtility.Logger_FolderKey(_setting.Key), recursive: false);
            var binary = FileKeyUtility.Logger_FilesBin(listing, _setting.Key, interval);
            var text = FileKeyUtility.Logger_FilesTxt(listing, _setting.Key, interval);
            if (binary.Count == 0 && text.Count == 0) {
                _noFiles = true;
                return;
            }
            var doomed = new List<(DateTime Date, FileMeta File)>();
            if (deleteBefore is DateTime before) doomed.AddRange(binary.Concat(text).Where(f => f.Date.AddInterval(interval) <= before));
            if (maxTotalSizeOfLogFilesInMb > 0) {
                var kept = binary.Where(f => !doomed.Contains(f)).ToList();
                var total = kept.Sum(f => f.File.Size);
                var max = maxTotalSizeOfLogFilesInMb * 1024L * 1024L;
                var current = DateTime.UtcNow.Floor(interval);
                foreach (var f in kept.Where(f => f.Date != current)) {
                    if (total <= max) break;
                    total -= f.File.Size;
                    doomed.Add(f);
                }
            }
            if (doomed.Count == 0) return;
            forgetSpan();
            _logStream.ReleaseFiles();
            _logTextStream.ReleaseFiles();
            foreach (var f in doomed) _io.DeleteFileIfItExists(f.File.KeyOf());
        }
    }
}
