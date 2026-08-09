using System.Globalization;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// A Lucene-backed word index, owned by <see cref="LuceneTextIndexEngine"/>. Documents are written
/// through an <see cref="IndexWriter"/> and searched through its near-real-time reader, so writes
/// are visible immediately without committing.
///
/// <para>Unlike the SQLite/KV-backed indexes, this index does not commit atomically with an engine
/// transaction, so it must never borrow the engine's timestamp: its persisted position is stored in
/// the Lucene commit user data, written atomically with every commit and read back on open. A crash
/// loses only uncommitted documents, and the index then reports the timestamp of its last durable
/// commit, which makes the startup loader replay exactly the missing part of the WAL.</para>
///
/// <para>The commit data also records the WAL file id the documents belong to. On open, commit data
/// that is missing, from another WAL file, or from a version that did not stamp it (a legacy index)
/// cannot be trusted, so a non-empty index is then reset to empty — it reports timestamp 0 and the
/// replay rebuilds it.</para>
/// </summary>
public class WordIndexLucene : IWordIndex {
    static LuceneVersion _version = LuceneVersion.LUCENE_48;
    internal const string TimestampCommitKey = "relatude.timestamp";
    internal const string WalFileIdCommitKey = "relatude.walfileid";
    readonly string _indexId;
    readonly StateIdValueTracker<string> _stateId;
    readonly SetRegister _sets;
    readonly string _path;
    FSDirectory _directory = null!;
    StandardAnalyzer _analyzer = null!;
    IndexWriter _writer = null!;
    long _persistedTimestamp;
    Guid _persistedWalFileId;
    public int MinWordLength { get; }
    public int MaxWordLength { get; }
    public bool PrefixSearch { get; }
    public bool InfixSearch { get; }
    internal static string GetFolderName(string indexId) => indexId.ToLower().Replace("wordindex", "");
    internal WordIndexLucene(SetRegister sets, string indexId, string friendlyName, string folderPath, WordIndexOptions options, Guid engineWalFileId) {
        _path = Path.Combine(folderPath, GetFolderName(indexId));
        _indexId = indexId;
        _stateId = new();
        _sets = sets;
        MinWordLength = options.MinWordLength;
        MaxWordLength = options.MaxWordLength;
        PrefixSearch = options.PrefixSearch;
        InfixSearch = options.InfixSearch;
        FriendlyName = friendlyName;
        Open(engineWalFileId);
    }
    public string UniqueKey => _indexId;
    public string FriendlyName { get; }

    /// <summary>The timestamp of the last durable Lucene commit; 0 for a fresh (or reset) index,
    /// which makes the startup loader rebuild it from the whole WAL.</summary>
    public long PersistedTimestamp => _persistedTimestamp;
    // The first-commit protocol is for indexes that borrow their engine's timestamp; this index
    // carries its own, so there is nothing to flag.
    public void FlagFirstCommit() { }
    // Data lives in the Lucene directory, not in the memory-index state files.
    public void ReadStateForMemoryIndexes(Guid walFileId) { }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { }
    // After a log rewrite hot-swap the engine re-stamps every index in one call.
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { }

    void add(int id, string value) {
        value = IndexUtil.Clean(value, MinWordLength, MaxWordLength);
        var doc = new Document {
            new StringField("id", id.ToString(), Field.Store.YES),
            new TextField("value", value, Field.Store.NO)
        };
        _writer.AddDocument(doc);
        _stateId.RegisterAddition(id, value); // invalidates cached result sets in the SetRegister
    }
    void remove(int id, string value) {
        var term = new Lucene.Net.Index.Term("id", id.ToString());
        _writer.DeleteDocuments(term);
        _stateId.RegisterRemoval(id, IndexUtil.Clean(value, MinWordLength, MaxWordLength));
    }
    public void Add(int id, object value) => add(id, (string)value);
    public void Remove(int id, object value) => remove(id, (string)value);
    public void RegisterAddDuringStateLoad(int id, object value) => add(id, (string)value);
    public void RegisterRemoveDuringStateLoad(int id, object value) => remove(id, (string)value);
    public void ClearCache() { }
    public void CompressMemory() { }
    public IdSet SearchForIdSetUnranked(TermSet value, bool orSearch, int maxWordsEval) {
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
        throw new NotImplementedException("Spelling suggestions are not implemented yet.");
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
        var top = maxHitsEvaluated;
        var hits = searcher.Search(query, top).ScoreDocs;
        List<RawSearchHit> result = [];
        HashSet<int> seenIds = new();
        int duplicateCount = 0;
        foreach (var hit in hits.Skip(pageIndex * pageSize).Take(pageSize)) {
            var doc = searcher.Doc(hit.Doc);
            var id = int.Parse(doc.Get("id"));
            if (seenIds.Contains(id)) {
                duplicateCount++;
            } else {
                result.Add(new() { NodeId = id, Score = hit.Score });
                seenIds.Add(id);
            }
        }
        if (duplicateCount > 1) {
            Console.WriteLine("Warning: Duplicates found in Lucene index search results: " + duplicateCount);
        }
        totalHits = hits.Length - duplicateCount;
        return result;
    }

