using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.Trie;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace TextIndexBenchmarks.Engines;

/// <summary>
/// The built-in in-memory index (<see cref="WordIndexTrie"/>) — the reference implementation, and
/// what a store with a memory text index (<c>DefaultTextIndex = Guid.Empty</c>) runs. It has no transaction
/// protocol of its own: writes are immediately visible, and durability is a full state file written
/// by <see cref="IIndex.SaveStateForMemoryIndexes"/>, which the store schedules periodically rather
/// than per transaction.
/// </summary>
public sealed class TrieBenchIndex : IBenchWordIndex {
    readonly WordIndexTrie _trie;
    readonly IIOProvider _io;
readonly string _dir;
    readonly Guid _walId;
    public TrieBenchIndex(string dir, Guid walId, int minWordLength, int maxWordLength, bool prefix, bool infix, bool reopen) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _walId = walId;
        _io = new IOProviderDisk(dir);
_trie = new WordIndexTrie(new SetRegister(0), "bench", "bench", _io, minWordLength, maxWordLength, prefix, infix);
        // the store applies the same wrapper to memory indexes (see IndexFactory)
        Index = new OptimizedWordIndex(_trie);
        if (reopen) _trie.ReadStateForMemoryIndexes(walId);
    }
    public IWordIndex Index { get; }
    public Features Supported => Features.Fuzzy | Features.Infix | Features.Suggest;
    public void Begin() { }
    public void Commit(long timestamp) { }
    public void Persist(long timestamp) => Index.SaveStateForMemoryIndexes(timestamp, _walId);
    public long DiskBytes {
        get {
            if (!Directory.Exists(_dir)) return 0;
            return Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Sum(f => {
                try { return new FileInfo(f).Length; } catch { return 0L; }
            });
        }
    }
    public void Dispose() {
        Index.Dispose();
        _io.CloseAllOpenStreams();
    }
}
