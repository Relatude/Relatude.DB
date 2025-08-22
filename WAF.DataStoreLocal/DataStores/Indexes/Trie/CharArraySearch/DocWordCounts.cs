// class to keep an index of doc field word counts, needed for BM25 and BM25F scoring

using WAF.IO;

namespace WAF.DataStores.Indexes.Trie.CharArraySearch;
internal class DocWordCounts {
    //readonly Dictionary<int,int> _counts = new();
    readonly ArrayDictionaryInt _counts = new();
    double _averageWordCount = 0;

    public double AverageWordCount => _averageWordCount;
    public int Get(int id) => _counts[id];
    internal void Add(int id, int wordCount) {
        _averageWordCount = (DocCount * _averageWordCount + wordCount) / (DocCount + 1);
        _counts.Add(id, wordCount);
    }
    internal void Remove(int id, int wordCount) {
        //if (!_counts.ContainsKey(id)) {
        if (!_counts.ContainsKey(id)) {
#if DEBUG
                throw new Exception("Trying to remove a non existing record. ");
#else
            return; // ignore
#endif
        }
        if (_counts[id] != wordCount) throw new Exception("Deindex text has a different word count than the original indexed text. ");

        if (DocCount > 0) {
            _averageWordCount = (DocCount * _averageWordCount - wordCount) / (DocCount - 1);
        } else {
            _averageWordCount = 0;
        }
        _counts.Remove(id);
    }
    internal void WriteState(IAppendStream stream) => _counts.WriteState(stream);
    internal void ReadState(IReadStream stream) => _counts.ReadState(stream);
    //internal void WriteState(IAppendStream stream) => throw new NotImplementedException("DocWordCounts.WriteState not implemented");
    //internal void ReadState(IReadStream stream) => throw new NotImplementedException("DocWordCounts.ReadState not implemented");
    internal double DocCount => _counts.Count;
}
