// class to keep an index of doc field word counts, needed for BM25 and BM25F scoring

using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
internal class DocWordCounts {
    readonly ArrayDictionaryInt _counts = new();
    long _totalWordCount = 0; // exact running total, average is derived to avoid float drift and division by zero

    public double AverageWordCount => _counts.Count > 0 ? (double)_totalWordCount / _counts.Count : 0d;
    public int Get(int id) => _counts[id];
    public bool TryGet(int id, out int wordCount) => _counts.TryGetValue(id, out wordCount);
    internal void Add(int id, int wordCount) {
        _counts.Add(id, wordCount); // throws on duplicate id, before the total is touched
        _totalWordCount += wordCount;
    }
    internal void Remove(int id, int wordCount) {
        if (!_counts.ContainsKey(id)) {
#if DEBUG
                //throw new Exception("Trying to remove a non existing record. ");
#endif
            return; // ignore
        }
        if (_counts[id] != wordCount) throw new Exception("Deindex text has a different word count than the original indexed text. ");
        _counts.Remove(id);
        _totalWordCount -= wordCount;
    }
    internal void WriteState(IAppendStream stream) {
        _counts.WriteState(stream);
    }
    internal void ReadState(IReadStream stream) {
        _counts.ReadState(stream);
        _totalWordCount = _counts.SumValues(); // not part of the file format, must be rebuilt for BM25 to work after a load
    }
    internal double DocCount => _counts.Count;
}
