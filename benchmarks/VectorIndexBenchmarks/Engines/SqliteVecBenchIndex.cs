using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// sqlite-vec (asg017), a loadable SQLite extension, driven through <c>Microsoft.Data.Sqlite</c> —
/// the same client <c>Relatude.DB.Sqlite</c> already uses. Vectors live in a <c>vec0</c> virtual
/// table, so this is the vector-side counterpart of what TextIndexBenchmarks measures against FTS5:
/// an ordinary embedded database put to the job.
///
/// <para><b>It is exact.</b> vec0 has no approximate index — a KNN query scans every stored vector
/// and ranks it. That makes it the natural neighbour of the Relatude disk index at accuracy 1 and
/// of the in-memory index: same answers, different machinery, so the comparison is purely about
/// how fast a correct answer comes off disk. Its recall must therefore come out at 100%.</para>
///
/// <para><b>A similarity floor buys it nothing.</b> A vec0 KNN query takes a k and nothing else —
/// a distance predicate cannot be combined with the match, so the floor is applied after the rows
/// come back. Every other implementation here can use the floor to stop work early; this one
/// cannot, which is a real property of the engine rather than an artifact of the adapter.</para>
///
/// <para><b>Durability</b> is the SQLite transaction: writes are batched into one and committed at
/// the state save, which is the cadence the data store drives its index engines at. There is no
/// cheaper delta hook, so the WAL-flush phase is skipped.</para>
/// </summary>
public sealed class SqliteVecBenchIndex : IBenchVectorIndex {
    readonly SqliteConnection _conn;
    readonly string _dir, _dbPath;
    readonly SqliteCommand _insert, _update, _delete;
    readonly SqliteParameter _insertId, _insertVec, _updateId, _updateVec, _deleteId;
    SqliteTransaction? _tx;

    public SqliteVecBenchIndex(string dir, int dimensions, long cacheBytes) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _dbPath = Path.Combine(dir, "vectors.db");
        var fresh = !File.Exists(_dbPath);
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        _conn.EnableExtensions(true);
        _conn.LoadExtension(ExtensionPath());
        // The page cache is SQLite's equivalent of the disk index's block-cache budget, so it gets
        // the same one. Negative means KiB rather than pages.
        exec($"pragma cache_size = -{Math.Max(64, cacheBytes / 1024)}");
        if (fresh) exec($"create virtual table v using vec0(id integer primary key, embedding float[{dimensions}] distance_metric=cosine)");

        _insert = _conn.CreateCommand();
        _insert.CommandText = "insert into v(id, embedding) values ($id, $e)";
        _insertId = param(_insert, "$id");
        _insertVec = param(_insert, "$e");
        // vec0 rejects INSERT OR REPLACE (the primary key is already taken), but takes an UPDATE.
        _update = _conn.CreateCommand();
        _update.CommandText = "update v set embedding = $e where id = $id";
        _updateId = param(_update, "$id");
        _updateVec = param(_update, "$e");
        _delete = _conn.CreateCommand();
        _delete.CommandText = "delete from v where id = $id";
        _deleteId = param(_delete, "$id");
    }

    public Features Supported => Features.UnrankedFilter;

    public void Add(int nodeId, float[] vector) {
        begin();
        _insertId.Value = nodeId;
        _insertVec.Value = ToBytes(vector);
        _insert.ExecuteNonQuery();
    }
    public void Update(int nodeId, float[] vector) {
        begin();
        _updateId.Value = nodeId;
        _updateVec.Value = ToBytes(vector);
        _update.ExecuteNonQuery();
    }
    public void Remove(int nodeId) {
        begin();
        _deleteId.Value = nodeId;
        _delete.ExecuteNonQuery();
    }

    public IReadOnlyList<int> SearchRanked(in BenchQuery query, int top, int maxHits, float minSimilarity) {
        // k is the whole instruction a vec0 KNN query takes: it ranks every stored vector and
        // returns the best k. Ask for maxHits, as the others are asked to evaluate maxHits.
        using var cmd = command();
        cmd.CommandText = "select id, distance from v where embedding match $q and k = $k order by distance";
        param(cmd, "$q").Value = ToBytes(query.Vector);
        param(cmd, "$k").Value = maxHits;
        var maxDistance = 1f - minSimilarity;
        var ids = new List<int>(Math.Min(top, maxHits));
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && ids.Count < top) {
            if (reader.GetDouble(1) > maxDistance) break; // ordered by distance, so the rest are worse
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    public IBenchIdSet SearchIds(in BenchQuery query, float minSimilarity) {
        // No k here: a plain scan with a distance predicate, which is how you ask a vec0 table for
        // "everything this similar".
        using var cmd = command();
        cmd.CommandText = "select id from v where vec_distance_cosine(embedding, $q) <= $d";
        param(cmd, "$q").Value = ToBytes(query.Vector);
        param(cmd, "$d").Value = (double)(1f - minSimilarity);
        var ids = new HashSet<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return new HashBenchIdSet(ids);
    }

    /// <summary>Commits the open transaction: with the default rollback journal that is a durable,
    /// fsynced commit, which is what the other implementations' state saves also achieve.</summary>
    public void SaveState(long timestamp) {
        if (_tx is null) return;
        _tx.Commit();
        _tx.Dispose();
        _tx = null;
        enlist();
    }
    public void MakeDurable(long timestamp) => throw new NotSupportedException();

    public long DiskBytes => Harness.Engines.FolderBytes(_dir);

    public void Dispose() {
        try { _tx?.Commit(); } catch { /* nothing durable was promised for an unsaved transaction */ }
        _tx?.Dispose();
        _insert.Dispose();
        _update.Dispose();
        _delete.Dispose();
        _conn.Close();
        _conn.Dispose();
        // the reopen step measures opening the file, which needs the handle actually released
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Writes are batched into one transaction, committed at the state save — the cadence
    /// the data store drives its index engines at, and the only way a row-per-statement client
    /// reaches a sane insert rate.</summary>
    void begin() {
        if (_tx is not null) return;
        _tx = _conn.BeginTransaction();
        enlist();
    }
    /// <summary>Microsoft.Data.Sqlite refuses to run a command that has no transaction while one is
    /// pending on its connection, so every command has to be told about the current one.</summary>
    void enlist() {
        _insert.Transaction = _tx;
        _update.Transaction = _tx;
        _delete.Transaction = _tx;
    }
    SqliteCommand command() {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        return cmd;
    }
    void exec(string sql) {
        using var cmd = command();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    static SqliteParameter param(SqliteCommand cmd, string name) {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
    static byte[] ToBytes(float[] vector) {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// The nupkg leaves vec0 under <c>runtimes/&lt;rid&gt;/native/</c>, and the package's own
    /// <c>LoadVector()</c> helper passes the bare name "vec0" to SQLite — which asks the OS loader,
    /// not the .NET host, so it never looks there and the load fails with "the specified module
    /// could not be found". Resolving the full path is the fix.
    /// </summary>
    public static string ExtensionPath() {
        var (rid, file) = RuntimeInformation.ProcessArchitecture switch {
            _ when OperatingSystem.IsWindows() => ("win-x64", "vec0.dll"),
            _ when OperatingSystem.IsMacOS() => (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "vec0.dylib"),
            _ => (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64", "vec0.so"),
        };
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
        if (!File.Exists(path)) throw new FileNotFoundException($"The sqlite-vec extension was not found at '{path}'. ", path);
        return path;
    }
}
