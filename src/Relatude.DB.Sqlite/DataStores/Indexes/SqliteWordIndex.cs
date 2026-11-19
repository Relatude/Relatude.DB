using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

public class SqliteWordIndex : PersistedIndexBase, IWordIndex {
    readonly string _indexId;
    readonly SqliteIndexStore _store;
    readonly StateIdValueTracker<string> _stateId;
    readonly SetRegister _sets;
    readonly string _tableName;
    public int MaxWordLength { get; }
    public int MinWordLength { get; }
    public bool PrefixSearch { get; }
    public bool InfixSearch { get; }
    public SqliteWordIndex(SetRegister sets, SqliteIndexStore store, string indexId, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch, bool justCreated)
        : base(store, justCreated) {
        _indexId = indexId;
        _store = store;
        _stateId = new();
        _sets = sets;
        _tableName = store.GetTableName(indexId);
        FriendlyName = friendlyName;
        MaxWordLength = maxWordLength;
        MinWordLength = minWordLength;
        PrefixSearch = prefixSearch;
        InfixSearch = infixSearch;
    }
    public string UniqueKey => _indexId;
    void add(int id, string value) {
        value = IndexUtil.Clean(value, MinWordLength, MaxWordLength);
        // OR REPLACE, so re-delivering an add the table already holds (a WAL replay after a crash)
        // updates the row instead of failing on the rowid's uniqueness. One node has one text per
        // word index, so replacing is what the contract means.
        _store.Execute("INSERT OR REPLACE INTO " + _tableName + " (rowid, value) VALUES (@id, @value)", ("@id", id), ("@value", _store.CastToDb(value)));
        _stateId.RegisterAddition(id, value);
    }
    void remove(int id, string value) {
        value = IndexUtil.Clean(value, MinWordLength, MaxWordLength);
        // by rowid: an fts5 column is full-text indexed rather than key-indexed, so the node id has
        // to be the rowid for this to be a lookup instead of a scan of the entire table
        _store.Execute("DELETE FROM " + _tableName + " WHERE rowid = @id", ("@id", id));
        _stateId.RegisterRemoval(id, value);
    }
    public void Add(int id, object value) => add(id, (string)value);
    public void Remove(int id, object value) => remove(id, (string)value);
    public void RegisterAddDuringStateLoad(int id, object value) => add(id, (string)value);
    public void RegisterRemoveDuringStateLoad(int id, object value) => remove(id, (string)value);
    public void ClearCache() { }
    public void CompressMemory() { }
    public void Dispose() { }
    public IdSet SearchForIdSetUnranked(TermSet value, bool orSearch, int maxWordsEval) {
        if (value.Terms.Length == 0) return IdSet.Empty;
        return _sets.SearchForIdSetUnranked(_stateId.Current, value, orSearch, () => {
            try {
                return _store.Rows("SELECT rowid FROM " + _tableName + " WHERE value MATCH @value", r => r.GetInt32(0), ("@value", getSearchValue(value, orSearch)));
            } catch {
                return [];
            }
        });
    }
    string getSearchValue(TermSet query, bool orSearch) {
        var searchValue = string.Empty;
        foreach (var expression in query.Terms) {
            var ids = new HashSet<int>();
            var word = expression.Word;
            if (expression.Infix) word = "*" + word;
            if (expression.Prefix) word += "*";
            // if (expression.Fuzzy) word += "~"; // FUZZY NOT SUPPORTED WITH SQLITE?
            if (searchValue.Length > 0) {
                searchValue += orSearch ? " OR " : " AND ";
                searchValue += word;
            } else {
                searchValue = word;
            }
        }
        return searchValue;
    }
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) {
        throw new NotImplementedException("Spelling suggestions are not implemented yet.");
    }
    //public int MaxCount(string value, bool orSearch) {
    //    using var cmd = _store.CreateCommand("SELECT COUNT(id) FROM " + _tableName + " WHERE value MATCH @value");
    //    cmd.Parameters.AddWithValue("@value", value);
    //    return _store.CastFromDb<int>(cmd.ExecuteScalar());
    //}
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        if (value.Terms.Length == 0) {
            totalHits = 0;
            return [];
        }
        var sql = "SELECT rowid, rank FROM " + _tableName;
        sql += " WHERE value MATCH @query ORDER BY rank LIMIT @skip, @take";
        var query = getSearchValue(value, orSearch);
        var hits = _store.Rows(sql, r => new RawSearchHit { NodeId = r.GetInt32(0), Score = (float)(r.GetDouble(1) / 100d) },
            ("@query", query), ("@skip", pageSize * pageIndex), ("@take", pageSize));
        totalHits = (int)(long)_store.Scalar("SELECT COUNT(*) FROM " + _tableName + " WHERE value MATCH @query", ("@query", query))!;

        return hits;
    }
    public string FriendlyName { get; }
}
