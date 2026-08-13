using Relatude.DB.VectorIndex;
using Relatude.DB.VectorIndexHNSW;
using VectorIndexBenchmarks.Engines;

namespace VectorIndexBenchmarks.Harness;

/// <summary>The vector index implementations under test, and how each one is opened.</summary>
public static class Engines {
    public const string Memory = "memory";
    public const string Native = "native";
    /// <summary>The disk index probing every cluster: an exact search, directly comparable with the
    /// in-memory index's accuracy, so the price of exactness on disk is visible.</summary>
    public const string NativeExact = "native-exact";
    /// <summary>The same disk index on a deliberately tiny block-cache budget — a second
    /// configuration of one implementation rather than another implementation, but naming it in
    /// --engines shows what the cache budget actually buys.</summary>
    public const string NativeLowMem = "native-lowmem";
    /// <summary>The disk-based HNSW index: the same storage design as <see cref="Native"/>, walking a
    /// proximity graph instead of probing the nearest clusters.</summary>
    public const string Hnsw = "hnsw";
    /// <summary>The HNSW index in <c>LowMemoryMode</c>: the graph stays on disk and is read through
    /// small caches, which is the configuration that shows what a graph walk really costs when it
    /// has to go to the disk for every hop — and what that buys back in resident memory.</summary>
    public const string HnswLowMem = "hnsw-lowmem";
    /// <summary>sqlite-vec: a vec0 virtual table in an ordinary SQLite file, exact by design.</summary>
    public const string SqliteVec = "sqlitevec";
    /// <summary>USearch: an HNSW graph in native memory — the other main answer to approximate search.</summary>
    public const string USearch = "usearch";
    public const long LowMemCacheBytes = 8L * 1024 * 1024; 

    //public static readonly string[] All = [Memory, Native, Hnsw, HnswLowMem, SqliteVec, USearch];
    // Both HNSW configurations by default: they are one implementation run two ways, and the pair is
    // the only place the table shows what the memory budget actually costs and buys. Reading them
    // next to each other is the point — the same graph, once with room to cache it and once without.
    public static readonly string[] All = [Memory, Native, Hnsw, HnswLowMem, USearch];

    /// <summary>A fixed log id: an index binds its data to the WAL file it belongs to, and the
    /// benchmark's reopen step has to present the same one or the index resets itself.</summary>
    public static readonly Guid WalFileId = new("7665637f-6f72-6265-6e63-686d61726b00");

    public static string DisplayName(string name) => name switch {
        Memory => "MemorySemanticIndex",
        Native => "NativeVectorIndex",
        NativeExact => "NativeVectorIndex (exact)",
        NativeLowMem => $"NativeVectorIndex ({LowMemCacheBytes / 1024 / 1024} MB cache)",
        Hnsw => "HnswVectorIndex",
        HnswLowMem => "HnswVectorIndex (low memory)",
        SqliteVec => "sqlite-vec",
        USearch => "USearch (HNSW)",
        _ => name,
    };

    /// <summary>Long-form description printed under the tables.</summary>
    public static string Description(string name) => name switch {
        Memory => "all vectors on the managed heap, every search an exact SIMD scan (Relatude.DB.DataStoreLocal)",
        Native => "disk segments, IVF clusters, cached blocks; searches the nearest clusters only (Relatude.DB.VectorIndex)",
        NativeExact => "the same disk index probing every cluster (accuracy 1): exact, and reading every block",
        NativeLowMem => $"the same disk index with its block cache budget set to {LowMemCacheBytes / 1024 / 1024} MB",
        Hnsw => "disk records, HNSW graph, upper layers in memory; walks to the query (Relatude.DB.VectorIndexHNSW)",
        HnswLowMem => "the same graph index in LowMemoryMode: the graph stays on disk, read through small caches",
        SqliteVec => "third party: a vec0 virtual table in a SQLite file, exact KNN by full scan (asg017/sqlite-vec)",
        USearch => "third party: an HNSW graph in native memory, top-k only (unum-cloud/USearch)",
        _ => name,
    };

    /// <summary>True when the implementation answers exactly, so its recall must come out at 100%.
    /// sqlite-vec has no approximate index at all; the Relatude disk index is exact only when it
    /// probes every cluster.</summary>
    public static bool IsExact(string name, BenchOptions options) => name switch {
        Memory or NativeExact or SqliteVec => true,
        Native or NativeLowMem => options.Accuracy >= 1f,
        _ => false,
    };

    public static IBenchVectorIndex Create(string name, string dir, BenchOptions options, Corpus corpus) {
        Directory.CreateDirectory(dir);
        return name switch {
            Memory => new MemoryBenchIndex(dir, WalFileId, BenchAiEngine.Create(corpus)),
            Native or NativeExact or NativeLowMem => new NativeBenchIndex(dir, WalFileId, BenchAiEngine.Create(corpus), new NativeVectorIndexOptions {
                Dimensions = corpus.Dimensions,
                Accuracy = name == NativeExact ? 1f : options.Accuracy,
                MaxCacheBytes = name == NativeLowMem ? LowMemCacheBytes : options.CacheBytes,
                // the corpus is normalized by construction; the per-add check is a measurable cost
                // that says nothing about the index, so it is off for every configuration
                ValidateNormalized = false,
            }),
            // the graph index takes the HNSW dials rather than --accuracy, so it and USearch are
            // configured identically and the algorithm is the only thing that differs between them
            Hnsw or HnswLowMem => new HnswBenchIndex(dir, WalFileId, BenchAiEngine.Create(corpus), new HnswVectorIndexOptions {
                Dimensions = corpus.Dimensions,
                LowMemoryMode = name == HnswLowMem, // budgets left null: the mode's own small defaults
                MaxCacheBytes = name == HnswLowMem ? null : options.CacheBytes,
                Connectivity = (int)options.HnswConnectivity,
                EfConstruction = (int)options.HnswExpansionAdd,
                EfSearch = (int)options.HnswExpansionSearch,
                ValidateNormalized = false,
            }),
            SqliteVec => new SqliteVecBenchIndex(dir, corpus.Dimensions, options.CacheBytes),
            USearch => new USearchBenchIndex(dir, corpus.Dimensions, options.HnswConnectivity, options.HnswExpansionAdd, options.HnswExpansionSearch),
            _ => throw new ArgumentException($"Unknown engine '{name}'."),
        };
    }

