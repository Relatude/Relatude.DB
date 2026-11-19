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
    int count(string sql, params (string Name, object? Value)[] parameters) => _store.CastFromDb<int>(_store.Scalar(sql, parameters));
    List<int> ids(string sql, params (string Name, object? Value)[] parameters) => _store.Rows(sql, r => r.GetInt32(0), parameters);
    int _idCount = -1;
    public int IdCount {
        get {
            if (_idCount == -1) _idCount = count("SELECT COUNT(*) FROM " + _tableName);
            return _idCount;
        }
    }
    void add(int id, T value) {
        _store.Execute("INSERT INTO " + _tableName + " (id, value) VALUES (@id, @value)", ("@id", id), ("@value", _store.CastToDb(value)));
        _stateId.RegisterAddition(id, value);
        if (_idCount != -1) _idCount++;
    }
    void remove(int id, T value) {
        _store.Execute("DELETE FROM " + _tableName + " WHERE id = @id", ("@id", id));
        _stateId.RegisterRemoval(id, value);
        if (_idCount != -1) _idCount--;
    }
    public void Add(int id, object value) => add(id, (T)value);
    public void Remove(int id, object value) => remove(id, (T)value);
    public void Add(int id, T value) => add(id, value);
    public void Remove(int id, T value) => remove(id, value);

    public void RegisterAddDuringStateLoad(int nodeId, object value) => add(nodeId, (T)value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => remove(nodeId, (T)value);
    public IEnumerable<int> Ids => ids("SELECT id FROM " + _tableName);
    public long StateId => _stateId.Current;
    public IEnumerable<T> UniqueValues => _store.Rows("SELECT DISTINCT value FROM " + _tableName, r => _store.CastFromDb<T>(r.GetValue(0)));
    public int ValueCount => count("SELECT COUNT(DISTINCT value) FROM " + _tableName);
    public string UniqueKey => _indexId;
    public void ClearCache() { _keyBuilder.Clear(); _idCount = -1; }
    public void CompressMemory() { }
    public bool ContainsValue(T value) => countEqual(value) > 0;
    public int CountEqual(IdSet nodeIds, T value) => _sets.CountEqual(this, nodeIds, value);
    public int CountGreaterThan(T value, bool inclusive)
        => count("SELECT COUNT(*) FROM " + _tableName + " WHERE value " + (inclusive ? ">=" : ">") + " @value", ("@value", _store.CastToDb(value)));
    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) => _sets.CountInRange(this, nodeIds, from, to, fromInclusive, toInclusive);
    public int CountLessThan(T value, bool inclusive)
        => count("SELECT COUNT(*) FROM " + _tableName + " WHERE value " + (inclusive ? "<=" : "<") + " @value", ("@value", _store.CastToDb(value)));
    public void Dispose() { }

    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) => _sets.Filter(this, nodeIds, op, v);
    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues) => _sets.FilterInValues(this, nodeIds, selectedValues);
    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges) => _sets.FilterRanges(this, nodeIds, selectedRanges);
    public IdSet FilterRangesObject(IdSet set, object from, object to) => _sets.FilterRangesObject(this, set, from, to);
    public IdSet ReOrder(IdSet unsorted, bool descending) => _sets.OrderBy(this, unsorted, descending);

    // Cache-key logic lives in the shared GapCacheKeyBuilder; the ordering-sensitive pieces are
    // provided through IGapSource below. See GapCacheKeyBuilder for the full explanation.
    public object[] GetCacheKey(T queryValue, QueryType queryType) => _keyBuilder.GetCacheKey(queryValue, queryType);

    // Bounds are kept as their raw db representation (string for TEXT columns, else T) rather than
    // parsed back to T, so that the comparison below is guaranteed to see exactly the bytes the db
    // ordered by, without depending on the encoding round-trip being canonical.
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
        return _store.Rows<(object?, int)>("SELECT " + aggregate + "(value), COUNT(*) FROM " + _tableName + " WHERE value " + op + " @value", reader => {
            var count = _store.CastFromDb<int>(reader.GetValue(1));
            if (reader.IsDBNull(0)) return (null, count);
            var raw = reader.GetValue(0);
            if (raw is string s) return (s, count); // keep db representation, see comment above _gap
            return (_store.CastFromDb<T>(raw), count); // numeric types round-trip exactly
        }, ("@value", _store.CastToDb(value)))[0];
    }
    // must match how sqlite compares the stored values: TEXT columns use the default BINARY
    // collation, a memcmp of the UTF-8 bytes; the remaining supported types are stored
    // numerically and match Comparer<T>.Default
    int compareDbOrder(T value, object bound) {
        if (bound is string sb) return compareText((string)_store.CastToDb(value)!, sb);
        return comparer.Compare(value, (T)bound);
    }
    // same comparison, for two values that are both still T (e.g. an index MIN/MAX). Comparer<T>
    // is not usable on its own here: Comparer<string> is culture-sensitive, so it puts "B" before
    // "a" while sqlite's BINARY collation puts it after, and a MIN/MAX bound compared the wrong
    // way makes MaxCount report that a value which is in the index cannot be there.
    int compareDbOrder(T value, T other) {
        var db = _store.CastToDb(value);
        if (db is string sv) return compareText(sv, (string)_store.CastToDb(other)!);
        return comparer.Compare(value, other);
    }
    static int compareText(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
    public ICollection<int> GetIds(T value) => ids("SELECT id FROM " + _tableName + " WHERE value = @value", ("@value", _store.CastToDb(value)));

    public T GetValue(int nodeId) => _store.CastFromDb<T>(_store.Scalar("SELECT value FROM " + _tableName + " WHERE id = @id", ("@id", nodeId)));
    public bool HasFastPointLookup => false;
    public bool TryGetValue(int nodeId, out T value) {
        var result = _store.Scalar("SELECT value FROM " + _tableName + " WHERE id = @id", ("@id", nodeId));
        if (result == null || result is DBNull) { value = default!; return false; }
        value = _store.CastFromDb<T>(result);
        return true;
    }
    public IEnumerable<int> GreaterThan(T value, bool inclusive)
        => ids("SELECT id FROM " + _tableName + " WHERE value " + (inclusive ? ">=" : ">") + " @value", ("@value", _store.CastToDb(value)));
    public IEnumerable<int> LessThan(T value, bool inclusive)
        => ids("SELECT id FROM " + _tableName + " WHERE value " + (inclusive ? "<=" : "<") + " @value", ("@value", _store.CastToDb(value)));
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive)
        => count("SELECT COUNT(*) FROM " + _tableName + " WHERE id IN (" + ids.AsStringList() + ") AND value " + (fromInclusive ? ">=" : ">") + " @from AND value " + (toInclusive ? "<=" : "<") + " @to",
            ("@from", _store.CastToDb(from)), ("@to", _store.CastToDb(to)));

    static readonly Comparer<T> comparer = Comparer<T>.Default;
    public int MaxCount(IndexOperator op, T value) {
        // optimized for fastest speed, not accuracy, important for performance of query engine
        if (IdCount == 0) return 0;
        if (compareDbOrder(value, MaxValue()!) > 0) return 0; // value is larger than max value in index
        if (compareDbOrder(value, MinValue()!) < 0) return 0; // value is smaller than min value in index
        return op switch {
            IndexOperator.Equal => countEqual(value),
            IndexOperator.NotEqual => ValueCount - countEqual(value),
            _ => IdCount,
        };
    }
    int countEqual(T value) => count("SELECT COUNT(*) FROM " + _tableName + " WHERE value = @value", ("@value", _store.CastToDb(value)));
    public T? MaxValue() => _store.CastFromDb<T>(_store.Scalar("SELECT MAX(value) FROM " + _tableName));
    public T? MinValue() => _store.CastFromDb<T>(_store.Scalar("SELECT MIN(value) FROM " + _tableName));
    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive)
        => ids("SELECT id FROM " + _tableName + " WHERE value " + (fromInclusive ? ">=" : ">") + " @from AND value " + (toInclusive ? "<=" : "<") + " @to",
            ("@from", _store.CastToDb(from)), ("@to", _store.CastToDb(to)));
    public IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive) {
        throw new NotImplementedException();
    }
    public string FriendlyName { get; }
}
