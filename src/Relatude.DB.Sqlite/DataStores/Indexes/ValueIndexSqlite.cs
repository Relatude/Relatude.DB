using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

public class ValueIndexSqlite<T> : IValueIndex<T> where T : notnull {
    readonly string _indexId;
    readonly PersistedIndexStore _store;
    readonly StateIdValueTracker<T> _stateId;
    readonly SetRegister _sets;
    readonly string _tableName;
    public ValueIndexSqlite(SetRegister sets, PersistedIndexStore store, string indexId) {
        _indexId = indexId;
        _store = store;
        _stateId = new(sets);
        _sets = sets;
        _tableName = store.GetTableName(indexId);
        Timestamp = _store.GetTimestamp(_indexId);
    }
    int _idCount = -1;
    public int IdCount {
        get {
            if (_idCount == -1) {
                using var cmd = _store.CreateCommand("SELECT COUNT(*) FROM " + _tableName);
                _idCount = _store.CastFromDb<int>(cmd.ExecuteScalar());
            }
            return _idCount;
        }
    }

    void add(int id, T value) {
        using var cmd = _store.CreateCommand("INSERT INTO " + _tableName + " (id, value) VALUES (@id, @value)");
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        cmd.ExecuteNonQuery();
        _stateId.RegisterAddition(id, value);
        if (_idCount != -1) _idCount++;
    }
    void remove(int id, T value) {
        using var cmd = _store.CreateCommand("DELETE FROM " + _tableName + " WHERE id = @id");
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        _stateId.RegisterRemoval(id, value);
        if (_idCount != -1) _idCount--;
    }
    public void Add(int id, object value) => add(id, (T)value);
    public void Remove(int id, object value) => remove(id, (T)value);
    public void RegisterAddDuringStateLoad(int nodeId, object value, long timestampId) {
        if (timestampId > Timestamp) {
            add(nodeId, (T)value);
            Timestamp = timestampId; // not really needed as timestamp is set when state load is completed
        }
    }
    public void RegisterRemoveDuringStateLoad(int nodeId, object value, long timestampId) {
        if (timestampId > Timestamp) {
            remove(nodeId, (T)value);
            Timestamp = timestampId; // not really needed as timestamp is set when state load is completed
        }
    }
    public long Timestamp { get; set; } // only set during state load
    public void ReadStateForMemoryIndexes() { } // not relevant for sqlite indexes  
    public void SaveStateForMemoryIndexes(long timestampId) { } // not relevant for sqlite indexes  

