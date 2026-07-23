using Microsoft.Data.Sqlite;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// SQLite-backed <see cref="IPersistedIndexStore"/>. All the cross-cutting orchestration
/// (transaction guard, first-commit protocol, word-index lifecycle, WAL-id/timestamp/reset rules)
/// lives in <see cref="PersistedIndexStoreBase"/>; this class only implements the SQLite specifics.
/// </summary>
public class SqliteIndexStore : PersistedIndexStoreBase {
    class idxInfo(string id, PropertyType dataType, string tableName) {
        public string Id { get; } = id;
        public PropertyType DataType { get; } = dataType;
        public string Table { get; } = tableName;
    }
    string _cnnStr;
    static string _settingsTableName = "settings";
    SqliteConnection _connection;
    SqliteTransaction? _transaction;
    readonly bool _useExternalWordIndex;
    readonly Dictionary<string, idxInfo> _idxs = [];
    public string GetTableName(string id) => _idxs[id].Table;
    readonly string _indexPath;
    public SqliteIndexStore(string indexPath, IPersistentWordIndexFactory? wordIndexFactory) : base(wordIndexFactory) {
        _useExternalWordIndex = wordIndexFactory != null;
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
    string getSqlType(PropertyType type) {
        return type switch {
            PropertyType.Boolean => "INTEGER",
            PropertyType.Integer => "INTEGER",
            PropertyType.Float => "REAL",
            PropertyType.Double => "REAL",
            PropertyType.String => "TEXT",
            PropertyType.DateTime => "INTEGER",
            _ => throw new NotImplementedException()
        };
    }

    // Only reached when no word-index factory was supplied (the built-in FTS5 word index). Both
    // shipped backends run with a factory, so this path is effectively unused; the table entry is
    // registered before constructing the index because WordIndexSqlite resolves its table via
    // GetTableName(id).
    protected override IWordIndex CreateBuiltInWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch, out bool justCreated) {
        var tableName = "W" + id.Replace("-", "_");
        justCreated = !doesTableExist(tableName);
        _idxs.Add(id, new idxInfo(id, PropertyType.String, tableName)); // registered first: WordIndexSqlite resolves its table via GetTableName(id)
        if (justCreated) {
            executeCommand("CREATE VIRTUAL TABLE " + tableName + " USING fts5(id, value, prefix ='2 3')");
        }
        return new SqliteWordIndex(sets, this, id, friendlyName, minWordLength, maxWordLength, prefixSearch, infixSearch, justCreated);
    }

    public T CastFromDb<T>(object? value) {
        if (value == null) return default!;
        if (value is T t) return t;
        if (typeof(T) == typeof(DateTime)) return (T)(object)DateTime.Parse((string)value);
        if (typeof(T) == typeof(DateTimeOffset)) return (T)(object)DateTimeOffset.Parse((string)value);
        if (typeof(T) == typeof(double)) return (T)(object)double.Parse((string)value);
        if (value is long && typeof(T) == typeof(int)) return (T)(object)(int)(long)value;
        if (value is long && typeof(T) == typeof(bool)) return (T)(object)((long)value != 0);
        if (value is double && typeof(T) == typeof(float)) return (T)(object)(float)(double)value;
        return (T)value;
    }
    public object? CastToDb(object value) {
        if (value is DateTime dt) return dt.ToString("O");
        if (value is DateTimeOffset dto) return dto.ToString("O");
        return value;
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
        // value tables are "P...", word tables "W...", string-array tables "A...". Skip open tables and anything derived from
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
            } else if (i.Table.StartsWith("W")) { // FTS5 Word Index
                if (!_useExternalWordIndex) {
                    cmd.CommandText = "CREATE VIRTUAL TABLE " + i.Table + " USING fts5(id, value, prefix ='2 3')";
                    cmd.ExecuteNonQuery();
                }
            } else if (i.Table.StartsWith("A")) { // string array index
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
