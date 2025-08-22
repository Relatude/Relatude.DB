using Microsoft.Data.Sqlite;
using System.Text;
using WAF.Common;
namespace WAF.Tasks;
// Not threadsafe
public class SqliteQueueStore : IQueueStore {
    readonly SqliteConnection _connection;
    int? _cachedNoPendingTasks = null;
    readonly string _queueDbPath;
    public SqliteQueueStore(string queueDbPath) {
        _queueDbPath = queueDbPath;
        _connection = new SqliteConnection($"Data Source={_queueDbPath};");
        _connection.Open();
        executeNonQuery("PRAGMA journal_mode=WAL;");
        ensureTables();
    }
    void ensureTables() {
        executeNonQuery(@"
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                type_id TEXT,
                job_id TEXT,
                priority INTEGER,
                state INTEGER,
                created DATETIME,
                completed DATETIME DEFAULT NULL,
                error_type TEXT,
                error_message TEXT,
                task_count INTEGER DEFAULT 1,
                task_data BLOB
            );");
        executeNonQuery(@"
            CREATE INDEX IF NOT EXISTS idx_tasks_state ON tasks (state);
            CREATE INDEX IF NOT EXISTS idx_tasks_type_id ON tasks (type_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_priority ON tasks (priority);
            CREATE INDEX IF NOT EXISTS idx_tasks_job_id ON tasks (job_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_created ON tasks (created);");
    }
    KeyValuePair<string, object> P(string name, object value) => new(name, value);
    void executeNonQuery(string sql, params KeyValuePair<string, object>[] paramaters) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in paramaters) {
            cmd.Parameters.AddWithValue(param.Key, param.Value);
        }
        cmd.ExecuteNonQuery();
    }
    SqliteDataReader executeReader(string sql, params KeyValuePair<string, object>[] parameters) {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in parameters) {
            cmd.Parameters.AddWithValue(param.Key, param.Value);
        }
        return cmd.ExecuteReader();
    }
    T? executeScalar<T>(string sql, params KeyValuePair<string, object>[] parameters) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in parameters) {
            cmd.Parameters.AddWithValue(param.Key, param.Value);
        }
        var value = cmd.ExecuteScalar();
        if (value == null || DBNull.Value == value) return default;
        return (T)cmd.ExecuteScalar()!;
    }
    public void Delete(Guid[] batchIds) {
        _cachedNoPendingTasks = null; // invalidate cache
        // can be optimized to use IN clause
        foreach (var batchId in batchIds) {
            executeNonQuery("DELETE FROM tasks WHERE id = @id", P("@id", batchId.ToString()));
        }
    }
    public void Enqueue(IBatch batch, ITaskRunner runner) {
        _cachedNoPendingTasks = null; // invalidate cache
        if (batch.Meta.State != BatchState.Pending) throw new Exception("Only pending batches can be enqueued.");
        executeNonQuery("INSERT INTO tasks (id, type_id, job_id, priority, state, created, completed, task_count, task_data)"
            + " VALUES (@id, @type_id, @job_id, @priority, @state, @created, @completed, @taskCount, @taskData)",
            P("@id", batch.Meta.BatchId.ToString()),
            P("@type_id", batch.Meta.TaskTypeId),
            P("@job_id", batch.Meta.JobId ?? (object)DBNull.Value), // job_id can be null
            P("@priority", (int)batch.Meta.Priority),
            P("@state", (int)batch.Meta.State),
            P("@created", batch.Meta.CreatedUtc.ToString("o")),
            P("@completed", DBNull.Value), // NULL for pending tasks
            P("@taskCount", batch.GenericTasks.Count()),
            P("@taskData", batch.TasksToBytes(runner)));
    }
    public IBatch? DequeueAndSetRunning(Dictionary<string, ITaskRunner> runners) {
        _cachedNoPendingTasks = null; // invalidate cache
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type_id, job_id, priority, state, created, completed, error_type, error_message, task_count, task_data
            FROM tasks
            WHERE state = @state
            ORDER BY priority DESC, created ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("@state", (int)BatchState.Pending);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null; // no pending tasks

        var batchId = Guid.Parse(reader.GetString(0));
        var typeId = reader.GetString(1);
        var jobId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var priority = (BatchTaskPriority)reader.GetInt32(3);
        var state = BatchState.Running; // (BatchState)reader.GetInt32(4);
        var created = reader.GetDateTime(5);
        DateTime? completed = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
        string? errorType = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? errorMessage = reader.IsDBNull(8) ? null : reader.GetString(8);
        int taskCount = reader.GetInt32(9);
        byte[] taskData = reader.GetFieldValue<byte[]>(10);

        var meta = new BatchMeta(batchId, typeId, state, priority, created) {
            JobId = jobId,
            Completed = completed,
            ErrorType = errorType,
            ErrorMessage = errorMessage
        };

        if (!runners.TryGetValue(typeId, out var runner)) {
            throw new Exception("No task runner registered for type: " + typeId);
        }
        var batch = runner.GetBatchFromMetaAndData(meta, taskData);

        // Update the state to Processing
        Set([batchId], BatchState.Running);

        return batch;
    }
    public void FlushDiskIfNeeded() {
        //_cachedNoPendingTasks = null; // invalidate cache
        //executeNonQuery("PRAGMA wal_checkpoint(TRUNCATE);"); // force checkpoint to flush WAL to disk
    }
    public void Set(Guid[] batchIds, BatchState state) {
        _cachedNoPendingTasks = null; // invalidate cache
        var idList = string.Join(",", batchIds.Select(id => $"'{id}'"));
        executeNonQuery($"UPDATE tasks SET state = @state WHERE id IN ({idList})", P("@state", (int)state));
    }
    public void Set(string jobId, BatchState state) {
        _cachedNoPendingTasks = null; // invalidate cache
        executeNonQuery("UPDATE tasks SET state = @state WHERE job_id = @jobId", P("@state", (int)state), P("@jobId", jobId ?? (object)DBNull.Value));
    }
    public void Set(Guid batchId, Exception error) {
        _cachedNoPendingTasks = null; // invalidate cache
        executeNonQuery("UPDATE tasks SET state = @state, error_type = @errorType, error_message = @errorMessage WHERE id = @id",
            P("@state", (int)BatchState.Failed),
            P("@errorType", error.GetType().FullName ?? "Unknown"),
            P("@errorMessage", error.Message),
            P("@id", batchId.ToString()));
    }
    public int CountBatch(BatchState state) {
        if (state == BatchState.Pending && _cachedNoPendingTasks.HasValue) return _cachedNoPendingTasks.Value;
        var cnt = (int)executeScalar<long>("SELECT COUNT(*) FROM tasks WHERE state = @state", P("@state", (int)state));
        if (state == BatchState.Pending) _cachedNoPendingTasks = cnt;
        return cnt;
    }
    public int CountTasks(BatchState state) {
        if (state == BatchState.Pending && _cachedNoPendingTasks.HasValue) return _cachedNoPendingTasks.Value;
        var cnt = (int)executeScalar<long>("SELECT SUM(task_count) FROM tasks WHERE state = @state", P("@state", (int)state));
        if (state == BatchState.Pending) _cachedNoPendingTasks = cnt;
        return cnt;
    }
    string sqlSafeString(string value) => $"'{value.Replace("'", "''")}'"; // simple SQL injection prevention  
    public BatchMetaWithCount[] GetBatchInfo(BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize, out int totalCount) {
        var stateList = string.Join(",", states.Select(s => (int)s));
        var typeIdList = string.Join(",", typeIds.Select(sqlSafeString));
        var jobIdList = string.Join(",", jobIds.Select(sqlSafeString));
        var offset = page * pageSize;
        string where = string.Empty;
        var conditions = new List<string>();
        if (states.Length > 0) conditions.Add($"state IN ({stateList})");
        if (typeIds.Length > 0) conditions.Add($"type_id IN ({typeIdList})");
        if (jobIds.Length > 0) conditions.Add($"job_id IN ({jobIdList})");
        where = "WHERE " + string.Join(" AND ", conditions);
        string sql = $@"SELECT id, type_id, priority, state, created, completed, error_type, error_message, task_count 
            FROM tasks {where} ORDER BY created ASC LIMIT {pageSize} OFFSET {offset}";
        using var reader = executeReader(sql);

        var batches = new List<BatchMetaWithCount>();
        while (reader.Read()) {
            var batchId = Guid.Parse(reader.GetString(0));
            var typeId = reader.GetString(1);
            var priority = (BatchTaskPriority)reader.GetInt32(2);
            var state = (BatchState)reader.GetInt32(3);
            var created = reader.GetDateTime(4);
            DateTime? completed = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
            string? errorType = reader.IsDBNull(6) ? null : reader.GetString(6);
            string? errorMessage = reader.IsDBNull(7) ? null : reader.GetString(7);
            int taskCount = reader.GetInt32(8);
            var info = new BatchMeta(batchId, typeId, state, priority, created) {
                Completed = completed,
                ErrorType = errorType,
                ErrorMessage = errorMessage
            };
            batches.Add(new(info, taskCount));
        }

        totalCount = (int)executeScalar<long>($"SELECT COUNT(*) FROM tasks {where}");
        return [.. batches];
    }
    public void Delete(BatchState[] states, string[] typeIds) {
        _cachedNoPendingTasks = null; // invalidate cache
        if (states.Length == 0 && typeIds.Length == 0) return; // nothing to delete

        var stateList = string.Join(",", states.Select(s => (int)s));
        var typeIdList = string.Join(",", typeIds.Select(sqlSafeString));
        string where = string.Empty;
        if (states.Length > 0 && typeIds.Length > 0) where = $"WHERE state IN ({stateList}) AND type_id IN ({typeIdList})";
        else if (states.Length > 0) where = $"WHERE state IN ({stateList})";
        else if (typeIds.Length > 0) where = $"WHERE type_id IN ({typeIdList})";

        executeNonQuery($"DELETE FROM tasks {where}");
    }
    public void Dispose() {
        _connection.Close();
        _connection.Dispose();
    }
    public void ReOpen() {
        if (_connection.State != System.Data.ConnectionState.Closed) return; // already open
        _connection.Open();
        executeNonQuery("PRAGMA journal_mode=WAL;"); // reapply WAL mode
    }
}