    public IEnumerable<int> Ids {
        get {
            using var cmd = _store.CreateCommand("SELECT id FROM " + _tableName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) yield return reader.GetInt32(0);
        }
    }
    public long StateId => _stateId.Current;
    public IEnumerable<T> UniqueValues {
        get {
            using var cmd = _store.CreateCommand("SELECT DISTINCT value FROM " + _tableName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) yield return _store.CastFromDb<T>(reader.GetValue(0));
        }
    }
    public int ValueCount {
        get {
            using var cmd = _store.CreateCommand("SELECT COUNT(DISTINCT value) FROM " + _tableName);
            return _store.CastFromDb<int>(cmd.ExecuteScalar());
        }
    }
    public string UniqueKey => _indexId;
    public void ClearCache() { }
    public void CompressMemory() { }
    public bool ContainsValue(T value) {
        using var cmd = _store.CreateCommand("SELECT COUNT(*) FROM " + _tableName + " WHERE value = @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        return _store.CastFromDb<int>(cmd.ExecuteScalar()) > 0;
    }
    public int CountEqual(IdSet nodeIds, T value) => _sets.CountEqual(this, nodeIds, value);
    public int CountGreaterThan(T value, bool inclusive) {
        using var cmd = _store.CreateCommand("SELECT COUNT(*) FROM " + _tableName + " WHERE value " + (inclusive ? ">=" : ">") + " @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        return (int)cmd.ExecuteScalar()!;
    }
    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) => _sets.CountInRange(this, nodeIds, from, to, fromInclusive, toInclusive);
    public int CountLessThan(T value, bool inclusive) {
        using var cmd = _store.CreateCommand("SELECT COUNT(*) FROM " + _tableName + " WHERE value " + (inclusive ? "<=" : "<") + " @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        return _store.CastFromDb<int>(cmd.ExecuteScalar());
    }
    public Task<JobResult> DequeueTasks() => Task.FromResult(new JobResult(0, 0, string.Empty));
    public int GetQueuedTaskCount() => 0;
    public void Dispose() { }

    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) => _sets.Filter(this, nodeIds, op, v);
    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues) => _sets.FilterInValues(this, nodeIds, selectedValues);
    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges) => _sets.FilterRanges(this, nodeIds, selectedRanges);
    public IdSet FilterRangesObject(IdSet set, object from, object to) => _sets.FilterRangesObject(this, set, from, to);
    public IdSet ReOrder(IdSet unsorted, bool descending) => _sets.OrderBy(this, unsorted, descending);

    public object[] GetCacheKey(T queryValue, QueryType queryType) {
        return [queryType, queryValue]; // further optimization possible....
    }
    public ICollection<int> GetIds(T value) {
        using var cmd = _store.CreateCommand("SELECT id FROM " + _tableName + " WHERE value = @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        using var reader = cmd.ExecuteReader();
        var result = new List<int>();
        while (reader.Read()) result.Add(reader.GetInt32(0));
        return result;
    }

    public T GetValue(int nodeId) {
        using var cmd = _store.CreateCommand("SELECT value FROM " + _tableName + " WHERE id = @id");
        cmd.Parameters.AddWithValue("@id", nodeId);
        return _store.CastFromDb<T>(cmd.ExecuteScalar());
    }
    public IEnumerable<int> GreaterThan(T value, bool inclusive) {
        using var cmd = _store.CreateCommand("SELECT id FROM " + _tableName + " WHERE value " + (inclusive ? ">=" : ">") + " @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return reader.GetInt32(0);
    }
    public IEnumerable<int> LessThan(T value, bool inclusive) {
        using var cmd = _store.CreateCommand("SELECT id FROM " + _tableName + " WHERE value " + (inclusive ? "<=" : "<") + " @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return reader.GetInt32(0);
    }
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) {
        using var cmd = _store.CreateCommand("SELECT COUNT(*) FROM " + _tableName + " WHERE id IN (" + ids.AsStringList() + ") AND value " + (fromInclusive ? ">=" : ">") + " @from AND value " + (toInclusive ? "<=" : "<") + " @to");
        cmd.Parameters.AddWithValue("@from", _store.CastToDb(from));
        cmd.Parameters.AddWithValue("@to", _store.CastToDb(to));
        return _store.CastFromDb<int>(cmd.ExecuteScalar());
    }

    static readonly Comparer<T> comparer = Comparer<T>.Default;
    public int MaxCount(IndexOperator op, T value) {
        // optimized for fastest speed, not accuracy, important for performance of query engine
        if (IdCount == 0) return 0;
        if (comparer.Compare(value, MaxValue()) > 0) return 0; // value is larger than max value in index
        if (comparer.Compare(value, MinValue()) < 0) return 0; // value is smaller than min value in index
        return op switch {
            IndexOperator.Equal => countEqual(value),
            IndexOperator.NotEqual => ValueCount - countEqual(value),
            _ => IdCount,
        };
    }
    int countEqual(T value) {
        using var cmd = _store.CreateCommand("SELECT COUNT(*) FROM " + _tableName + " WHERE value = @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        return (int)cmd.ExecuteScalar()!;
    }
    public T? MaxValue() {
        using var cmd = _store.CreateCommand("SELECT MAX(value) FROM " + _tableName);
        return _store.CastFromDb<T>(cmd.ExecuteScalar());
    }
    public T? MinValue() {
        using var cmd = _store.CreateCommand("SELECT MIN(value) FROM " + _tableName);
        return _store.CastFromDb<T>(cmd.ExecuteScalar());
    }
    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive) {
        using var cmd = _store.CreateCommand("SELECT id FROM " + _tableName + " WHERE value " + (fromInclusive ? ">=" : ">") + " @from AND value " + (toInclusive ? "<=" : "<") + " @to");
        cmd.Parameters.AddWithValue("@from", _store.CastToDb(from));
        cmd.Parameters.AddWithValue("@to", _store.CastToDb(to));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return reader.GetInt32(0);
    }
    public IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive) {
        throw new NotImplementedException();
    }
}

