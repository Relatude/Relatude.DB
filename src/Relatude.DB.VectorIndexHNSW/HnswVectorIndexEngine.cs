using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.VectorIndexHNSW;

/// <summary>
/// Semantic index engine backed by the disk-based <see cref="HnswVectorIndex"/>, one index folder per
/// semantic index under <c>&lt;indexFolder&gt;/vectorindex</c>. Same shape as the IVF engine: the
/// engine is only a factory and folder owner: each index is driven by the data store through the
/// memory-index protocol (<see cref="IIndex.SaveStateForMemoryIndexes"/> and friends) and carries its
/// own durable position — timestamp and WAL file id — in its manifest, so there is no engine-level
/// transaction or WAL marker; see <see cref="ISemanticIndexEngine"/>.
/// </summary>
public class HnswVectorIndexEngine : ISemanticIndexEngine {
    readonly string _folderPath;
    readonly HnswVectorIndexOptions _defaults;
    readonly Dictionary<string, HnswVectorIndex> _indexes = [];
    public HnswVectorIndexEngine(string baseIndexFolderPath) : this(baseIndexFolderPath, null) { }
    public HnswVectorIndexEngine(string baseIndexFolderPath, HnswVectorIndexOptions? defaultOptions) {
        _folderPath = Path.Combine(baseIndexFolderPath, FileKeyUtility.IndexEngine_VectorIndexFolderKey);
        if (!Directory.Exists(_folderPath)) Directory.CreateDirectory(_folderPath);
        _defaults = defaultOptions ?? new();
    }
    public string Name => "HNSW Vector";
    public ISemanticIndex OpenSemanticIndex(SetRegister sets, string id, string friendlyName, AIEngine ai, Action<string>? log) {
        if (_indexes.TryGetValue(id, out var existing)) return existing; // idempotent re-open
        var folder = Path.Combine(_folderPath, FileKeyUtility.IndexEngine_VectorIndexIndexFolderKey(id));
        var index = new HnswVectorIndex(sets, id, friendlyName, folder, ai, cloneDefaults(), log);
        _indexes[id] = index;
        return index;
    }
    // every index gets its own copy so runtime tuning of one (cache budget, accuracy) stays local to it
    HnswVectorIndexOptions cloneDefaults() => new() {
        Dimensions = _defaults.Dimensions,
        LowMemoryMode = _defaults.LowMemoryMode,
        MaxCacheBytes = _defaults.MaxCacheBytes,
        Accuracy = _defaults.Accuracy,
        ValidateNormalized = _defaults.ValidateNormalized,
        MemTableFlushThresholdBytes = _defaults.MemTableFlushThresholdBytes,
        MinVectorsForGraphSearch = _defaults.MinVectorsForGraphSearch,
        Connectivity = _defaults.Connectivity,
        ConnectivityLevel0 = _defaults.ConnectivityLevel0,
        EfConstruction = _defaults.EfConstruction,
        EfSearch = _defaults.EfSearch,
        MaxLevels = _defaults.MaxLevels,
        MaxRoutingCacheBytes = _defaults.MaxRoutingCacheBytes,
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
        var openFolders = _indexes.Keys.Select(FileKeyUtility.IndexEngine_VectorIndexIndexFolderKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