    /// <summary>
    /// Durably commits pending documents together with the index's position: the timestamp and WAL
    /// file id go into the Lucene commit user data, atomically with the data itself. Skips the
    /// commit when there is nothing new to record, and never regresses the persisted timestamp
    /// (during replay the engine may checkpoint at a position this index is already past).
    /// </summary>
    internal void Commit(long timestamp, Guid walFileId) {
        if (_writer.IsClosed) return;
        if (timestamp < _persistedTimestamp) return;
        if (!_writer.HasUncommittedChanges() && timestamp == _persistedTimestamp && walFileId == _persistedWalFileId) return;
        _writer.SetCommitData(new Dictionary<string, string> {
            [TimestampCommitKey] = timestamp.ToString(CultureInfo.InvariantCulture),
            [WalFileIdCommitKey] = walFileId.ToString("D"),
        });
        _writer.Commit();
        _persistedTimestamp = timestamp;
        _persistedWalFileId = walFileId;
    }

    internal void Close() => close();
    internal void Open(Guid engineWalFileId) {
        if (!System.IO.Directory.Exists(_path)) System.IO.Directory.CreateDirectory(_path);
        _directory = FSDirectory.Open(_path);
        _analyzer = new StandardAnalyzer(_version);
        try {
            _writer = new IndexWriter(_directory, new(_version, _analyzer));
        } catch (Exception err) {
            throw new Exception("Failed to open Lucene index writer for path: " + _path, err);
        }
        readPersistedState(engineWalFileId);
    }
    /// <summary>
    /// Reads the persisted position from the latest commit's user data. The position is only
    /// trusted when it carries a timestamp AND belongs to the engine's WAL file; otherwise a
    /// non-empty index holds data of unknown provenance (legacy index without commit data, crash
    /// between the engine's WAL re-binding steps, restored files) and is reset to empty so the
    /// replay rebuilds it from timestamp 0 instead of duplicating or resurrecting documents.
    /// </summary>
    void readPersistedState(Guid engineWalFileId) {
        long ts = 0;
        Guid wal = Guid.Empty;
        var commitData = _writer.CommitData;
        if (commitData != null) {
            if (commitData.TryGetValue(TimestampCommitKey, out var tsStr)) long.TryParse(tsStr, NumberStyles.None, CultureInfo.InvariantCulture, out ts);
            if (commitData.TryGetValue(WalFileIdCommitKey, out var walStr)) Guid.TryParse(walStr, out wal);
        }
        if (ts > 0 && wal != Guid.Empty && wal == engineWalFileId) {
            _persistedTimestamp = ts;
            _persistedWalFileId = wal;
        } else if (_writer.MaxDoc > 0 || ts > 0) {
            resetFiles();
        } else {
            _persistedTimestamp = 0;
            _persistedWalFileId = wal == engineWalFileId ? wal : Guid.Empty;
        }
    }
    void resetFiles() {
        _writer.Rollback(); // discards uncommitted changes and closes the writer
        _analyzer.Dispose();
        _directory.Dispose();
        if (System.IO.Directory.Exists(_path)) System.IO.Directory.Delete(_path, true);
        System.IO.Directory.CreateDirectory(_path);
        _directory = FSDirectory.Open(_path);
        _analyzer = new StandardAnalyzer(_version);
        _writer = new IndexWriter(_directory, new(_version, _analyzer));
        _persistedTimestamp = 0;
        _persistedWalFileId = Guid.Empty;
    }
    /// <summary>Wipes the index back to empty (timestamp 0). Used by the engine's reset paths.</summary>
    internal void ResetToEmpty() => resetFiles();
    public void Dispose() => close();
    void close() {
        if (!_writer.IsClosed) {
            // Uncommitted documents are not covered by the persisted-timestamp bookkeeping: a plain
            // Dispose would commit them under the LAST commit's user data, making the index content
            // newer than its claimed position and the replay would then duplicate them. Discard
            // instead — the WAL replay rebuilds them on the next open.
            if (_writer.HasUncommittedChanges()) _writer.Rollback();
            else _writer.Dispose();
        }
        _analyzer.Dispose();
        _directory.Dispose();
    }
    internal void OptimizeAndMerge(long timestamp, Guid walFileId) {
        Commit(timestamp, walFileId);
        _writer.ForceMerge(1, true);
        if (_writer.HasUncommittedChanges()) _writer.Commit(); // persists the merge; the commit user data carries over
        close();
        Open(walFileId);
    }
}
