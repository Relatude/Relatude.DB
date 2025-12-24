using Microsoft.Data.Sqlite;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using System.Data.Common;
namespace Relatude.DB.DataStores.Indexes;

public class PersistedIndexStore : IPersistedIndexStore {
    class idxInfo(string id, PropertyType dataType, string tableName, IIndex index) {
        public string Id { get; } = id;
        public PropertyType DataType { get; } = dataType;
        public string Table { get; } = tableName;
        public IIndex Index { get; } = index;
    }
    string _cnnStr;
    static string _settingsTableName = "settings";
    SqliteConnection _connection;
    SqliteTransaction? _transaction;
    readonly bool _useExternalWordIndex;
    readonly Dictionary<string, idxInfo> _idxs = [];
    public string GetTableName(string id) => _idxs[id].Table;
    readonly Dictionary<string, IPersistentWordIndex> _wordIndexLucenes = [];
    readonly IPersistentWordIndexFactory? _wordIndexFactory;
    readonly string _indexPath;
    public PersistedIndexStore(string indexPath, IPersistentWordIndexFactory? wordIndexFactory) {
        _wordIndexFactory = wordIndexFactory;
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
        // Console.WriteLine("GetSetting: " + key + " = " + (result == null ? "null" : (string)result));
        return result == null ? fallback : (string)result;
    }
    void setSetting(string key, string value) {
        using var cmd = CreateCommand("INSERT OR REPLACE INTO " + _settingsTableName + " (key, value) VALUES (@key, @value)");
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
        // Console.WriteLine("SetSetting: " + key + " = " + value);
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
    public Guid WalFileId => Guid.Parse(getSetting("WALFileId", Guid.Empty.ToString()));
    public void SetWalFileId(Guid walFileId) { setSetting("WALFileId", walFileId.ToString()); }
    public long GetTimestamp(string indexId) {
        var key = "Timestamp_" + indexId;
        return long.Parse(getSetting(key, "0"));
    }
    private void setTimestamp(string indexId, long timestamp) {
        var key = "Timestamp_" + indexId;
        setSetting(key, timestamp.ToString());
    }
    public void UpdateTimestampsDueToHotswap(long timestamp, Guid walFileId) {
        foreach (var i in _idxs.Values) {
            setTimestamp(i.Id, timestamp);
            i.Index.PersistedTimestamp = timestamp;
            setSetting("WALFileId", walFileId.ToString());
        }
    }

    public IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string key, string friendlyName, PropertyType type) where T : notnull {
        var index = new ValueIndexSqlite<T>(sets, this, key, friendlyName);
        _idxs.Add(key, new(key, type, "P" + key.Replace("-", "_"), index));
        index.PersistedTimestamp = GetTimestamp(key);
        return index;
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

    public IWordIndex OpenWordIndex(SetRegister sets, string key, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        IWordIndex index;
        if (_idxs.ContainsKey(key)) return _wordIndexLucenes[key];
        if (_wordIndexFactory != null) {
            var idx = _wordIndexFactory!.Create(sets, this, key, friendlyName, minWordLength, maxWordLength, prefixSearch, infixSearch);
            _wordIndexLucenes.Add(key, idx);
            index = idx;
        } else {
            index = new WordIndexSqlite(sets, this, key, friendlyName, minWordLength, maxWordLength, prefixSearch, infixSearch);
        }
        index.PersistedTimestamp = GetTimestamp(key);
        _idxs.Add(key, new(key, PropertyType.String, "W" + key.Replace("-", "_"), index));
        return index;
    }
    public T CastFromDb<T>(object? value) {
        if (value == null) return default!;
        if (value is T t) return t;
        if (typeof(T) == typeof(DateTime)) return (T)(object)DateTime.Parse((string)value);
        if (typeof(T) == typeof(DateTimeOffset)) return (T)(object)DateTimeOffset.Parse((string)value);
        if (typeof(T) == typeof(double)) return (T)(object)double.Parse((string)value);
        if (value is long && typeof(T) == typeof(int)) return (T)(object)(int)(long)value;
        return (T)value;
    }
    public object? CastToDb(object value) {
        if (value is DateTime dt) return dt.ToString("O");
        if (value is DateTimeOffset dto) return dto.ToString("O");
        return value;
    }

    public void StartTransaction() {
        if (_transaction != null) throw new InvalidOperationException("Transaction already started");
        _transaction = _connection.BeginTransaction();
    }
    public void CommitTransaction(long timestamp) {
        if (_transaction == null) throw new InvalidOperationException("Transaction not started");
        foreach (var i in _idxs.Values) setTimestamp(i.Id, timestamp);
        _transaction.Commit();
        foreach (var i in _wordIndexLucenes.Values) i.Commit();
        foreach (var i in _idxs.Values) i.Index.PersistedTimestamp = timestamp;
        _transaction.Dispose(); // is this needed?...
        _transaction = null;
    }

    public void OptimizeDisk() {
        _connection.Close();
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
        foreach (var i in _wordIndexLucenes) i.Value.OptimizeAndMerge();
    }
    public long GetTotalDiskSpace() {
        if (!Directory.Exists(_indexPath)) return 0;
        return Directory.GetFiles(_indexPath, "*", SearchOption.AllDirectories).Sum(f => {
            try {
                return new FileInfo(f).Length; // sometimes files get deleted between the GetFiles and FileInfo calls
            } catch {
                return 0;
            }
        });
    }

    public void ResetAll() {
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
        foreach (var i in _idxs.Values) {
            i.Index.PersistedTimestamp = 0;
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
            }
        }
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
        _connection.Close();
        _connection.Open();
        foreach (var i in _wordIndexLucenes) i.Value.Close();
        if (_wordIndexFactory != null) _wordIndexFactory.DeleteAllFiles();
        foreach (var i in _wordIndexLucenes) i.Value.Open();
    }
    public void ReOpen() {
        _transaction?.Dispose();
        _transaction = null;
        _connection.Close();
        _connection.Open();
    }

    public void Dispose() {
        foreach (var i in _wordIndexLucenes) i.Value.Dispose();
        try {
            if (_connection.State != System.Data.ConnectionState.Closed) _connection.Close();
        } catch { }
        _transaction?.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
