using Relatude.DB.VectorIndex;
using VectorIndexBenchmarks.Engines;

namespace VectorIndexBenchmarks.Harness;

/// <summary>The semantic index implementations under test, and how each one is opened.</summary>
public static class Engines {
    public const string Memory = "memory";
    public const string Native = "native";
    /// <summary>The disk index probing every cluster: an exact search, directly comparable with the
    /// in-memory index's accuracy, so the price of exactness on disk is visible.</summary>
    public const string NativeExact = "native-exact";
    /// <summary>The same disk index on a deliberately tiny block-cache budget — a second
    /// configuration of one implementation rather than a third implementation, but naming it in
    /// --engines shows what the cache budget actually buys.</summary>
    public const string NativeLowMem = "native-lowmem";
    public const long LowMemCacheBytes = 8L * 1024 * 1024;

    public static readonly string[] All = [Memory, Native];

    /// <summary>A fixed log id: an index binds its data to the WAL file it belongs to, and the
    /// benchmark's reopen step has to present the same one or the index resets itself.</summary>
    public static readonly Guid WalFileId = new("7665637f-6f72-6265-6e63-686d61726b00");

    public static string DisplayName(string name) => name switch {
        Memory => "MemorySemanticIndex",
        Native => "NativeVectorIndex",
        NativeExact => "NativeVectorIndex (exact)",
        NativeLowMem => $"NativeVectorIndex ({LowMemCacheBytes / 1024 / 1024} MB cache)",
        _ => name,
    };

    /// <summary>Long-form description printed under the tables.</summary>
    public static string Description(string name) => name switch {
        Memory => "all vectors on the managed heap, every search an exact SIMD scan (Relatude.DB.DataStoreLocal)",
        Native => "disk segments, IVF clusters, cached blocks; searches the nearest clusters only (Relatude.DB.VectorIndex)",
        NativeExact => "the same disk index probing every cluster (accuracy 1): exact, and reading every block",
        NativeLowMem => $"the same disk index with its block cache budget set to {LowMemCacheBytes / 1024 / 1024} MB",
        _ => name,
    };

    /// <summary>True when the engine answers exactly, so its recall must come out at 100%.</summary>
    public static bool IsExact(string name, BenchOptions options)
        => name == Memory || name == NativeExact || options.Accuracy >= 1f;

    public static IBenchVectorIndex Create(string name, string dir, BenchOptions options, Corpus corpus) {
        Directory.CreateDirectory(dir);
        var ai = BenchAiEngine.Create(corpus);
        return name switch {
            Memory => new MemoryBenchIndex(dir, WalFileId, ai),
            Native or NativeExact or NativeLowMem => new NativeBenchIndex(dir, WalFileId, ai, new NativeVectorIndexOptions {
                Dimensions = corpus.Dimensions,
                Accuracy = name == NativeExact ? 1f : options.Accuracy,
                MaxCacheBytes = name == NativeLowMem ? LowMemCacheBytes : options.CacheBytes,
                // the corpus is normalized by construction; the per-add check is a measurable cost
                // that says nothing about the index, so it is off for every configuration
                ValidateNormalized = false,
            }),
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
    /// <summary>Byte budget of the disk index's block cache.</summary>
    public long CacheBytes = 256L * 1024 * 1024 * 100;
    /// <summary>Fraction of clusters the disk index probes per search.</summary>
    public float Accuracy = 0.25f;
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
