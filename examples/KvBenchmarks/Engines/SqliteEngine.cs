using Microsoft.Data.Sqlite;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace KvBenchmarks.Engines;

/// <summary>
/// <see cref="IStorageEngine"/> on a single SQLite database (WAL mode, synchronous=FULL so
/// durable commits are real). One table per index: (id INTEGER PRIMARY KEY, v ...) plus a
/// covering (v, id) index for ordered scans. Values map to native SQLite types whose default
/// BINARY collation matches the byte order the native engine uses (test data is ASCII).
/// </summary>
public sealed class SqliteEngine : IStorageEngine, IDisposable
{
    private readonly SqliteConnection _con;
    private readonly string _path;
    private readonly Dictionary<string, object> _openIndexes = new();
    private long _timestamp;
    private bool _inTxn;

    public SqliteEngine(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "bench.sqlite");
        _con = new SqliteConnection($"Data Source={_path}");
        _con.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=FULL");
        Exec("PRAGMA cache_size=-65536"); // 64 MB, same budget as the native engine's page cache
        Exec("PRAGMA temp_store=MEMORY");
        Exec("CREATE TABLE IF NOT EXISTS meta(k INTEGER PRIMARY KEY, ts INTEGER NOT NULL)");
        Exec("CREATE TABLE IF NOT EXISTS catalog(name TEXT PRIMARY KEY, type TEXT NOT NULL)");
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT ts FROM meta WHERE k=0";
        _timestamp = cmd.ExecuteScalar() is long ts ? ts : 0;
    }

    internal SqliteConnection Connection => _con;

    private void Exec(string sql)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public ISortedIntIndex<T> OpenOrCreateIntIndex<T>(string name) where T : notnull
    {
        if (Existing<T>(name) is object open)
        {
            return open as SqliteIndex<T>
                ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type or layout.");
        }
        var index = new SqliteIndex<T>(this, name, hasEngineTimestamp: Register<T>(name));
        _openIndexes[name] = index;
        return index;
    }

    /// <summary>
    /// The unordered layout: the same table, without the covering (v, id) index that
    /// <see cref="OpenOrCreateIntIndex{T}"/> creates. SQLite then maintains one B-tree per write
    /// instead of two, and value-side queries fall back to a table scan.
    /// </summary>
    public IIntIndex<T> OpenOrCreateIntHashIndex<T>(string name) where T : notnull
    {
        if (Existing<T>(name) is object open)
        {
            if (open is SqliteIndex<T>)
                throw new InvalidOperationException($"Index '{name}' is already open as a sorted index.");
            return open as SqliteHashIndex<T>
                ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type.");
        }
        var index = new SqliteHashIndex<T>(this, name, hasEngineTimestamp: Register<T>(name));
        _openIndexes[name] = index;
        return index;
    }

    /// <summary>The index already open under this name, or null when the name is free.</summary>
    private object? Existing<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _openIndexes.GetValueOrDefault(name);
    }

    /// <summary>Records the index in the catalog if new; returns whether it was already there.</summary>
    private bool Register<T>(string name) where T : notnull
    {
        string typeName = typeof(T).FullName!;
        bool existed;
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "SELECT type FROM catalog WHERE name=@n";
            cmd.Parameters.AddWithValue("@n", name);
            object? t = cmd.ExecuteScalar();
            existed = t is not null;
            if (existed && (string)t! != typeName)
                throw new InvalidOperationException($"Index '{name}' exists with a different value type.");
        }
        if (!existed)
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = "INSERT INTO catalog(name, type) VALUES(@n, @t)";
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@t", typeName);
            cmd.ExecuteNonQuery();
        }
        return existed;
    }

    public ISortedUlongIndex<T> OpenOrCreateUlongIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public ISortedGuidIndex<T> OpenOrCreateGuidIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public IUlongIndex<T> OpenOrCreateUlongHashIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public IGuidIndex<T> OpenOrCreateGuidHashIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public bool IsInTransaction => _inTxn;

    public void BeginTransaction()
    {
        if (_inTxn) throw new InvalidOperationException("A transaction is already active.");
        Exec("BEGIN IMMEDIATE");
        _inTxn = true;
    }

    public void CommitTransaction(long timestamp, bool durable)
    {
        if (!_inTxn) throw new InvalidOperationException("No active transaction.");
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO meta(k, ts) VALUES(0, @ts) ON CONFLICT(k) DO UPDATE SET ts=excluded.ts";
            cmd.Parameters.AddWithValue("@ts", timestamp);
            cmd.ExecuteNonQuery();
        }
        Exec("COMMIT");
        _inTxn = false;
        _timestamp = timestamp;
        foreach (object open in _openIndexes.Values)
            ((ISqliteIndexInternal)open).AdoptEngineTimestamp();
    }

    public void RollbackTransaction()
    {
        if (!_inTxn) throw new InvalidOperationException("No active transaction.");
        Exec("ROLLBACK");
        _inTxn = false;
    }

    public long GetTimestamp() => _timestamp;

    public void SetTimestamp(long timestamp)
    {
        if (_inTxn) throw new InvalidOperationException("SetTimestamp cannot run while a transaction is active.");
        BeginTransaction();
        CommitTransaction(timestamp, durable: true);
    }

    public long GetTotalDiskSpace()
    {
        long total = 0;
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            var fi = new FileInfo(_path + suffix);
            if (fi.Exists) total += fi.Length;
        }
        return total;
    }

    public void DeleteAll()
    {
        if (_inTxn) throw new InvalidOperationException("DeleteAll cannot run while a transaction is active.");
        BeginTransaction();
        foreach (object open in _openIndexes.Values)
            ((ISqliteIndexInternal)open).ClearData();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM catalog";
            var doomed = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    if (!_openIndexes.ContainsKey(r.GetString(0)))
                        doomed.Add(r.GetString(0));
            foreach (string name in doomed)
            {
                Exec($"DROP TABLE IF EXISTS \"t_{name}\"");
                using var del = _con.CreateCommand();
                del.CommandText = "DELETE FROM catalog WHERE name=@n";
                del.Parameters.AddWithValue("@n", name);
                del.ExecuteNonQuery();
            }
        }
        CommitTransaction(0, durable: true);
        _timestamp = 0;
    }

    public void DeleteUnopenedIndexes()
    {
        if (_inTxn) throw new InvalidOperationException("DeleteUnopenedIndexes cannot run while a transaction is active.");
        BeginTransaction();
        var doomed = new List<string>();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM catalog";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!_openIndexes.ContainsKey(r.GetString(0)))
                    doomed.Add(r.GetString(0));
        }
        foreach (string name in doomed)
        {
            Exec($"DROP TABLE IF EXISTS \"t_{name}\"");
            using var del = _con.CreateCommand();
            del.CommandText = "DELETE FROM catalog WHERE name=@n";
            del.Parameters.AddWithValue("@n", name);
            del.ExecuteNonQuery();
        }
        CommitTransaction(_timestamp, durable: true);
    }

    public void Dispose()
    {
        foreach (object open in _openIndexes.Values)
            (open as IDisposable)?.Dispose();
        _con.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

internal interface ISqliteIndexInternal
{
    void AdoptEngineTimestamp();
    void ClearData();
}

/// <summary>Maps scenario value types to SQLite parameter/reader representations that keep the same order.</summary>
internal static class SqliteValueMap<T> where T : notnull
{
    public static readonly string ColumnType;
    public static readonly Func<T, object> ToDb;
    public static readonly Func<SqliteDataReader, int, T> FromDb;

    static SqliteValueMap()
    {
        Type t = typeof(T);
        if (t == typeof(int))
        {
            ColumnType = "INTEGER";
            ToDb = v => (long)(int)(object)v;
            FromDb = (r, i) => (T)(object)(int)r.GetInt64(i);
        }
        else if (t == typeof(long))
        {
            ColumnType = "INTEGER";
            ToDb = v => (long)(object)v;
            FromDb = (r, i) => (T)(object)r.GetInt64(i);
        }
        else if (t == typeof(double))
        {
            ColumnType = "REAL";
            ToDb = v => (double)(object)v;
            FromDb = (r, i) => (T)(object)r.GetDouble(i);
        }
        else if (t == typeof(string))
        {
            ColumnType = "TEXT";
            ToDb = v => (string)(object)v;
            FromDb = (r, i) => (T)(object)r.GetString(i);
        }
        else if (t == typeof(Guid))
        {
            ColumnType = "BLOB";
            ToDb = v =>
            {
                byte[] b = new byte[16];
                ((Guid)(object)v).TryWriteBytes(b, bigEndian: true, out _);
                return b;
            };
            FromDb = (r, i) => (T)(object)new Guid((byte[])r.GetValue(i), bigEndian: true);
        }
        else if (t == typeof(DateTime))
        {
            ColumnType = "INTEGER";
            ToDb = v => ((DateTime)(object)v).Ticks;
            FromDb = (r, i) => (T)(object)new DateTime(r.GetInt64(i), DateTimeKind.Utc);
        }
        else
        {
            throw new NotSupportedException($"SQLite value mapping does not support '{t}'.");
        }
    }
}

/// <summary>
/// The unordered layout: the table alone, keyed by id. <see cref="SqliteIndex{T}"/> derives from it
/// and adds the covering (v, id) index, so the two rows of the benchmark differ by exactly that
/// index — the writes that maintain it, the disk it occupies, and the queries it makes possible.
/// </summary>
public class SqliteHashIndex<T> : IIntIndex<T>, ISqliteIndexInternal, IDisposable where T : notnull
{
    private readonly SqliteEngine _engine;
    protected readonly SqliteConnection Con;
    protected readonly string Table;
    private readonly Dictionary<string, SqliteCommand> _commands = new();
    private bool _hasEngineTimestamp;

    internal SqliteHashIndex(SqliteEngine engine, string name, bool hasEngineTimestamp)
    {
        _engine = engine;
        Con = engine.Connection;
        Table = $"\"t_{name}\"";
        _hasEngineTimestamp = hasEngineTimestamp;
        using var cmd = Con.CreateCommand();
        // The DROP matters: reopening a table that was created as sorted must not keep serving
        // (and paying for) an index this layout claims not to have.
        cmd.CommandText =
            $"CREATE TABLE IF NOT EXISTS {Table} (id INTEGER NOT NULL PRIMARY KEY, v {SqliteValueMap<T>.ColumnType} NOT NULL);" +
            (Ordered ? $"CREATE INDEX IF NOT EXISTS \"i_{name}\" ON {Table} (v, id);"
                     : $"DROP INDEX IF EXISTS \"i_{name}\";");
        cmd.ExecuteNonQuery();
    }

    /// <summary>Whether this layout keeps the (v, id) index; read by the constructor, so it must not touch instance state.</summary>
    protected virtual bool Ordered => false;

    protected SqliteCommand Cmd(string key, string sql)
    {
        if (_commands.TryGetValue(key, out var cmd)) return cmd;
        cmd = Con.CreateCommand();
        cmd.CommandText = sql;
        _commands[key] = cmd;
        return cmd;
    }

    protected static SqliteParameter Par(SqliteCommand cmd, string name)
    {
        if (cmd.Parameters.Contains(name)) return cmd.Parameters[name];
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    public int Count
    {
        get
        {
            var cmd = Cmd("count", $"SELECT COUNT(*) FROM {Table}");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Set(int id, T value)
    {
        var cmd = Cmd("set", $"INSERT INTO {Table}(id, v) VALUES(@id, @v) ON CONFLICT(id) DO UPDATE SET v=excluded.v");
        Par(cmd, "@id").Value = (long)id;
        Par(cmd, "@v").Value = SqliteValueMap<T>.ToDb(value);
        cmd.ExecuteNonQuery();
    }

    public bool Remove(int id)
    {
        var cmd = Cmd("del", $"DELETE FROM {Table} WHERE id=@id");
        Par(cmd, "@id").Value = (long)id;
        return cmd.ExecuteNonQuery() > 0;
    }

    public T GetValue(int id)
        => TryGetValue(id, out T value) ? value : throw new KeyNotFoundException($"Id {id} not found.");

    public bool TryGetValue(int id, out T value)
    {
        var cmd = Cmd("get", $"SELECT v FROM {Table} WHERE id=@id");
        Par(cmd, "@id").Value = (long)id;
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            value = SqliteValueMap<T>.FromDb(r, 0);
            return true;
        }
        value = default!;
        return false;
    }

    public bool ContainsKey(int id)
    {
        var cmd = Cmd("hasid", $"SELECT EXISTS(SELECT 1 FROM {Table} WHERE id=@id)");
        Par(cmd, "@id").Value = (long)id;
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>Without the (v, id) index this is a table scan — the query is the same, the plan is not.</summary>
    public IEnumerable<int> GetIds(T value)
    {
        var cmd = Cmd("ids", $"SELECT id FROM {Table} WHERE v=@v ORDER BY id");
        Par(cmd, "@v").Value = SqliteValueMap<T>.ToDb(value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return (int)r.GetInt64(0);
    }

    public IEnumerable<KeyValuePair<int, T>> Entries
    {
        get
        {
            var cmd = Cmd("entries", $"SELECT id, v FROM {Table} ORDER BY id");
            using var r = cmd.ExecuteReader();
            while (r.Read()) yield return new((int)r.GetInt64(0), SqliteValueMap<T>.FromDb(r, 1));
        }
    }

    public IEnumerable<int> Keys
    {
        get
        {
            var cmd = Cmd("keys", $"SELECT id FROM {Table} ORDER BY id");
            using var r = cmd.ExecuteReader();
            while (r.Read()) yield return (int)r.GetInt64(0);
        }
    }

    public long GetTimestamp() => _hasEngineTimestamp ? _engine.GetTimestamp() : 0;

    public void SetTimestamp(long timestamp)
    {
        if (timestamp == 0) { _hasEngineTimestamp = false; return; }
        if (timestamp != _engine.GetTimestamp())
            throw new InvalidOperationException("An index timestamp is always 0 or the engine's current timestamp.");
        _hasEngineTimestamp = true;
    }

    void ISqliteIndexInternal.AdoptEngineTimestamp() => _hasEngineTimestamp = true;

    void ISqliteIndexInternal.ClearData()
    {
        using var cmd = Con.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table}";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var cmd in _commands.Values) cmd.Dispose();
        _commands.Clear();
    }
}

public sealed class SqliteIndex<T> : SqliteHashIndex<T>, ISortedIntIndex<T> where T : notnull
{
    internal SqliteIndex(SqliteEngine engine, string name, bool hasEngineTimestamp)
        : base(engine, name, hasEngineTimestamp) { }

    protected override bool Ordered => true;

    public int DistinctValueCount
    {
        get
        {
            var cmd = Cmd("dcount", $"SELECT COUNT(DISTINCT v) FROM {Table}");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public bool ContainsValue(T value)
    {
        var cmd = Cmd("hasv", $"SELECT EXISTS(SELECT 1 FROM {Table} WHERE v=@v)");
        Par(cmd, "@v").Value = SqliteValueMap<T>.ToDb(value);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    public IEnumerable<T> DistinctValues
    {
        get
        {
            var cmd = Cmd("dvalues", $"SELECT DISTINCT v FROM {Table} ORDER BY v");
            using var r = cmd.ExecuteReader();
            while (r.Read()) yield return SqliteValueMap<T>.FromDb(r, 0);
        }
    }

    public T GetMinValue()
    {
        var cmd = Cmd("minv", $"SELECT v FROM {Table} ORDER BY v LIMIT 1");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("The index is empty.");
        return SqliteValueMap<T>.FromDb(r, 0);
    }

    public T GetMaxValue()
    {
        var cmd = Cmd("maxv", $"SELECT v FROM {Table} ORDER BY v DESC LIMIT 1");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("The index is empty.");
        return SqliteValueMap<T>.FromDb(r, 0);
    }

    private SqliteCommand RangeCmd(string what, string select, bool hasFrom, T from, bool hasTo, T to, bool includeFrom, bool includeTo, bool descending)
    {
        string opFrom = includeFrom ? ">=" : ">";
        string opTo = includeTo ? "<=" : "<";
        string where =
            hasFrom && hasTo ? $"WHERE v {opFrom} @from AND v {opTo} @to"
            : hasFrom ? $"WHERE v {opFrom} @from"
            : hasTo ? $"WHERE v {opTo} @to"
            : "";
        string order = descending ? "ORDER BY v DESC, id DESC" : "ORDER BY v, id";
        string key = $"{what}|{where}|{descending}";
        var cmd = Cmd(key, $"SELECT {select} FROM {Table} {where} {order}");
        if (hasFrom) Par(cmd, "@from").Value = SqliteValueMap<T>.ToDb(from);
        if (hasTo) Par(cmd, "@to").Value = SqliteValueMap<T>.ToDb(to);
        return cmd;
    }

    private SqliteCommand CountCmd(bool hasFrom, T from, bool hasTo, T to, bool includeFrom, bool includeTo)
    {
        string opFrom = includeFrom ? ">=" : ">";
        string opTo = includeTo ? "<=" : "<";
        string where =
            hasFrom && hasTo ? $"WHERE v {opFrom} @from AND v {opTo} @to"
            : hasFrom ? $"WHERE v {opFrom} @from"
            : $"WHERE v {opTo} @to";
        var cmd = Cmd($"c|{where}", $"SELECT COUNT(*) FROM {Table} {where}");
        if (hasFrom) Par(cmd, "@from").Value = SqliteValueMap<T>.ToDb(from);
        if (hasTo) Par(cmd, "@to").Value = SqliteValueMap<T>.ToDb(to);
        return cmd;
    }

    public IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
    {
        var cmd = RangeCmd("rid", "id", true, from, true, to, includeFrom, includeTo, descending);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return (int)r.GetInt64(0);
    }

    public IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
    {
        var cmd = RangeCmd("rent", "id, v", true, from, true, to, includeFrom, includeTo, descending);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return new((int)r.GetInt64(0), SqliteValueMap<T>.FromDb(r, 1));
    }

    public IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
    {
        var cmd = RangeCmd("rgt", "id", true, value, false, default!, includeValue, true, descending);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return (int)r.GetInt64(0);
    }

    public IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
    {
        var cmd = RangeCmd("rlt", "id", false, default!, true, value, true, includeValue, descending);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return (int)r.GetInt64(0);
    }

    public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
        => Convert.ToInt32(CountCmd(true, from, true, to, includeFrom, includeTo).ExecuteScalar());

    public int CountIdsGreaterThan(T value, bool includeValue = true)
        => Convert.ToInt32(CountCmd(true, value, false, default!, includeValue, true).ExecuteScalar());

    public int CountIdsSmallerThan(T value, bool includeValue = true)
        => Convert.ToInt32(CountCmd(false, default!, true, value, true, includeValue).ExecuteScalar());
}
