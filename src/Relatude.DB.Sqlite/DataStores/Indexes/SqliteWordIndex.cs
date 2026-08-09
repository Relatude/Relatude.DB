using System.Diagnostics;
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
        using var cmd = _store.CreateCommand("INSERT OR REPLACE INTO " + _tableName + " (rowid, value) VALUES (@id, @value)");
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@value", _store.CastToDb(value));
        cmd.ExecuteNonQuery();
        _stateId.RegisterAddition(id, value);
    }
    void remove(int id, string value) {
        value = IndexUtil.Clean(value, MinWordLength, MaxWordLength);
        // by rowid: an fts5 column is full-text indexed rather than key-indexed, so the node id has
        // to be the rowid for this to be a lookup instead of a scan of the entire table
        using var cmd = _store.CreateCommand("DELETE FROM " + _tableName + " WHERE rowid = @id");
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
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
            string sql = "SELECT rowid FROM " + _tableName + " WHERE value MATCH @value";
            using var cmd = _store.CreateCommand(sql);
            cmd.Parameters.AddWithValue("@value", getSearchValue(value, orSearch));
            cmd.Parameters.AddWithValue("@flag", 1);
            var sw = Stopwatch.StartNew();
            try {
                using var reader = cmd.ExecuteReader();
                var result = new List<int>();
                while (reader.Read()) result.Add(reader.GetInt32(0));
                // Console.WriteLine("Search '" + value + "' with " + result.Count + " hit(s) took " + sw.ElapsedMilliseconds + " ms");
                return result;
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
        using var cmd = _store.CreateCommand(sql);
        cmd.Parameters.AddWithValue("@query", getSearchValue(value, orSearch));
        cmd.Parameters.AddWithValue("@skip", pageSize * pageIndex);
        cmd.Parameters.AddWithValue("@take", pageSize);
        using var reader = cmd.ExecuteReader();

        var result = new List<KeyValuePair<int, double>>();
        while (reader.Read()) result.Add(new(reader.GetInt32(0), reader.GetDouble(1)));
        List<RawSearchHit> hits = [];
        foreach (var r in result) {
            hits.Add(new() { NodeId = r.Key, Score = (float)(r.Value / 100d) });
        }

        var sql2 = "SELECT COUNT(*) FROM " + _tableName + " WHERE value MATCH @query";
        using var cmd2 = _store.CreateCommand(sql2);
        cmd2.Parameters.AddWithValue("@query", getSearchValue(value, orSearch));
        totalHits = (int)(long)cmd2.ExecuteScalar()!;

        return hits;
    }
    public string FriendlyName { get; }
}
