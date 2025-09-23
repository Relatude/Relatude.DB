using Relatude.DB.IO;
using System.Collections.ObjectModel;
using Relatude.DB.Logging.Statistics;

namespace Relatude.DB.Logging;
// threadsafe
internal class Log : IDisposable {
    object _lock = new();
    public LogSettings Setting { get => _setting; }
    readonly LogStream _logStream;
    StatisticsCount _rowStat = null!; // initialized in loadAllStatistics
    ReadOnlyDictionary<string, List<IStatistics>> _statByProp = null!; // initialized in loadAllStatistics
    readonly LogSettings _setting;
    readonly IIOProvider _io;
    readonly LogTextStream _logTextStream;
    readonly string _statFileKey;
    readonly string _backupStatFile;
    static string getStatisticsFileKey(string property, StatisticsInfo info, LogSettings settings) {
        // a unique that prevents collisions between different statistical settings
        // if a change is made to the stat settings it will simply have a different key and last state will be ignored and not corrupt the new state
        return "stat_" + property + "_" + info.Resolution + "_" + settings.FirstDayOfWeek + "_" + info.StatisticsType;
    }
    public Log(LogSettings settings, IIOProvider io) {
        _setting = settings;
        _io = io;
        _logStream = new(_io, _setting.Key, _setting.Compressed, _setting.FileInterval, _setting.FileNamePrefix, _setting.FileNameDelimiter, _setting.FileNameExtension);
        _logTextStream = new LogTextStream(io, _setting.Key, _setting.FileInterval, _setting.FileNamePrefix, _setting.FileNameDelimiter, ".txt");
        _statFileKey = (string.IsNullOrEmpty(_setting.FileNamePrefix) ? "" : _setting.FileNamePrefix + _setting.FileNameDelimiter) + _setting.Key + _setting.FileNameDelimiter + "statistics" + _setting.FileNameExtension;
        _backupStatFile = _statFileKey + ".bkup";
        loadAllStatistics();
    }
    void loadAllStatistics() {
        var ri = new StatisticsInfo(StatisticsType.Count, _setting.ResolutionRowStats);
        _rowStat = new StatisticsCount(ri, _setting.FirstDayOfWeek, getStatisticsFileKey("r_o_w", ri, _setting));
        var statByProp = new Dictionary<string, List<IStatistics>>();
        foreach (var kv in _setting.Properties) {
            if (kv.Value.Statistics != null) {
                foreach (var info in kv.Value.Statistics) {
                    if (info == null) continue;
                    var stat = createStatisticsIfPossible(kv.Key, kv.Value, info);
                    if (stat == null) continue;
                    if (!statByProp.ContainsKey(kv.Key)) statByProp.Add(kv.Key, new());
                    statByProp[kv.Key].Add(stat);
                }
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
        switch (property.DataType) {
            case LogDataType.DateTime:
                switch (info.StatisticsType) {
                    case StatisticsType.Count: return new StatisticsCount(info, f, key);
                    case StatisticsType.UniqueCountWithValues: return new StatisticsGroupCount(info, f, key);
                    case StatisticsType.UniqueCountHashedValues: return new StatisticsUniqueCount(info, f, key);
                    case StatisticsType.UniqueCountEstimate: return new StatisticsEstimatedUniqueCount(info, f, key);
                    case StatisticsType.Sum:
                    case StatisticsType.AvgMinMax:
                    default: return null;
                }
            case LogDataType.TimeSpan:
                switch (info.StatisticsType) {
                    case StatisticsType.Count: return new StatisticsCount(info, f, key);
                    case StatisticsType.UniqueCountWithValues: return new StatisticsGroupCount(info, f, key);
                    case StatisticsType.UniqueCountHashedValues: return new StatisticsUniqueCount(info, f, key);
                    case StatisticsType.UniqueCountEstimate: return new StatisticsEstimatedUniqueCount(info, f, key);
                    case StatisticsType.Sum:
                    case StatisticsType.AvgMinMax:
                    default: return null;
                }
            case LogDataType.String:
                switch (info.StatisticsType) {
                    case StatisticsType.Count: return new StatisticsCount(info, f, key);
                    case StatisticsType.UniqueCountWithValues: return new StatisticsGroupCount(info, f, key);
                    case StatisticsType.UniqueCountHashedValues: return new StatisticsUniqueCount(info, f, key);
                    case StatisticsType.UniqueCountEstimate: return new StatisticsEstimatedUniqueCount(info, f, key);
                    case StatisticsType.Sum:
                    case StatisticsType.AvgMinMax:
                    default: return null;
                }
            case LogDataType.Integer:
                switch (info.StatisticsType) {
                    case StatisticsType.Count: return new StatisticsCount(info, f, key);
                    case StatisticsType.Sum: return new StatisticsIntegerSum(info, f, key);
                    case StatisticsType.AvgMinMax: return new StatisticsAvgMinMax(info, f, key);
                    case StatisticsType.CountSumAvgMinMax: return new StatisticsCountSumAvgMinMax(info, f, key);
                    case StatisticsType.UniqueCountWithValues: return new StatisticsGroupCount(info, f, key);
                    case StatisticsType.UniqueCountHashedValues: return new StatisticsUniqueCount(info, f, key);
                    case StatisticsType.UniqueCountEstimate: return new StatisticsEstimatedUniqueCount(info, f, key);
                    default: return null;
                }
            case LogDataType.Double:
                switch (info.StatisticsType) {
                    case StatisticsType.Count: return new StatisticsCount(info, f, key);
                    case StatisticsType.Sum: return new StatisticsDoubleSum(info, f, key);
                    case StatisticsType.AvgMinMax: return new StatisticsAvgMinMax(info, f, key);
                    case StatisticsType.CountSumAvgMinMax: return new StatisticsCountSumAvgMinMax(info, f, key);
                    case StatisticsType.UniqueCountWithValues: return new StatisticsGroupCount(info, f, key);
                    case StatisticsType.UniqueCountHashedValues: return new StatisticsUniqueCount(info, f, key);
                    case StatisticsType.UniqueCountEstimate: return new StatisticsEstimatedUniqueCount(info, f, key);
                    default: return null;
                }
            case LogDataType.Bytes:
                switch (info.StatisticsType) {
                    case StatisticsType.Count: return new StatisticsCount(info, f, key);
                    case StatisticsType.Sum:
                    case StatisticsType.AvgMinMax:
                    case StatisticsType.UniqueCountWithValues:
                    case StatisticsType.UniqueCountHashedValues:
                    case StatisticsType.UniqueCountEstimate:
                    default: return null;
                }
            default: return null;
        }
    }
    public void FlushToDiskNow() {
        if (_setting.EnableLog) {
            lock (_lock) {
                _logStream.FlushToDisk();
                if (_setting.EnableLogTextFormat) _logTextStream.FlushToDisk();
            }
        }
    }
    public void Record(LogEntry entry, bool flushToDisk, bool? forceLogging = null, bool? forceStatistics = null) {
        lock (_lock) {
            if (_setting.EnableLog || (forceLogging.HasValue && forceLogging.Value)) {
                var record = getRecord(entry);
                _logStream.Record(record, flushToDisk);
                if (_setting.EnableLogTextFormat) _logTextStream.Record(entry, flushToDisk);
            }
            if (_setting.EnableStatistics || (forceStatistics.HasValue && forceStatistics.Value)) {
                _rowStat.RecordIfPossible(entry.Timestamp, true);
                foreach (var value in entry.Values) {
                    if (_statByProp.TryGetValue(value.Key, out var stats)) {
                        foreach (var stat in stats) {
                            stat.RecordIfPossible(entry.Timestamp, value.Value);
                        }
                    }
                }
            }
        }
    }
    public IEnumerable<LogEntry> Extract(DateTime from, DateTime to, int skip, int take, out int total) {
        lock (_lock) {
            var records = _logStream.Extract(from, to, skip, take, out total);
            return records.Select(r => getEntry(r));
        }
    }
    public long GetTotalFileSize() => GetLogFileSize() + GetStatisticsFileSize();
    public long GetLogFileSize() {
        lock (_lock) {
            return _logStream.Size() + _logTextStream.Size();
        }
    }
    public long GetStatisticsFileSize() {
        lock (_lock) {
            return _io.GetFileSizeOrZeroIfUnknown(_statFileKey);
        }
    }
    static byte[] getStatBytes(IStatistics s) {
        var io = new IOProviderMemory();
        using (var stream = io.OpenAppend("stat")) s.SaveState(stream);
        using var read = io.OpenRead("stat", 0);
        return read.Read((int)read.Length);
    }
    static void loadStatStateFromBytes(IStatistics s, byte[] bytes) {
        var io = new IOProviderMemory();
        using (var stream = io.OpenAppend("stat")) stream.Append(bytes);
        using var read = io.OpenRead("stat", 0);
        s.LoadState(read);
    }
    void loadStatisticsState() {
        lock (_lock) {
            if (!_setting.EnableStatistics) return;
            if (_io.DoesNotExistOrIsEmpty(_statFileKey)) return;

            if (canConfirmFileIsNotValid(_io, _statFileKey)) { // confirmed corrupted file
                _io.DeleteIfItExists(_statFileKey); // delete corrupted file
                if (_io.ExistsAndIsNotEmpty(_backupStatFile)) {
                    if (canConfirmFileIsNotValid(_io, _backupStatFile)) {
                        _io.DeleteIfItExists(_backupStatFile); // delete corrupted bkup file
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
    static bool canConfirmFileIsNotValid(IIOProvider io, string fileKey) {
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
        lock (_lock) {
            if (!_setting.EnableStatistics) return;
            var allStats = _statByProp.Values.SelectMany(s => s).ToList();
            var anyDirty = allStats.Any(s => s.IsDirty) || _rowStat.IsDirty;
            if (!anyDirty) return;
            _io.CopyIfItExistsAndOverwrite(_statFileKey, _backupStatFile); // make backup first
            _io.DeleteIfItExists(_statFileKey);
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
    LogRecord getRecord(LogEntry entry) {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(entry.Timestamp.Ticks);
        bw.Write(entry.Values.Count());
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
        return new LogRecord(entry.Timestamp, ms.ToArray());
    }
    LogDataType getDataType(object value) {
        if (value is double) return LogDataType.Double;
        if (value is int) return LogDataType.Integer;
        if (value is string) return LogDataType.String;
        if (value is DateTime) return LogDataType.DateTime;
        if (value is TimeSpan) return LogDataType.TimeSpan;
        if (value is byte[]) return LogDataType.Bytes;
        return LogDataType.String; // defaults to string
    }
    object forceValueType(object value, LogDataType dtype) {
        switch (dtype) {
            case LogDataType.DateTime:
                if (value is DateTime) return value;
                return default(DateTime);
            case LogDataType.TimeSpan:
                if (value is TimeSpan) return value;
                return default(TimeSpan);
            case LogDataType.String:
                return value + string.Empty;
            case LogDataType.Integer:
                if (value is int) return value;
                return default(int);
            case LogDataType.Double:
                if (value is double) return value;
                return default(long);
            case LogDataType.Bytes:
                if (value is byte[]) return value;
                return Array.Empty<byte>();
            default:
                throw new NotImplementedException();
        }
    }
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
            if (_setting.Properties.TryGetValue(key, out var prop)) {
                entry.Values.Add(key, forceValueType(value, prop.DataType));
            } else {
                entry.Values.Add(key, forceToLegalType(value));
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
        lock (_lock) {
            EnforceDateLimit(DateTime.MaxValue);
            DeleteStatistics();
        }
    }
    public void DeleteStatistics() {
        lock (_lock) {
            _io.DeleteIfItExists(_statFileKey);
            _io.DeleteIfItExists(_backupStatFile);
            loadAllStatistics();
        }
    }
    public void EnforceDateLimit(DateTime to) {
        lock (_lock) {
            _logStream.Delete(to);
            _logTextStream.Delete(to);
        }
    }
    public DateTime? GetTimestampOfFirstRecord() {
        lock (_lock) {
            return _logStream.GetTimestampOfFirstRecord();
        }
    }
    public DateTime? GetTimestampOfLastRecord() {
        lock (_lock) {
            return _logStream.GetTimestampOfLastRecord();
        }
    }
    public Dictionary<string, List<StatisticsInfo>> GetAvailableStatisticsByProperty() {
        lock (_lock) {
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
        lock (_lock) {
            return _rowStat.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<int>> AnalyseCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<int>> AnalyseIntegerSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsIntegerSum>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<double>> AnalyseDoubleSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<double>>();
            var cn = stats.OfType<StatisticsDoubleSum>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<double>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        }
    }
    public IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<AvgMinMax<double>>>();
            var cn = stats.OfType<StatisticsAvgMinMax>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<AvgMinMax<double>>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => new AvgMinMax<double>(i.Average, i.Min, i.Max))).ToList();
        }
    }
    public IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<CountSumAvgMinMax<double>>>();
            var cn = stats.OfType<StatisticsCountSumAvgMinMax>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<CountSumAvgMinMax<double>>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => new CountSumAvgMinMax<double>(i.RecordCount, i.Sum, i.Average, i.Min, i.Max))).ToList();
        }
    }
    public IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<Dictionary<string, int>>>();
            var cn = stats.OfType<StatisticsGroupCount>().FirstOrDefault();
            // ensureing dictionary is copied to avoid concurrency issues
            return cn == null ? new() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated)
                .Select(c => c.Map(i => i.Values.ToDictionary(k => k.Key, v => v.Value))).ToList();
        }
    }
    public IEnumerable<Interval<int>> AnalyseUniqueCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsUniqueCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated).Select(c => c.Map(i => i.HashCount())).ToList();
        }
    }
    public IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return Array.Empty<Interval<int>>();
            var cn = stats.OfType<StatisticsEstimatedUniqueCount>().FirstOrDefault();
            return cn == null ? Array.Empty<Interval<int>>() : cn.GetValues(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated).Select(c => c.Map(i => i.EstimateCount())).ToList();
        }
    }
    public Interval<int> AnalyseCombinedRows(IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        lock (_lock) {
            return _rowStat.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<int> AnalyseCombinedCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsCount>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<int> AnalyseCombinedIntegerSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsIntegerSum>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<double> AnalyseCombinedDoubleSums(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsDoubleSum>().FirstOrDefault();
            return cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
        }
    }
    public Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsAvgMinMax>().FirstOrDefault();
            var i = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return i.Map(i => new AvgMinMax<double>(i.Average, i.Min, i.Max));
        }
    }
    public Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval = true, DateTime? nowSimulated = null) {
        lock (_lock) {
            if (!_statByProp.TryGetValue(property, out var stats)) return new(fromUtc, toUtc);
            var cn = stats.OfType<StatisticsCountSumAvgMinMax>().FirstOrDefault();
            var i = cn == null ? new(fromUtc, toUtc) : cn.GetCombinedValue(intervalType, fromUtc, toUtc);
            return i.Map(i => new CountSumAvgMinMax<double>(i.RecordCount, i.Sum, i.Average, i.Min, i.Max));
        }
    }
    public Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        lock (_lock) {
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
        var deltaTimePerChunk = (lastRecord.Value - firstRecord.Value).Ticks / (chunkCount + 1);
        var currentFrom = firstRecord.Value;
        while (currentFrom < lastRecord.Value) {
            var currentTo = new DateTime(currentFrom.Ticks + deltaTimePerChunk, DateTimeKind.Utc);
            if (currentTo > lastRecord.Value) currentTo = lastRecord.Value;
            var entries = Extract(currentFrom, currentTo, 0, int.MaxValue, out var total);
            lock (_lock) {
                foreach (var entry in entries) {
                    _rowStat.RecordIfPossible(entry.Timestamp, true);
                    Console.Write($"{entry.Timestamp} : ");
                    foreach (var value in entry.Values) {
                        if (_statByProp.TryGetValue(value.Key, out var stats)) {
                            foreach (var stat in stats) {
                                Console.Write($"{value.Key}={value.Value}, ");
                                stat.RecordIfPossible(entry.Timestamp, value.Value);
                            }
                        }
                    }
                    Console.WriteLine();
                }
            }
            currentFrom = currentTo;
        }
        SaveStatisticsState();
    }
    public void Dispose() {
        lock (_lock) {
            SaveStatisticsState();
            _logStream.Dispose();
            _logTextStream.Dispose();
        }
    }
    internal void EnforceSizeLimit(int maxTotalSizeOfLogFilesInMb) {
        lock (_lock) {
            _logStream.DeleteLargeLog(maxTotalSizeOfLogFilesInMb);
        }
    }
}
