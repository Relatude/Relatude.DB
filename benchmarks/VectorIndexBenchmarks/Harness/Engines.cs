using Relatude.DB.AI.HNSW;
using Relatude.DB.AI.ISV;
using VectorIndexBenchmarks.Engines;

namespace VectorIndexBenchmarks.Harness;

/// <summary>The vector index implementations under test, and how each one is opened.</summary>
public static class Engines {
    public const string Memory = "memory";
    public const string IVS = "ivs";
    /// <summary>The disk index probing every cluster: an exact search, directly comparable with the
    /// in-memory index's accuracy, so the price of exactness on disk is visible.</summary>
    public const string IVSExact = "ivs-exact";
    /// <summary>The same disk index on a deliberately tiny block-cache budget — a second
    /// configuration of one implementation rather than another implementation, but naming it in
    /// --engines shows what the cache budget actually buys.</summary>
    public const string IVSLowMem = "ivs-lowmem";
    /// <summary>The disk-based HNSW index: the same storage design as <see cref="IVS"/>, walking a
    /// proximity graph instead of probing the nearest clusters — the routing graph (int8 vectors +
    /// edges) resident in flat, prefetch-friendly memory, the float vectors mirrored too when the
    /// budget allows and read from their own file for exact re-scoring when it does not.</summary>
    public const string Hnsw = "hnsw";
    /// <summary>sqlite-vec: a vec0 virtual table in an ordinary SQLite file, exact by design.</summary>
    public const string SqliteVec = "sqlitevec";
    /// <summary>USearch: an HNSW graph in native memory — the other main answer to approximate search.</summary>
    public const string USearch = "usearch";
    public const long LowMemCacheBytes = 8L * 1024 * 1024;

    public static readonly string[] All = [Memory, IVS, Hnsw, USearch];
    /// <summary>Everything <c>--engines</c> accepts: the default set plus the configurations that
    /// are only interesting next to a specific question (exactness, a starved cache, sqlite-vec).</summary>
    public static readonly string[] Known = [.. All, IVSExact, IVSLowMem, SqliteVec];

    /// <summary>A fixed log id: an index binds its data to the WAL file it belongs to, and the
    /// benchmark's reopen step has to present the same one or the index resets itself.</summary>
    public static readonly Guid WalFileId = new("7665637f-6f72-6265-6e63-686d61726b00");

    /// <summary>The short label the matrix report charts and tables use, where the full class name
    /// would drown the numbers.</summary>
    public static string ShortName(string name) => name switch {
        Memory => "Memory",
        IVS => "IVS",
        IVSExact => "IVS exact",
        IVSLowMem => "IVS lowmem",
        Hnsw => "HNSW",
        SqliteVec => "sqlite-vec",
        USearch => "USearch",
        _ => name,
    };

    public static string DisplayName(string name) => name switch {
        Memory => "MemorySemanticIndex",
        IVS => "IVSVectorIndex",
        IVSExact => "IVSVectorIndex (exact)",
        IVSLowMem => $"IVSVectorIndex ({LowMemCacheBytes / 1024 / 1024} MB cache)",
        Hnsw => "HnswVectorIndex",
        SqliteVec => "sqlite-vec",
        USearch => "USearch (HNSW)",
        _ => name,
    };

    /// <summary>Long-form description printed under the tables.</summary>
    public static string Description(string name) => name switch {
        Memory => "all vectors on the managed heap, every search an exact SIMD scan (Relatude.DB.DataStoreLocal)",
        IVS => "disk segments, IVF clusters, cached blocks; searches the nearest clusters only (Relatude.DB.VectorIndex)",
        IVSExact => "the same disk index probing every cluster (accuracy 1): exact, and reading every block",
        IVSLowMem => $"the same disk index with its block cache budget set to {LowMemCacheBytes / 1024 / 1024} MB",
        Hnsw => "the HNSW graph resident in flat int8 arenas, floats mirrored or read only to re-score (Relatude.DB.VectorIndex)",
        SqliteVec => "third party: a vec0 virtual table in a SQLite file, exact KNN by full scan (asg017/sqlite-vec)",
        USearch => "third party: an HNSW graph in native memory, top-k only (unum-cloud/USearch)",
        _ => name,
    };

    /// <summary>True when the implementation answers exactly, so its recall must come out at 100%.
    /// sqlite-vec has no approximate index at all; the Relatude disk index is exact only when it
    /// probes every cluster.</summary>
    public static bool IsExact(string name, BenchOptions options) => name switch {
        Memory or IVSExact or SqliteVec => true,
        IVS or IVSLowMem => options.Accuracy >= 1f,
        _ => false,
    };

    public static IBenchVectorIndex Create(string name, string dir, BenchOptions options, Corpus corpus) {
        Directory.CreateDirectory(dir);
        return name switch {
            Memory => new MemoryBenchIndex(dir, WalFileId, BenchAiEngine.Create(corpus)),
            IVS or IVSExact or IVSLowMem => new IVSBenchIndex(dir, WalFileId, BenchAiEngine.Create(corpus), new Relatude.DB.AI.ISV.VectorIndexOptions {
                Dimensions = corpus.Dimensions,
                Accuracy = name == IVSExact ? 1f : options.Accuracy,
                MaxCacheBytes = name == IVSLowMem ? LowMemCacheBytes : options.CacheBytes,
                // the corpus is normalized by construction; the per-add check is a measurable cost
                // that says nothing about the index, so it is off for every configuration
                ValidateNormalized = false,
            }),
            // the HNSW index takes the same dials as USearch, so the two graph rows are configured
            // identically; MaxMemoryBytes is its one budget, mapped from --cache like the other
            // indexes' cache budgets (the resident graph is its floor — a budget below the floor is
            // exceeded, not honored, so small --cache values price the no-mirror band, not a slow mode)
            Hnsw => new HnswBenchIndex(dir, WalFileId, BenchAiEngine.Create(corpus), new Relatude.DB.AI.HNSW.VectorIndexOptions {
                Dimensions = corpus.Dimensions,
                MaxMemoryBytes = options.CacheBytes,
                Connectivity = options.HnswConnectivity,
                EfConstruction = options.HnswExpansionAdd,
                EfSearch = options.HnswExpansionSearch,
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

