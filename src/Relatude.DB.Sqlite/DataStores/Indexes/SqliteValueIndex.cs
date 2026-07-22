using System.Text;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

public class SqliteValueIndex<T> : PersistedIndexBase, IValueIndex<T>, GapCacheKeyBuilder<T>.IGapSource where T : notnull {
    readonly string _indexId;
    readonly SqliteIndexStore _store;
    readonly StateIdValueTracker<T> _stateId;
    readonly SetRegister _sets;
    readonly string _tableName;
    readonly GapCacheKeyBuilder<T> _keyBuilder;
    public SqliteValueIndex(SetRegister sets, SqliteIndexStore store, string indexId, string tableName, string friendlyName, bool justCreated)
        : base(store, justCreated) {
        _indexId = indexId;
        _store = store;
        _stateId = new();
        _sets = sets;
        _tableName = tableName;
        FriendlyName = friendlyName;
        _keyBuilder = new GapCacheKeyBuilder<T>(this);
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
    public void Add(int id, T value) => add(id, value);
    public void Remove(int id, T value) => remove(id, value);

    public void RegisterAddDuringStateLoad(int nodeId, object value) => add(nodeId, (T)value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => remove(nodeId, (T)value);
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
    public void ClearCache() { _keyBuilder.Clear(); _idCount = -1; }
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
        return _store.CastFromDb<int>(cmd.ExecuteScalar());
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

    // Cache-key logic lives in the shared GapCacheKeyBuilder; the ordering-sensitive pieces are
    // provided through IGapSource below. See GapCacheKeyBuilder for the full explanation.
    public object[] GetCacheKey(T queryValue, QueryType queryType) => _keyBuilder.GetCacheKey(queryValue, queryType);

    // Bounds are kept as their raw db representation (string for TEXT columns, else T) and never
    // parsed back to T: CastFromDb's DateTime.Parse converts "Z" values to local time, so a
    // round-tripped bound would re-serialize to a different string than the one the db ordered by.
    GapCacheKeyBuilder<T>.Gap GapCacheKeyBuilder<T>.IGapSource.BuildGap(T value) { // value is known not to be in the index
        var (lower, countBelow) = boundAndCount(value, "MAX", "<");
        var (upper, countAbove) = boundAndCount(value, "MIN", ">");
        return new GapCacheKeyBuilder<T>.Gap { StateId = StateId, CountBelow = countBelow, CountAbove = countAbove, Lower = lower, Upper = upper };
    }
    bool GapCacheKeyBuilder<T>.IGapSource.InGap(GapCacheKeyBuilder<T>.Gap gap, T value) {
        if (gap.Lower != null && compareDbOrder(value, gap.Lower) <= 0) return false;
        if (gap.Upper != null && compareDbOrder(value, gap.Upper) >= 0) return false;
        return true;
    }
    (object? bound, int count) boundAndCount(T value, string aggregate, string op) {
        using var cmd = _store.CreateCommand("SELECT " + aggregate + "(value), COUNT(*) FROM " + _tableName + " WHERE value " + op + " @value");
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var count = _store.CastFromDb<int>(reader.GetValue(1));
        if (reader.IsDBNull(0)) return (null, count);
        var raw = reader.GetValue(0);
        if (raw is string s) return (s, count); // keep db representation, see comment above _gap
        return (_store.CastFromDb<T>(raw), count); // numeric types round-trip exactly
    }
    // must match how sqlite compares the stored values: TEXT columns use the default BINARY
    // collation, a memcmp of the UTF-8 bytes; the remaining supported types are stored
    // numerically and match Comparer<T>.Default
    int compareDbOrder(T value, object bound) {
        if (bound is string sb) {
            var sv = (string)_store.CastToDb(value)!;
            return Encoding.UTF8.GetBytes(sv).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(sb));
        }
        return comparer.Compare(value, (T)bound);
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
    public bool TryGetValue(int nodeId, out T value) {
        using var cmd = _store.CreateCommand("SELECT value FROM " + _tableName + " WHERE id = @id");
        cmd.Parameters.AddWithValue("@id", nodeId);
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) { value = default!; return false; }
        value = _store.CastFromDb<T>(result);
        return true;
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
        return _store.CastFromDb<int>(cmd.ExecuteScalar());
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
    public string FriendlyName { get; }
}

