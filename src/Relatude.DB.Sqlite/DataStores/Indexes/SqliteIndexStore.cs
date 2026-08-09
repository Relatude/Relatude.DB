using System.Globalization;
using Microsoft.Data.Sqlite;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// SQLite-backed index engine. It is dual-role: it always serves the value and array indexes
/// (<see cref="IValueIndexEngine"/> via <see cref="ValueIndexEngineBase"/>), and can additionally
/// serve the word indexes as FTS5 tables (<see cref="ITextIndexEngine"/>) sharing the same
/// connection and transaction — when configured so, the same instance fills both engine slots and
/// <see cref="IndexEngines"/> de-duplicates lifecycle calls by reference, so all index data still
/// commits in one SQLite transaction. All the cross-cutting orchestration (transaction guard,
/// first-commit protocol, WAL-id/timestamp/reset rules) lives in the base classes; this class only
/// implements the SQLite specifics.
/// </summary>
public class SqliteIndexStore : ValueIndexEngineBase, ITextIndexEngine {
    class idxInfo(string id, PropertyType dataType, string tableName) {
        public string Id { get; } = id;
        public PropertyType DataType { get; } = dataType;
        public string Table { get; } = tableName;
    }
    string _cnnStr;
    static string _settingsTableName = "settings";
    SqliteConnection _connection;
    SqliteTransaction? _transaction;
    readonly Dictionary<string, idxInfo> _idxs = [];
    readonly Dictionary<string, IWordIndex> _wordIndexes = []; // opened word indexes, for idempotent re-open
    public string GetTableName(string id) => _idxs[id].Table;
    readonly string _indexPath;
    public SqliteIndexStore(string indexPath) {
        _indexPath = indexPath;
        var sqlLiteFolder = Path.Combine(indexPath, "sqlite");
        if (!Directory.Exists(sqlLiteFolder)) Directory.CreateDirectory(sqlLiteFolder);
        var dbFileName = "index.db";
        var dbPath = Path.Combine(sqlLiteFolder, dbFileName);
        _cnnStr = "Data Source=" + dbPath;// + ";Pooling=False;";
        _connection = new SqliteConnection(_cnnStr);
        _connection.Open();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteNonQuery();
        if (!doesTableExist(_settingsTableName)) createSettingsTable();
    }
    bool doesTableExist(string tableName) {
        var result = executeScalar("SELECT name FROM sqlite_master WHERE type='table' AND name='" + tableName + "'");
        return result != null;
    }

    void createSettingsTable() {
        executeCommand("CREATE TABLE " + _settingsTableName + " (key TEXT PRIMARY KEY, value TEXT)");
    }
    string getSetting(string key, string fallback) {
        using var cmd = CreateCommand("SELECT value FROM " + _settingsTableName + " WHERE key = @key");
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result == null ? fallback : (string)result;
    }
    void setSetting(string key, string value) {
        using var cmd = CreateCommand("INSERT OR REPLACE INTO " + _settingsTableName + " (key, value) VALUES (@key, @value)");
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }
    public SqliteCommand CreateCommand(string? sql = null) {
        var cmd = _connection.CreateCommand();
        if (_transaction != null) cmd.Transaction = _transaction;
        if (sql != null) cmd.CommandText = sql;
        return cmd;
    }
    void executeCommand(string sql) {
        using var cmd = CreateCommand(sql);
        cmd.ExecuteNonQuery();
    }
    object? executeScalar(string sql) {
        using var cmd = CreateCommand(sql);
        return cmd.ExecuteScalar();
    }

    // ---- WAL id / timestamp (backend primitives; see base for the public surface) ----

