using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.VectorIndex.ISV;

/// <summary>
/// Semantic index engine backed by the disk-based <see cref="NativeVectorIndex"/>, one index folder
/// per semantic index under <c>&lt;indexFolder&gt;/vectorindex</c>. The engine is only a factory and
/// folder owner: each index is driven by the data store through the memory-index protocol
/// (<see cref="IIndex.SaveStateForMemoryIndexes"/> and friends) and carries its own durable
/// position — timestamp and WAL file id — in its manifest, so there is no engine-level transaction
/// or WAL marker; see <see cref="ISemanticIndexEngine"/>.
/// </summary>
public class NativeVectorIndexEngine : ISemanticIndexEngine {
    readonly string _folderPath;
    readonly NativeVectorIndexOptions _defaults;
    readonly Dictionary<string, NativeVectorIndex> _indexes = [];
    public NativeVectorIndexEngine(string baseIndexFolderPath) : this(baseIndexFolderPath, null) { }
    public NativeVectorIndexEngine(string baseIndexFolderPath, NativeVectorIndexOptions? defaultOptions) {
        _folderPath = Path.Combine(baseIndexFolderPath, FileKeyUtility.IndexEngine_VectorIndexFolderKey);
        if (!Directory.Exists(_folderPath)) Directory.CreateDirectory(_folderPath);
        _defaults = defaultOptions ?? new();
    }
    public string Name => "Native Vector";
    public ISemanticIndex OpenSemanticIndex(SetRegister sets, string id, string friendlyName, AIEngine ai, Action<string>? log) {
        if (_indexes.TryGetValue(id, out var existing)) return existing; // idempotent re-open
        var folder = Path.Combine(_folderPath, FileKeyUtility.IndexEngine_VectorIndexIndexFolderKey(id));
        var index = new NativeVectorIndex(sets, id, friendlyName, folder, ai, cloneDefaults(), log);
        _indexes[id] = index;
        return index;
    }
    // every index gets its own copy so runtime tuning of one (cache budget, accuracy) stays local to it
    NativeVectorIndexOptions cloneDefaults() => new() {
        Dimensions = _defaults.Dimensions,
        MaxCacheBytes = _defaults.MaxCacheBytes,
        Accuracy = _defaults.Accuracy,
        ValidateNormalized = _defaults.ValidateNormalized,
        MemTableFlushThresholdBytes = _defaults.MemTableFlushThresholdBytes,
        MinVectorsForClustering = _defaults.MinVectorsForClustering,
        TargetVectorsPerCluster = _defaults.TargetVectorsPerCluster,
        MaxClusters = _defaults.MaxClusters,
        MaxSegments = _defaults.MaxSegments,
        KMeansIterations = _defaults.KMeansIterations,
        KMeansMaxSamples = _defaults.KMeansMaxSamples,
        RetrainGrowthFactor = _defaults.RetrainGrowthFactor,
    };
    /// <summary>Called by the data store right after every successful WAL flush; each index writes
    /// only its unflushed changes (or just advances its manifest stamp when clean), so the disk
    /// index follows the log at the same cadence as the other persisted index engines.</summary>
    public void MakeDurable(long logTimestamp) {
        foreach (var index in _indexes.Values) index.MakeDurable(logTimestamp);
    }
    public void ResetAll() {
        // every open index resets in place (no segments, timestamp 0, WAL binding kept); dropping
        // the unopened folders covers semantic indexes that have left the schema
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
