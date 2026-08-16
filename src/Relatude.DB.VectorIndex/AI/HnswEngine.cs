using Relatude.DB.AI.HNSW;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.AI;

/// <summary>
/// Semantic index engine backed by <see cref="VectorIndex"/>, one index folder per semantic
/// index under <c>&lt;indexFolder&gt;/vectorindex-hnsw</c>. Same shape as the other vector index
/// engines: the engine is only a factory and folder owner — each index is driven by the data store
/// through the memory-index protocol (<see cref="IIndex.SaveStateForMemoryIndexes"/> and friends)
/// and carries its own durable position — timestamp and WAL file id — in its manifest, so there is
/// no engine-level transaction or WAL marker; see <see cref="ISemanticIndexEngine"/>.
/// </summary>
public class HnswEngine : ISemanticIndexEngine {
    // deliberately its own folder name: the first HNSW engine's files live under "vectorindex", and
    // the two write incompatible formats, so sharing the folder would just trade resets back and forth
    const string folderKey = "vectorindex-hnsw";
    readonly string _folderPath;
    readonly VectorIndexOptions _defaults;
    readonly Dictionary<string, VectorIndex> _indexes = [];
    public HnswEngine(string baseIndexFolderPath) : this(baseIndexFolderPath, null) { }
    public HnswEngine(string baseIndexFolderPath, VectorIndexOptions? defaultOptions) {
        _folderPath = Path.Combine(baseIndexFolderPath, folderKey);
        if (!Directory.Exists(_folderPath)) Directory.CreateDirectory(_folderPath);
        _defaults = defaultOptions ?? new();
    }
    public string Name => "HNSW Vector";
    public ISemanticIndex OpenSemanticIndex(SetRegister sets, string id, string friendlyName, AIEngine ai, Action<string>? log) {
        if (_indexes.TryGetValue(id, out var existing)) return existing; // idempotent re-open
        var folder = Path.Combine(_folderPath, id.ToLowerInvariant());
        var index = new VectorIndex(sets, id, friendlyName, folder, ai, cloneDefaults(), log);
        _indexes[id] = index;
        return index;
    }
    // every index gets its own copy so runtime tuning of one (memory budget, accuracy) stays local to it
    VectorIndexOptions cloneDefaults() => new() {
        Dimensions = _defaults.Dimensions,
        MaxMemoryBytes = _defaults.MaxMemoryBytes,
        MaxThreads = _defaults.MaxThreads,
        Accuracy = _defaults.Accuracy,
        ValidateNormalized = _defaults.ValidateNormalized,
        MemTableFlushThresholdBytes = _defaults.MemTableFlushThresholdBytes,
        MinVectorsForGraphSearch = _defaults.MinVectorsForGraphSearch,
        Connectivity = _defaults.Connectivity,
        ConnectivityLevel0 = _defaults.ConnectivityLevel0,
        EfConstruction = _defaults.EfConstruction,
        EfSearch = _defaults.EfSearch,
        MaxLevels = _defaults.MaxLevels,
        CompactionDeadFraction = _defaults.CompactionDeadFraction,
        CompactionMinDeadRecords = _defaults.CompactionMinDeadRecords,
        RandomSeed = _defaults.RandomSeed,
    };
    /// <summary>Called by the data store right after every successful WAL flush; each index writes
    /// only the records its graph changed (or just advances its manifest stamp when clean), so the
    /// disk index follows the log at the same cadence as the other persisted index engines.</summary>
    public void MakeDurable(long logTimestamp) {
        foreach (var index in _indexes.Values) index.MakeDurable(logTimestamp);
    }
    public void ResetAll() {
        // every open index resets in place (no graph, timestamp 0, WAL binding kept); dropping the
        // unopened folders covers semantic indexes that have left the schema
        foreach (var index in _indexes.Values) index.ResetToEmpty();
        DeleteUnopenedIndexes();
    }
    public void DeleteUnopenedIndexes() {
        // drops the folders of semantic indexes that have left the schema, so a later re-add starts
        // with a fresh, empty index (timestamp 0, forcing a rebuild) instead of stale data
        var openFolders = _indexes.Keys.Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(_folderPath)) {
            if (openFolders.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, true); } catch { } // a locked folder is skipped, not fatal
        }
    }
    public long GetTotalDiskSpace() {
        if (!Directory.Exists(_folderPath)) return 0;
        return Directory.GetFiles(_folderPath, "*", SearchOption.AllDirectories).Sum(f => {
            try { return new FileInfo(f).Length; } catch { return 0L; }
        });
    }
    public void Dispose() {
        foreach (var index in _indexes.Values) {
            try { index.Dispose(); } catch { }
        }
        _indexes.Clear();
    }
}
