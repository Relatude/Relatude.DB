using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

public class WordIndexLuceneFactory : IPersistentWordIndexFactory {
    readonly string _luceneFolderPath;
    public WordIndexLuceneFactory(string baseIndexFolderPath) {
        _luceneFolderPath = Path.Combine(baseIndexFolderPath, "lucene");
        if (!System.IO.Directory.Exists(_luceneFolderPath)) System.IO.Directory.CreateDirectory(_luceneFolderPath);
    }
    public IPersistentWordIndex Create(SetRegister sets, IPersistedIndexStore index, string key, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        return new WordIndexLucene(sets, index, key, _luceneFolderPath, minWordLength, maxWordLength, prefixSearch, infixSearch);
    }
    public void DeleteAllFiles() {
        if (System.IO.Directory.Exists(_luceneFolderPath)) {
            System.IO.Directory.Delete(_luceneFolderPath, true);
        }
    }
}

public class WordIndexLucene : IPersistentWordIndex {
    static LuceneVersion _version = LuceneVersion.LUCENE_48;
    readonly string _indexId;
    readonly IPersistedIndexStore _store;
    readonly StateIdValueTracker<string> _stateId;
    readonly SetRegister _sets;
    readonly string _path;
    FSDirectory _directory = null!;
    StandardAnalyzer _analyzer = null!;
    IndexWriter _writer = null!;
    public int MinWordLength { get; }
    public int MaxWordLength { get; }
    public bool PrefixSearch { get; }
    public bool InfixSearch { get; }
    public WordIndexLucene(SetRegister sets, IPersistedIndexStore store, string indexId, string folderPath, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        _path = Path.Combine(folderPath, indexId.ToLower().Replace("wordindex", ""));
        if (!System.IO.Directory.Exists(_path)) System.IO.Directory.CreateDirectory(_path);
        _indexId = indexId;
        _store = store;
        _stateId = new(sets);
        _sets = sets;
        MinWordLength = minWordLength;
        MaxWordLength = maxWordLength;
        PrefixSearch = prefixSearch;
        InfixSearch = infixSearch;
        Open();
    }
    public string UniqueKey => _indexId;
    void add(int id, string value) {
        value = IndexUtil.Clean(value, MinWordLength, MaxWordLength);
        var doc = new Document {
            new StringField("id", id.ToString(), Field.Store.YES),
            new TextField("value", value, Field.Store.NO)
        };
        _writer.AddDocument(doc);
    }
    void remove(int id, string value) {
        var term = new Lucene.Net.Index.Term("id", id.ToString());
        _writer.DeleteDocuments(term);
    }
    public void Add(int id, object value) => add(id, (string)value);
    public void Remove(int id, object value) => remove(id, (string)value);
    public void RegisterAddDuringStateLoad(int id, object value, long timestampId) {
        if (timestampId > _store.Timestamp) add(id, (string)value);
    }
    public void RegisterRemoveDuringStateLoad(int id, object value, long timestampId) {
        if (timestampId > _store.Timestamp) remove(id, (string)value);
    }
    public void ClearCache() { }
    public void CompressMemory() { }
    public Task<JobResult> DequeueTasks() => Task.FromResult(new JobResult(0, 0, string.Empty));
    public int GetQueuedTaskCount() => 0;
    public void ReadState(IReadStream stream) { }
    public void SaveState(IAppendStream stream) { }
    public IdSet SearchForIdSetUnranked(TermSet value, bool orSearch) {
        if (value.Terms.Length == 0) return IdSet.Empty;
        return _sets.SearchForIdSetUnranked(_stateId.Current, value, orSearch, () => {
            var queryParser = new QueryParser(_version, "value", _analyzer);
            queryParser.DefaultOperator = orSearch ? Operator.OR : Operator.AND;
            queryParser.AllowLeadingWildcard = InfixSearch;
            var query = queryParser.Parse(value.ToString());
            using var reader = _writer.GetReader(applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var hits = searcher.Search(query, int.MaxValue).ScoreDocs;
            var ids = new HashSet<int>();
            foreach (var hit in hits) {
                var doc = searcher.Doc(hit.Doc);
                ids.Add(int.Parse(doc.Get("id")));
            }
            return ids;
        });
    }
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) {
        //return _store.SuggestSpelling(_indexId, query, boostCommonWords);
        throw new NotImplementedException("Spelling suggestions are not implemented yet.");
    }
    //public int MaxCount(string value, bool orSearch) {
    //    if (value.Length < 3) return int.MaxValue;
    //    var queryParser = new QueryParser(_version, "value", _analyzer);
    //    queryParser.DefaultOperator = orSearch ? Operator.OR : Operator.AND;
    //    queryParser.AllowLeadingWildcard = InfixSearch;
    //    var query = queryParser.Parse(value);
    //    using var reader = _writer.GetReader(applyAllDeletes: true);
    //    var searcher = new IndexSearcher(reader);
    //    var hitCount = searcher.Search(query, int.MaxValue).ScoreDocs.Length;
    //    if (hitCount > 999) return int.MaxValue;
    //    return hitCount;
    //}
    public void Commit() {
        _writer.Commit();
    }
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        if (value.Terms.Length == 0) {
            totalHits = 0;
            return [];
        }
        var queryParser = new QueryParser(_version, "value", _analyzer);
        queryParser.DefaultOperator = orSearch ? Operator.OR : Operator.AND;
        queryParser.AllowLeadingWildcard = InfixSearch;
        var query = queryParser.Parse(value.ToString());
        using var reader = _writer.GetReader(applyAllDeletes: true);
        var searcher = new IndexSearcher(reader);
        var top = maxHitsEvaluated;// pageSize * (pageIndex + 1);
        var hits = searcher.Search(query, top).ScoreDocs;
        List<RawSearchHit> result = [];
        foreach (var hit in hits.Skip(pageIndex * pageSize).Take(pageSize)) {
            var doc = searcher.Doc(hit.Doc);
            result.Add(new RawSearchHit() { NodeId = int.Parse(doc.Get("id")), Semantic = false, Score = hit.Score });
        }
        totalHits = hits.Length;
        return result;
    }
    public void Close() {
        _writer.Dispose();
        _analyzer.Dispose();
        _directory.Dispose();
    }
    public void Open() {
        _directory = FSDirectory.Open(_path);
        _analyzer = new StandardAnalyzer(_version);
        _writer = new IndexWriter(_directory, new(_version, _analyzer));
    }
    public void Dispose() {
        _writer.Dispose();
        _analyzer.Dispose();
        _directory.Dispose();
    }

    public void OptimizeAndMerge() {
        _writer.Commit();
        _writer.ForceMerge(1, true);
        Close();
        Open();
    }
}