    public static long FolderBytes(string dir) {
        if (!Directory.Exists(dir)) return 0;
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => {
            try { return new FileInfo(f).Length; } catch { return 0L; }
        });
    }
}

public sealed class BenchOptions {
    public int N = 100000;
    /// <summary>Vector length. 384 is a small sentence-transformer model, 1536 an OpenAI
    /// text-embedding-3-small; both are worth running.</summary>
    public int Dimensions = 1536;
    /// <summary>Cluster centers the vectors are drawn around; 0 gives uniformly random directions,
    /// the worst case for any clustering index.</summary>
    public int Clusters = 200;
    /// <summary>How far vectors scatter from their center, as a ratio of the center's own length.
    /// Higher is a looser cluster: 1.0 puts a vector at about 0.87 cosine similarity to its center.</summary>
    public float ClusterNoise = 1.0f;
    /// <summary>Vectors per state save during the index, update and remove phases.</summary>
    public int BatchSize = 5_000;
    /// <summary>Byte budget of the disk index's block cache, and of sqlite-vec's page cache. Both
    /// are limits rather than reservations, so a very large value means "do not evict".</summary>
    public long CacheBytes = 256L * 1024 * 1024 * 100;
    /// <summary>Fraction of clusters the disk index probes per search.</summary>
    public float Accuracy = 0.25f;
    /// <summary>HNSW graph degree (USearch's <c>connectivity</c>). 0 leaves the library default.</summary>
    public ulong HnswConnectivity = 16;
    /// <summary>HNSW build effort (USearch's <c>expansionAdd</c>).</summary>
    public ulong HnswExpansionAdd = 128;
    /// <summary>HNSW search effort (USearch's <c>expansionSearch</c>) — the closest analogue to the
    /// disk index's <see cref="Accuracy"/>, and the dial to turn when comparing recall.</summary>
    public ulong HnswExpansionSearch = 64;
    /// <summary>The similarity floor the search phases pass down; derived from the exact answers
    /// when not given, so about <see cref="Corpus.FilterRank"/> vectors clear it.</summary>
    public float? MinSimilarity;
    /// <summary>Save state after every batch (the cadence of a store that checkpoints often)
    /// instead of once at the end of the load.</summary>
    public bool PersistEveryBatch;
    public string[] EngineNames = Engines.All;
    public string DataDir = Path.GetTempPath();
    public bool InProcess;
    public string? ChildEngine, ChildDir;

    /// <summary>Vectors the update and remove phases touch, and writes the mixed phase makes —
    /// fixed, not a share of the corpus, so a slow engine cannot decide the suite's runtime.</summary>
    public int UpdateCount => Math.Min(BenchRunner.WritePhaseOps, N);
    public int RemoveCount => Math.Min(BenchRunner.WritePhaseOps, N);
    public int MixedCount => BenchRunner.MixedPhaseWrites;
    /// <summary>The small delta indexed before the durability checkpoint is timed.</summary>
    public int DeltaCount => Math.Clamp(N / 100, 100, 1_000);

    public static BenchOptions Parse(string[] args) {
        var o = new BenchOptions();
        foreach (var a in args) {
            var kv = a.Split('=', 2);
            switch (kv[0]) {
                case "--n": o.N = int.Parse(kv[1]); break;
                case "--dims": o.Dimensions = int.Parse(kv[1]); break;
                case "--clusters": o.Clusters = int.Parse(kv[1]); break;
                case "--noise": o.ClusterNoise = float.Parse(kv[1]); break;
                case "--batch": o.BatchSize = int.Parse(kv[1]); break;
                case "--cache": o.CacheBytes = long.Parse(kv[1]) * 1024 * 1024; break;
                case "--accuracy": o.Accuracy = float.Parse(kv[1]); break;
                case "--hnsw-m": o.HnswConnectivity = ulong.Parse(kv[1]); break;
                case "--hnsw-ef-add": o.HnswExpansionAdd = ulong.Parse(kv[1]); break;
                case "--hnsw-ef": o.HnswExpansionSearch = ulong.Parse(kv[1]); break;
                case "--min-sim": o.MinSimilarity = float.Parse(kv[1]); break;
                case "--persist": o.PersistEveryBatch = kv[1] == "batch"; break;
                case "--engines": o.EngineNames = kv[1] == "all" ? Engines.All : kv[1].Split(','); break;
                case "--data": o.DataDir = kv[1]; break;
                case "--in-process": o.InProcess = true; break;
                case "--child-engine": o.ChildEngine = kv[1]; break;
                case "--child-dir": o.ChildDir = kv[1]; break;
                default: throw new ArgumentException($"Unknown option '{a}'.");
            }
        }
        if (o.Dimensions < 2) throw new ArgumentException("--dims must be at least 2.");
        if (o.N < 1) throw new ArgumentException("--n must be at least 1.");
        return o;
    }
}