    protected override Guid ReadWalFileId() => Guid.Parse(getSetting("WALFileId", Guid.Empty.ToString()));
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        if (timestamp.HasValue) setSetting("Timestamp", timestamp.Value.ToString());
        setSetting("WALFileId", walFileId.ToString());
    }
    public override long GetTimestamp() {
        var tsStr = getSetting("Timestamp", "0");
        if (long.TryParse(tsStr, out var ts)) return ts;
        return 0;
    }
    void setTimestamp(long timestamp) => setSetting("Timestamp", timestamp.ToString());

    // ---- index creation ----

    protected override IValueIndex<T> CreateValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var tableName = "P" + id.Replace("-", "_");
        justCreated = !doesTableExist(tableName);
        _idxs.Add(id, new idxInfo(id, type, tableName));
        var index = new SqliteValueIndex<T>(sets, this, id, tableName, friendlyName, justCreated);
        if (justCreated) {
            using var cmd = CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS " + tableName + " (id INTEGER PRIMARY KEY, value " + getSqlType(type) + ")";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS " + tableName + "_value ON " + tableName + " (value)";
            cmd.ExecuteNonQuery();
        }
        return index;
    }
    protected override IStringArrayIndex CreateStringArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var tableName = "A" + id.Replace("-", "_");
        justCreated = !doesTableExist(tableName);
        _idxs.Add(id, new idxInfo(id, type, tableName));
        // one JSON-encoded TEXT value per node; queries run on the index's in-memory mirror,
        // so no value index is needed (see SqliteStringArrayIndex)
        if (justCreated) executeCommand("CREATE TABLE IF NOT EXISTS " + tableName + " (id INTEGER PRIMARY KEY, value TEXT)");
        return new SqliteStringArrayIndex(sets, this, id, tableName, friendlyName, justCreated);
    }
    // guid-array indexes share the "A" table prefix and schema with string arrays (ids are unique
    // per property), so the orphan cleanup and reset paths cover both without changes
    protected override IGuidArrayIndex CreateGuidArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var tableName = "A" + id.Replace("-", "_");
        justCreated = !doesTableExist(tableName);
        _idxs.Add(id, new idxInfo(id, type, tableName));
        // one JSON-encoded TEXT value per node; queries run on the index's in-memory mirror,
        // so no value index is needed (see SqliteGuidArrayIndex)
        if (justCreated) executeCommand("CREATE TABLE IF NOT EXISTS " + tableName + " (id INTEGER PRIMARY KEY, value TEXT)");
        return new SqliteGuidArrayIndex(sets, this, id, tableName, friendlyName, justCreated);
    }
    // int-array indexes (enum arrays) also share the "A" prefix and schema
    protected override IIntArrayIndex CreateIntArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var tableName = "A" + id.Replace("-", "_");
        justCreated = !doesTableExist(tableName);
        _idxs.Add(id, new idxInfo(id, type, tableName));
        // one JSON-encoded TEXT value per node; queries run on the index's in-memory mirror,
        // so no value index is needed (see SqliteIntArrayIndex)
        if (justCreated) executeCommand("CREATE TABLE IF NOT EXISTS " + tableName + " (id INTEGER PRIMARY KEY, value TEXT)");
        return new SqliteIntArrayIndex(sets, this, id, tableName, friendlyName, justCreated);
    }
    /// <summary>
    /// The declared type of the value column for a value-index property type. Sqlite is
    /// dynamically typed, so this only sets the column affinity; what actually matters is that
    /// <see cref="CastToDb"/> / <see cref="CastFromDb{T}"/> produce a representation that both
    /// round-trips exactly and whose sqlite ordering matches <c>Comparer&lt;T&gt;.Default</c> —
    /// range queries, MIN/MAX and the gap cache all compare inside the database.
    /// Only the types backed by <c>ValueProperty&lt;T&gt;</c> reach this method; array-, file-,
    /// embedded- and relation-typed properties use their own tables (see the "A"/"W" prefixes).
    /// </summary>
    string getSqlType(PropertyType type) {
        return type switch {
            PropertyType.Boolean => "INTEGER",       // 0 / 1
            PropertyType.Integer => "INTEGER",
            PropertyType.Long => "INTEGER",
            PropertyType.Float => "REAL",
            PropertyType.Double => "REAL",
            PropertyType.Decimal => "TEXT",          // sortable fixed-point text, see decimalToDb
            PropertyType.String => "TEXT",
            PropertyType.DateTime => "TEXT",         // round-trip ("O") text: fixed width, so binary order is chronological
            PropertyType.DateTimeOffset => "TEXT",   // utc-first sortable text, see dateTimeOffsetToDb
            PropertyType.TimeSpan => "INTEGER",      // ticks
            PropertyType.Guid => "TEXT",             // "D" format
            PropertyType.Reference => "TEXT",        // a reference is the guid of the referenced node
            PropertyType.GeoCoordinate => "INTEGER", // the 62-bit storage code fits a signed sqlite INTEGER and preserves order
            _ => throw new NotSupportedException("The property type '" + type + "' has no sqlite value index representation.")
        };
    }

    // The built-in FTS5 word index, used when this instance is also the text engine. The word
    // index shares the value store's connection and transaction, so it takes part in the base's
    // first-commit protocol and queue lifecycle like any value index.
    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, WordIndexOptions options) {
        if (_wordIndexes.TryGetValue(id, out var existing)) return existing; // idempotent re-open
        var tableName = "W" + id.Replace("-", "_");
        var justCreated = !doesTableExist(tableName);
        _idxs.Add(id, new idxInfo(id, PropertyType.String, tableName)); // registered first: SqliteWordIndex resolves its table via GetTableName(id)
        if (justCreated) {
            executeCommand("CREATE VIRTUAL TABLE " + tableName + " USING fts5(id, value, prefix ='2 3')");
        }
        var index = new SqliteWordIndex(sets, this, id, friendlyName, options.MinWordLength, options.MaxWordLength, options.PrefixSearch, options.InfixSearch, justCreated);
        RegisterManagedIndex(id, index, justCreated);
        // Wrapped here for the same reason as in OpenValueIndex: the engine owns the queue lifecycle.
        var optimized = WrapWordIndexAndRegisterQueue(id, index);
        _wordIndexes[id] = optimized;
        return optimized;
    }

    public T CastFromDb<T>(object? value) {
        if (value == null || value is DBNull) return default!;
        if (value is T t) return t;
        // TEXT-backed types: the encodings are canonical, so CastToDb(CastFromDb(x)) == x, which
        // the range/gap logic in SqliteValueIndex relies on when it feeds a MIN/MAX back as a bound
        if (typeof(T) == typeof(DateTime)) return (T)(object)DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (typeof(T) == typeof(DateTimeOffset)) return (T)(object)dateTimeOffsetFromDb((string)value);
        if (typeof(T) == typeof(decimal)) return (T)(object)decimalFromDb((string)value);
        if (typeof(T) == typeof(Guid)) return (T)(object)Guid.Parse((string)value); // guid and reference properties
        if (typeof(T) == typeof(double)) return (T)(object)double.Parse((string)value, CultureInfo.InvariantCulture);
        if (value is long l) {
            if (typeof(T) == typeof(int)) return (T)(object)(int)l;
            if (typeof(T) == typeof(bool)) return (T)(object)(l != 0);
            if (typeof(T) == typeof(TimeSpan)) return (T)(object)TimeSpan.FromTicks(l);
            if (typeof(T) == typeof(GeoCoordinate)) return (T)(object)GeoCoordinate.FromStorageValue((ulong)l);
        }
        if (value is double d && typeof(T) == typeof(float)) return (T)(object)(float)d;
        return (T)value;
    }
    public object? CastToDb(object value) {
        if (value is DateTime dt) return dt.ToString("O"); // fixed-width and chronological within a DateTimeKind, matching Comparer<DateTime>
        if (value is DateTimeOffset dto) return dateTimeOffsetToDb(dto);
        if (value is decimal dec) return decimalToDb(dec);
        if (value is Guid guid) return guid.ToString("D"); // guid and reference properties
        if (value is TimeSpan ts) return ts.Ticks;
        if (value is GeoCoordinate geo) return (long)geo.StorageValue; // 62-bit code: always non-negative as signed
        return value;
    }

    // ---- order-preserving text encodings -----------------------------------------------------
    // Both encodings below are fixed width and canonical, so sqlite's default BINARY collation
    // (a memcmp of the utf-8 bytes) orders them exactly as Comparer<T>.Default orders the values.

    // A DateTimeOffset is both compared and equated by its instant alone (12:00+02:00 == 10:00Z),
    // so only the utc timestamp is stored: keeping the offset would give one logical value two
    // different keys, splitting its rows across two facet buckets and breaking equality lookups.
    // A value therefore reads back at offset zero - equal to, and ordered with, what was written.
    const string _utcFormat = "yyyy-MM-ddTHH:mm:ss.fffffff"; // 27 chars, always
    static string dateTimeOffsetToDb(DateTimeOffset v) => v.UtcDateTime.ToString(_utcFormat, CultureInfo.InvariantCulture);
    static DateTimeOffset dateTimeOffsetFromDb(string s) {
        var utc = DateTime.ParseExact(s, _utcFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    // Sqlite has no exact decimal type (REAL would lose equality), so a decimal is stored as a
    // sign digit followed by the full fixed-point digit string: '1' for >= 0 and '0' for negative,
    // so that negatives sort first, and for negatives the nine's complement of every digit, which
    // reverses their order within that half. The padding means the scale is not preserved:
    // 1.50m reads back as 1.5m. Values compare equal either way, and the encoding stays canonical.
    const int _decIntDigits = 29;  // decimal.MaxValue has 29 integer digits
    const int _decFracDigits = 28; // and at most 28 decimals
    static string decimalToDb(decimal v) {
        var negative = v < 0;
        var abs = negative ? -v : v; // decimal is sign-magnitude: negating MinValue does not overflow
        var s = abs.ToString("F" + _decFracDigits, CultureInfo.InvariantCulture);
        var digits = s.Remove(s.IndexOf('.'), 1).PadLeft(_decIntDigits + _decFracDigits, '0');
        if (!negative) return "1" + digits;
        var complement = new char[digits.Length + 1];
        complement[0] = '0';
        for (var i = 0; i < digits.Length; i++) complement[i + 1] = (char)('9' - (digits[i] - '0'));
        return new string(complement);
    }
    static decimal decimalFromDb(string s) {
        var negative = s[0] == '0';
        var digits = new char[_decIntDigits + _decFracDigits];
        for (var i = 0; i < digits.Length; i++) {
            var c = s[i + 1];
            digits[i] = negative ? (char)('9' - (c - '0')) : c;
        }
        // trim the padding before parsing: the padded form has more digits than a decimal can hold
        var intPart = new string(digits, 0, _decIntDigits).TrimStart('0');
        var fracPart = new string(digits, _decIntDigits, _decFracDigits).TrimEnd('0');
        if (intPart.Length == 0) intPart = "0";
        var text = fracPart.Length == 0 ? intPart : intPart + "." + fracPart;
        var value = decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    // ---- transactions (backend primitives; the base owns the guard + first-commit protocol) ----

    protected override void BeginTransactionCore() {
        _transaction = _connection.BeginTransaction();
    }
    protected override void CommitTransactionCore(long timestamp) {
        setTimestamp(timestamp); // persisted in the same transaction as the index data
        _transaction!.Commit();
        _transaction.Dispose();
        _transaction = null;
    }
    protected override void RollbackTransactionCore() {
        try { _transaction!.Rollback(); }
        finally { _transaction?.Dispose(); _transaction = null; }
    }

    // ---- maintenance / lifecycle (backend primitives; word indexes handled by the base) ----

    protected override void DeleteUnopenedIndexesCore() {
        var openTables = _idxs.Values.Select(i => i.Table).ToHashSet();
        List<string> allTables = new();
        using (var cmd = CreateCommand("SELECT name FROM sqlite_master WHERE type='table'")) {
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) allTables.Add(reader.GetString(0));
        }
        // value tables are "P...", word tables "W...", array tables (string and guid) "A...". Skip open tables and anything derived from
        // them ("<openTable>_..." covers the fts5 shadow tables of an open word index). Shorter
        // names first so an unopened fts5 virtual table drops before its shadow tables; the
        // shadows then vanish with it, and a direct drop of a still-present shadow table (which
        // sqlite refuses) is just skipped by the catch.
        var doomed = allTables
            .Where(t => t.StartsWith("P") || t.StartsWith("W") || t.StartsWith("A"))
            .Where(t => t != _settingsTableName && !openTables.Contains(t) && !openTables.Any(o => t.StartsWith(o + "_")))
            .OrderBy(t => t.Length)
            .ToList();
        foreach (var table in doomed) {
            try { executeCommand("DROP TABLE IF EXISTS " + table); } catch { }
        }
        // remove the deleted indexes' persisted timestamps, so a re-added index starts at 0
        List<string> timestampKeys = new();
        using (var cmd = CreateCommand("SELECT key FROM " + _settingsTableName + " WHERE key LIKE 'Timestamp_%'")) {
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) timestampKeys.Add(reader.GetString(0));
        }
        var openIds = _idxs.Keys.ToHashSet();
        foreach (var key in timestampKeys) {
            var id = key.Substring("Timestamp_".Length);
            if (openIds.Contains(id)) continue;
            using var cmd = CreateCommand("DELETE FROM " + _settingsTableName + " WHERE key = @key");
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }
    }

    protected override void OptimizeDiskCore() {
        _connection.Close();
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
    }
    public override long GetTotalDiskSpace() {
        if (!Directory.Exists(_indexPath)) return 0;
        return Directory.GetFiles(_indexPath, "*", SearchOption.AllDirectories).Sum(f => {
            try {
                return new FileInfo(f).Length; // sometimes files get deleted between the GetFiles and FileInfo calls
            } catch {
                return 0;
            }
        });
    }

    protected override void ResetAllDataCore() {
        _connection.Close();
        _connection.Open(); // reopen connection to clear all tables
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        List<string> tables = new();
        while (reader.Read()) tables.Add(reader.GetString(0));
        reader.Close();
        foreach (var table in tables) {
            try {
                cmd.CommandText = "DROP TABLE IF EXISTS " + table;
                cmd.ExecuteNonQuery();
            } catch { }
        }
        // The settings table is dropped with the rest above; recreate it empty so the base can
        // re-persist the WAL id and a timestamp of 0 immediately after this returns.
        createSettingsTable();
        foreach (var i in _idxs.Values) {
            if (i.Table.StartsWith("P")) {
                cmd.CommandText = "CREATE TABLE " + i.Table + " (id INTEGER PRIMARY KEY, value " + getSqlType(i.DataType) + ")";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE INDEX " + i.Table + "_value ON " + i.Table + " (value)";
                cmd.ExecuteNonQuery();
            } else if (i.Table.StartsWith("W")) { // FTS5 word index: only in _idxs when this instance is the text engine
                cmd.CommandText = "CREATE VIRTUAL TABLE " + i.Table + " USING fts5(id, value, prefix ='2 3')";
                cmd.ExecuteNonQuery();
            } else if (i.Table.StartsWith("A")) { // array index (string or guid)
                cmd.CommandText = "CREATE TABLE " + i.Table + " (id INTEGER PRIMARY KEY, value TEXT)";
                cmd.ExecuteNonQuery();
            }
        }
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
        _connection.Close();
        _connection.Open();
    }

    protected override void DisposeCore() {
        try {
            if (_connection.State != System.Data.ConnectionState.Closed) _connection.Close();
        } catch { }
        _transaction?.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
