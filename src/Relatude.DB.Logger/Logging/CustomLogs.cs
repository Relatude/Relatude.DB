using Relatude.DB.IO;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Relatude.DB.Logging;
/// <summary>
/// The custom logs of one log folder; see <see cref="ICustomLogs"/>.
///
/// The settings files are the definitions. What is running is a <see cref="Logging.LogStore"/>
/// built from copies of them, so a definition handed out can be changed freely without changing a
/// log that runs on it, and a change reaches the log only through <see cref="Update"/>, which saves
/// the file before the log is rebuilt on it.
///
/// Recording goes straight to the store, lock-free; defining, changing and deleting hold one lock,
/// so two of them never interleave. Thread-safe.
/// </summary>
public sealed class CustomLogs : ICustomLogs, IDisposable {
    // Letters, digits, '-' and '_': a key is part of every file name the log has, and a dot there
    // would separate parts of the name that the log's search patterns take apart again.
    static readonly Regex _keyPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);
    // how much of a log is read in one go while its entries are moved to another file layout: a few
    // files at a time rather than one, since every read lists the folder first
    const long moveChunkBytes = 32L * 1024 * 1024;
    const int moveChunkFiles = 500;

    readonly IIOProvider _io;
    readonly HashSet<string> _reserved;
    readonly object _lock = new();
    volatile LogStore _store;
    // the definitions as they were saved, by key. Never handed out: callers get copies
    Dictionary<string, LogSettings> _definitions = new(StringComparer.OrdinalIgnoreCase);
    // where a definition was read from, when that is not the file it would be saved to (a file
    // renamed by hand): saving writes the right file and removes this one, so the log is not
    // defined twice at the next start
    Dictionary<string, string[]> _sourceFiles = new(StringComparer.OrdinalIgnoreCase);
    List<CustomLogLoadError> _loadErrors = [];
    volatile bool _anyEnabled;
    bool _disposed;

    /// <param name="io">The provider the log folder is in.</param>
    /// <param name="reservedKeys">Keys a custom log may not take: those of the logs defined in code,
    /// which keep their files in the same folder.</param>
    public CustomLogs(IIOProvider io, IEnumerable<string>? reservedKeys = null) {
        _io = io;
        _reserved = new(reservedKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _store = new LogStore(io, load());
    }

    public ILogStore LogStore => _store;
    public IReadOnlyCollection<string> ReservedKeys => _reserved;
    public bool AnyEnabled => _anyEnabled;

    public IReadOnlyList<LogSettings> GetDefinitions() {
        lock (_lock) {
            return [.. _definitions.Values
                .OrderBy(s => string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .Select(s => s.Clone())];
        }
    }
    public LogSettings? GetDefinition(string logKey) {
        lock (_lock) return _definitions.TryGetValue(logKey, out var s) ? s.Clone() : null;
    }
    public bool HasLog(string logKey) => _store.HasLog(logKey);
    public bool IsEnabled(string logKey) => _store.IsEnabled(logKey);
    public IReadOnlyList<CustomLogLoadError> LoadErrors {
        get { lock (_lock) return [.. _loadErrors]; }
    }

    public string? CheckNewKey(string logKey) {
        if (string.IsNullOrWhiteSpace(logKey)) return "A log needs a key.";
        if (!_keyPattern.IsMatch(logKey)) return "A key is letters, digits, '-' and '_', starts with a letter or a digit, and is at most 64 characters long.";
        if (_reserved.Contains(logKey)) return $"'{logKey}' is the key of one of the database's activity logs.";
        lock (_lock) {
            if (_definitions.ContainsKey(logKey)) return $"There is already a log with the key '{logKey}'.";
            if (_io.Exists(FileKeyUtility.Logger_GetSettings(logKey))) return $"The log folder already has a settings file for '{logKey}'.";
        }
        return null;
    }

    // ---- reading the definitions ----

    List<LogSettings> load() {
        var definitions = new Dictionary<string, LogSettings>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<CustomLogLoadError>();
        foreach (var fileKey in FileKeyUtility.Logger_GetAllSettingsFileKeys(_io)) {
            LogSettings settings;
            try {
                if (!_io.ExistsAndIsNotEmpty(fileKey)) throw new Exception("The file is empty.");
                settings = LogSettings.Load(_io, fileKey);
            } catch (Exception error) {
                errors.Add(new(fileKey.AsKeyString(), error.Message));
                continue;
            }
            // the system logs are defined in code; settings saved for them here are theirs, not a
            // custom log with the same name
            if (_reserved.Contains(settings.Key)) continue;
            if (!definitions.TryAdd(settings.Key, settings)) {
                errors.Add(new(fileKey.AsKeyString(), $"Another settings file already defines the log '{settings.Key}'."));
                continue;
            }
            if (!fileKey.IsSameKey(FileKeyUtility.Logger_GetSettings(settings.Key))) sources[settings.Key] = fileKey;
        }
        _definitions = definitions;
        _sourceFiles = sources;
        _loadErrors = errors;
        updateAnyEnabled();
        return [.. definitions.Values.Select(s => s.Clone())];
    }
    void updateAnyEnabled() => _anyEnabled = _definitions.Values.Any(s => s.IsEnabled());

    // Settings as the log will run on them: validated, normalized the way a file would read back,
    // and sharing nothing with what the caller holds.
    static LogSettings prepare(LogSettings settings) => LogSettings.FromJson(settings.ToJson());

    void save(LogSettings settings) {
        settings.Save(_io);
        if (_sourceFiles.Remove(settings.Key, out var source)) _io.DeleteFileIfItExists(source);
    }
    void ensureOpen() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    LogSettings current(string logKey) {
        return _definitions.TryGetValue(logKey, out var s) ? s : throw new ArgumentException($"There is no log with the key '{logKey}'.");
    }

    // ---- defining ----

    public void Create(LogSettings settings) {
        var next = prepare(settings);
        lock (_lock) {
            ensureOpen();
            var problem = CheckNewKey(next.Key);
            if (problem != null) throw new ArgumentException(problem);
            save(next);
            _store.AddLog(next.Clone());
            _definitions[next.Key] = next;
            updateAnyEnabled();
        }
    }

    public CustomLogChanges PlanUpdate(LogSettings settings) {
        var next = prepare(settings);
        lock (_lock) {
            ensureOpen();
            return plan(current(next.Key), next);
        }
    }

    public CustomLogChanges Update(LogSettings settings, bool rebuildStatistics = false) {
        var next = prepare(settings);
        lock (_lock) {
            ensureOpen();
            var old = current(next.Key);
            var changes = plan(old, next);
            if (!changes.Changed) return changes;
            if (changes.MovesEntries) changes.EntriesMoved = moveEntries(old, next);
            else _store.ReplaceLog(next.Clone());
            // written once the log runs on it: a failure above leaves the file saying what the log
            // on disk still is
            save(next);
            _definitions[next.Key] = next;
            updateAnyEnabled();
            var hasEntries = _store.GetTimestampOfFirstRecord(next.Key) != null;
            if (changes.DiscardsStatistics) {
                // what the statistics hold was aggregated as another type, and would read as noise
                if (next.EnableStatistics && hasEntries) {
                    _store.RebuildStatistics(next.Key);
                    changes.StatisticsRebuilt = true;
                } else {
                    _store.DeleteStatistics(next.Key);
                }
            } else if (rebuildStatistics && next.EnableStatistics && hasEntries) {
                _store.RebuildStatistics(next.Key);
                changes.StatisticsRebuilt = true;
            }
            return changes;
        }
    }

    public void SetEnabled(string logKey, bool? log, bool? statistics) {
        lock (_lock) {
            ensureOpen();
            var next = current(logKey).Clone();
            if (log is bool l) next.EnableLog = l;
            if (statistics is bool s) next.EnableStatistics = s;
            var old = _definitions[logKey];
            if (next.EnableLog == old.EnableLog && next.EnableStatistics == old.EnableStatistics) return;
            save(next);
            // rebuilt rather than switched in place: statistics turned on have to be read back from
            // their file, and statistics turned off have to be saved to it first
            _store.ReplaceLog(next.Clone());
            _definitions[logKey] = next;
            updateAnyEnabled();
        }
    }

    public void Delete(string logKey, bool deleteRecorded) {
        lock (_lock) {
            ensureOpen();
            var old = current(logKey);
            if (deleteRecorded) _store.DeleteLogAndStatistics(old.Key);
            _store.RemoveLog(old.Key);
            LogSettings.DeleteSaved(_io, old.Key);
            if (_sourceFiles.Remove(old.Key, out var source)) _io.DeleteFileIfItExists(source);
            _definitions.Remove(old.Key);
            updateAnyEnabled();
        }
    }

    /// <summary>
    /// What a new definition does to what the log has recorded. Every consequence that is not simply
    /// "the next entry is recorded the new way" is written out, because each one of them is either
    /// work (entries moved, statistics rebuilt) or something that is lost (statistics dropped,
    /// entries past a tighter limit) - which is what a confirmation has to be able to say.
    /// </summary>
    CustomLogChanges plan(LogSettings old, LogSettings next) {
        var changes = new CustomLogChanges { Changed = old.ToJson() != next.ToJson() };
        if (!changes.Changed) return changes;
        var key = next.Key;
        var first = _store.GetTimestampOfFirstRecord(key);
        var hasEntries = first != null;
        var logBytes = _store.GetLogFileSize(key);
        var notes = changes.Notes;

        if (old.FileInterval != next.FileInterval && hasEntries) {
            changes.MovesEntries = true;
            notes.Add($"The log keeps one file per {intervalName(next.FileInterval)} instead of one per {intervalName(old.FileInterval)}. "
                + $"The entries recorded so far ({bytes(logBytes)}) are moved into files of the new size, which reads and writes every one of them.");
        }

        var statisticsRestart = false;
        var statisticsAdded = false;
        // The level of statistical detail is how far back the statistics reach. The admin UI sets it
        // for every statistic at once (the entry count's is the log's), so a change of level is said
        // once, and only a statistic that went to some other level is named on its own.
        var levelChanged = old.ResolutionRowStats != next.ResolutionRowStats;
        if (levelChanged) {
            notes.Add($"The level of statistical detail changes from {old.ResolutionRowStats} to {next.ResolutionRowStats}: the statistics start over, and from now on reach "
                + (next.ResolutionRowStats > old.ResolutionRowStats ? "further back." : "less far back."));
            statisticsRestart = true;
        }
        foreach (var (column, before) in old.Properties) {
            var label = columnLabel(column, before);
            if (!next.Properties.TryGetValue(column, out var after)) {
                notes.Add(before.Statistics.Count > 0
                    ? $"{label} is no longer a column, and its statistics are dropped. The values already recorded for it stay in their entries."
                    : $"{label} is no longer a column. The values already recorded for it stay in their entries.");
                continue;
            }
            if (after.DataType != before.DataType) {
                notes.Add($"{label} changes from {typeName(before.DataType)} to {typeName(after.DataType)}. Values recorded so far are read as {typeName(after.DataType)} where they can be, and shown as they were stored where they cannot.");
                if (before.Statistics.Count > 0 && after.Statistics.Count > 0) changes.DiscardsStatistics = true;
            }
            var had = statisticsOf(before);
            var has = statisticsOf(after);
            foreach (var (type, resolution) in had) {
                if (!has.TryGetValue(type, out var now)) {
                    notes.Add($"{label} no longer keeps {statisticName(type)} statistics; what they hold is dropped.");
                } else if (now != resolution) {
                    statisticsRestart = true;
                    if (levelChanged && now == next.ResolutionRowStats) continue; // said with the level
                    notes.Add($"{label} keeps its {statisticName(type)} statistics at level {now} instead of {resolution}. They start over at the new level.");
                }
            }
            if (has.Keys.Any(t => !had.ContainsKey(t))) statisticsAdded = true;
        }
        foreach (var (column, added) in next.Properties) {
            if (old.Properties.ContainsKey(column)) continue;
            notes.Add($"{columnLabel(column, added)} is a new column: the entries recorded so far have no value for it.");
            if (added.Statistics.Count > 0) statisticsAdded = true;
        }
        if (old.FirstDayOfWeek != next.FirstDayOfWeek) {
            notes.Add($"Weeks start on {next.FirstDayOfWeek} instead of {old.FirstDayOfWeek}, which every statistic is kept by: they all start over.");
            statisticsRestart = true;
        }
        if (changes.DiscardsStatistics) {
            notes.Add(next.EnableStatistics
                ? "A column with statistics changed type, and what they hold was aggregated as the old one: the statistics are rebuilt from the entries."
                : "A column with statistics changed type, and what they hold was aggregated as the old one: the statistics are deleted. Turn them on and rebuild them from the entries to get them back.");
        } else if ((statisticsAdded || statisticsRestart) && hasEntries && next.EnableStatistics) {
            changes.CanRebuildStatistics = true;
            notes.Add("New statistics start empty and cover only what is recorded from now on, unless they are rebuilt from the entries already recorded.");
        }

        if (old.EnableLog && !next.EnableLog) notes.Add("Entries are no longer recorded. Those recorded so far are kept until the log's limits remove them.");
        if (old.EnableStatistics && !next.EnableStatistics) notes.Add("Statistics are no longer kept. What they hold stays on disk, and is read again when they are turned back on.");
        if (!old.EnableStatistics && next.EnableStatistics && hasEntries) notes.Add("Statistics are kept again, from what they held when they were turned off. What was recorded in between is only counted if they are rebuilt.");
        if (next.MaxAgeOfLogFilesInDays > 0 && first is DateTime oldest && oldest < DateTime.UtcNow.AddDays(-next.MaxAgeOfLogFilesInDays)
            && (old.MaxAgeOfLogFilesInDays <= 0 || next.MaxAgeOfLogFilesInDays < old.MaxAgeOfLogFilesInDays)) {
            notes.Add($"Entries older than {next.MaxAgeOfLogFilesInDays} days are deleted at the next clean-up, within a minute or so. The oldest is from {oldest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
        }
        var limit = (long)next.MaxTotalSizeOfLogFilesInMb * 1024 * 1024;
        if (next.MaxTotalSizeOfLogFilesInMb > 0 && logBytes > limit
            && (old.MaxTotalSizeOfLogFilesInMb <= 0 || next.MaxTotalSizeOfLogFilesInMb < old.MaxTotalSizeOfLogFilesInMb)) {
            notes.Add($"The entries take {bytes(logBytes)}, more than the new limit of {next.MaxTotalSizeOfLogFilesInMb} MB: the oldest files are deleted at the next clean-up.");
        }
        return changes;
    }
    static Dictionary<StatisticsType, int> statisticsOf(LogProperty property) {
        var result = new Dictionary<StatisticsType, int>();
        foreach (var s in property.Statistics) result.TryAdd(s.StatisticsType, s.Resolution);
        return result;
    }
    static string columnLabel(string key, LogProperty property) => string.IsNullOrWhiteSpace(property.Name) || property.Name == key ? $"'{key}'" : $"'{property.Name}' ({key})";
    static string intervalName(FileInterval interval) => interval.ToString().ToLowerInvariant();
    static string typeName(LogDataType type) => type switch {
        LogDataType.DateTime => "a date and time",
        LogDataType.TimeSpan => "a duration",
        LogDataType.String => "text",
        LogDataType.Integer => "a whole number",
        LogDataType.Double => "a decimal number",
        LogDataType.Bytes => "bytes",
        _ => type.ToString(),
    };
    static string statisticName(StatisticsType type) => type switch {
        StatisticsType.Count => "count",
        StatisticsType.Sum => "total",
        StatisticsType.AvgMinMax => "average, min and max",
        StatisticsType.CountSumAvgMinMax => "count, total, average, min and max",
        StatisticsType.UniqueCountWithValues => "count per value",
        StatisticsType.UniqueCountHashedValues => "unique count",
        StatisticsType.UniqueCountEstimate => "estimated unique count",
        _ => type.ToString(),
    };
    static string bytes(long value) {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) {
            size /= 1024;
            unit++;
        }
        return (unit == 0 || size >= 100 ? Math.Round(size).ToString(CultureInfo.InvariantCulture) : size.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    /// <summary>
    /// Moves a log's entries into files of another interval: the file name carries the interval, so
    /// a log reading files per hour does not see the files it wrote per day.
    ///
    /// The old files are read through a log of their own with statistics off, so it never touches
    /// the statistics file the new log opens - the statistics are kept by time, not by file, and stay
    /// valid as they are. The entries are written with statistics off for the same reason: they are
    /// already counted. The old files are deleted only once every entry is in the new ones.
    /// </summary>
    int moveEntries(LogSettings old, LogSettings next) {
        var key = next.Key;
        _store.RemoveLog(key); // statistics saved, files closed
        var readerSettings = old.Clone();
        readerSettings.EnableStatistics = false;
        var reader = new LogStore(_io, [readerSettings]);
        var moved = 0;
        try {
            _store.AddLog(next.Clone());
            var files = FileKeyUtility.Logger_FileDatesBin(_io, key, old.FileInterval);
            var i = 0;
            while (i < files.Count) {
                // a chunk of whole files, read in one go
                var from = files[i];
                long size = 0;
                var j = i;
                while (j < files.Count && (j == i || (size < moveChunkBytes && j - i < moveChunkFiles))) {
                    size += _io.GetFileSizeOrZeroIfUnknown(FileKeyUtility.Logger_FileNameBin(key, old.FileInterval, files[j]));
                    j++;
                }
                var until = files[j - 1].AddInterval(old.FileInterval);
                foreach (var entry in reader.ExtractLog(key, from, until, 0, int.MaxValue, false, out _)) {
                    _store.Record(key, entry, false, forceLogging: true, forceStatistics: false);
                    moved++;
                }
                i = j;
            }
            _store.FlushToDiskNow(key);
            reader.DeleteLog(key);
        } finally {
            reader.Dispose();
        }
        return moved;
    }

    public IReadOnlyList<CustomLogFile> GetFiles(string logKey) {
        LogSettings settings;
        lock (_lock) settings = current(logKey);
        // reading the size writes what is buffered and lets go of the file being appended to, so the
        // sizes are current and a download of today's file is not refused as in use
        _store.GetLogFileSize(settings.Key);
        // the key has no dots (see _keyPattern), so this prefix is this log's alone
        var prefix = "log." + settings.Key + ".";
        var entriesPrefix = prefix + settings.FileInterval.ToString().ToLowerInvariant() + ".";
        var files = new List<CustomLogFile>();
        foreach (var key in _io.Search([FileKeyUtility.LogFolderName, prefix + "*"])) {
            var name = key.FileName();
            var kind = name.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase) ? "settings"
                : name.EndsWith(".statistics.bin", StringComparison.OrdinalIgnoreCase) ? "statistics"
                : name.EndsWith(".statistics.bin.bkup", StringComparison.OrdinalIgnoreCase) ? "statistics-backup"
                : !name.StartsWith(entriesPrefix, StringComparison.OrdinalIgnoreCase) ? "left-over"
                : name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? "entries"
                : name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text"
                : "left-over";
            files.Add(new(key.AsKeyString(), kind, _io.GetFileSizeOrZeroIfUnknown(key)));
        }
        return files;
    }

    // ---- settings files that did not read ----

    string[] broken(string fileKey) {
        lock (_lock) {
            if (!_loadErrors.Any(e => e.FileKey == fileKey)) throw new ArgumentException("That is not a settings file that failed to read.");
        }
        return fileKey.SplitKey();
    }
    public string ReadBrokenDefinition(string fileKey) {
        var key = broken(fileKey);
        return _io.ExistsAndIsNotEmpty(key) ? _io.ReadAllTextUTF8(key) : string.Empty;
    }
    public void RepairBrokenDefinition(string fileKey, string json) {
        var key = broken(fileKey);
        var settings = LogSettings.FromJson(json); // throws with what is still wrong, before anything changes
        lock (_lock) {
            ensureOpen();
            if (_reserved.Contains(settings.Key)) throw new ArgumentException($"'{settings.Key}' is the key of one of the database's activity logs.");
            if (_definitions.ContainsKey(settings.Key)) throw new ArgumentException($"There is already a log with the key '{settings.Key}'.");
            _io.DeleteFileIfItExists(key);
            settings.Save(_io);
            _store.AddLog(settings.Clone());
            _definitions[settings.Key] = settings;
            _loadErrors.RemoveAll(e => e.FileKey == fileKey);
            updateAnyEnabled();
        }
    }
    public void DeleteBrokenDefinition(string fileKey) {
        var key = broken(fileKey);
        lock (_lock) {
            _io.DeleteFileIfItExists(key);
            _loadErrors.RemoveAll(e => e.FileKey == fileKey);
        }
    }

    public void Reload() {
        lock (_lock) {
            ensureOpen();
            // closed first, so its statistics are on disk for the new store to read back
            _store.Dispose();
            _store = new LogStore(_io, load());
        }
    }

    // ---- recording ----

    public bool Record(string logKey, LogEntry entry, bool flushToDisk = false) => _store.Record(logKey, entry, flushToDisk);
    public bool Record(string logKey, params (string Property, object? Value)[] values) {
        var entry = new LogEntry();
        foreach (var (property, value) in values) {
            if (value != null) entry.Values[property] = value;
        }
        return _store.Record(logKey, entry);
    }
    public bool Record(string logKey, IEnumerable<KeyValuePair<string, object?>> values, DateTime? timestampUtc = null) {
        var entry = new LogEntry { Timestamp = timestampUtc ?? DateTime.UtcNow };
        foreach (var (property, value) in values) {
            if (value != null) entry.Values[property] = value;
        }
        return _store.Record(logKey, entry);
    }
    public bool RecordObject(string logKey, object values, DateTime? timestampUtc = null) {
        ArgumentNullException.ThrowIfNull(values);
        // a log that is off, or not there, costs no reflection
        if (!_store.IsEnabled(logKey)) return _store.HasLog(logKey);
        if (values is IEnumerable<KeyValuePair<string, object?>> pairs) return Record(logKey, pairs, timestampUtc);
        var entry = new LogEntry { Timestamp = timestampUtc ?? DateTime.UtcNow };
        foreach (var (name, get) in readersOf(values.GetType())) {
            if (get(values) is object value) entry.Values[name] = value;
        }
        return _store.Record(logKey, entry);
    }
    static readonly ConcurrentDictionary<Type, (string Name, Func<object, object?> Get)[]> _readers = new();
    static (string Name, Func<object, object?> Get)[] readersOf(Type type) => _readers.GetOrAdd(type, t => [..
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => (p.Name, (Func<object, object?>)p.GetValue))]);

    // ---- keeping ----

    public void FlushToDiskNow() => _store.FlushToDiskNow();
    public void SaveStatsAndDeleteExpiredData() => _store.SaveStatsAndDeleteExpiredData();
    public long GetTotalFileSize() {
        long total = 0;
        foreach (var settings in _store.GetSettings()) total += _store.GetFileSize(settings.Key);
        return total;
    }
    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _store.Dispose();
        }
    }
}
