namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// Everything one run is configured by. The values themselves live at the top of <c>Program.cs</c>,
/// where they can be edited without reading the harness; what is here is what each setting means
/// and how a command line overrides it — so a setting has exactly one default, in one place.
/// </summary>
public sealed class BenchOptions {
    /// <summary>Vectors indexed.</summary>
    public int N;
    /// <summary>Vector length. 384 is a small sentence-transformer model, 1536 an OpenAI
    /// text-embedding-3-small; both are worth running.</summary>
    public int Dimensions;
    /// <summary>Cluster centers the vectors are drawn around; 0 gives uniformly random directions,
    /// the worst case for any clustering index.</summary>
    public int Clusters;
    /// <summary>How far vectors scatter from their center, as a ratio of the center's own length.
    /// Higher is a looser cluster: 1.0 puts a vector at about 0.87 cosine similarity to its center.</summary>
    public float ClusterNoise;
    /// <summary>Vectors per state save during the index, update and remove phases.</summary>
    public int BatchSize;
    /// <summary>Megabyte budget of the disk index's block cache, of the HNSW index's whole memory
    /// budget, and of sqlite-vec's page cache. All three are limits rather than reservations, so a
    /// value larger than the data means "do not evict".</summary>
    public int CacheMB;
    /// <summary>Fraction of clusters the IVF disk index probes per search.</summary>
    public float Accuracy;
    /// <summary>HNSW graph degree (USearch's <c>connectivity</c>).</summary>
    public int HnswConnectivity;
    /// <summary>HNSW build effort (USearch's <c>expansionAdd</c>).</summary>
    public int HnswExpansionAdd;
    /// <summary>HNSW search effort (USearch's <c>expansionSearch</c>) — the closest analogue to the
    /// disk index's <see cref="Accuracy"/>, and the dial to turn when comparing recall.</summary>
    public int HnswExpansionSearch;
    public string[] EngineNames = Engines.All;

    /// <summary>The similarity floor the search phases pass down; derived from the exact answers
    /// when not given, so about <see cref="Corpus.FilterRank"/> vectors clear it.</summary>
    public float? MinSimilarity;
    /// <summary>Save state after every batch (the cadence of a store that checkpoints often)
    /// instead of once at the end of the load.</summary>
    public bool PersistEveryBatch;
    /// <summary>Where the index files are written. Each engine gets a subdirectory of a run root
    /// that is deleted afterwards.</summary>
    public string DataDir = Path.GetTempPath();
    /// <summary>Run every engine in this process instead of one child process each: faster, and the
    /// memory columns become noisy because the engines share a heap.</summary>
    public bool InProcess;
    /// <summary>Sweep mode: run the grid in <see cref="MatrixOptions"/> and write a CSV row per run
    /// instead of the two tables.</summary>
    public bool Matrix;
    /// <summary>Skip the recall measurement and the four search phases, leaving the write rates and
    /// the footprint — which is what a sweep of many runs records, and most of what it waits for.
    /// The mixed phase still searches: that is what makes it mixed.</summary>
    public bool SkipSearches;
    public bool ShowHelp;
    /// <summary>Set in a child process: the one engine it runs, and the directory it runs it in.</summary>
    public string? ChildEngine, ChildDir;

    public long CacheBytes => CacheMB * 1024L * 1024L;

    /// <summary>A copy to vary one setting on, which is how a sweep builds its runs.</summary>
    public BenchOptions Clone() => (BenchOptions)MemberwiseClone();

    /// <summary>Vectors the update and remove phases touch, and writes the mixed phase makes —
    /// fixed, not a share of the corpus, so a slow engine cannot decide the suite's runtime.</summary>
    public int UpdateCount => Math.Min(BenchRunner.WritePhaseOps, N);
    public int RemoveCount => Math.Min(BenchRunner.WritePhaseOps, N);
    public int MixedCount => BenchRunner.MixedPhaseWrites;
    /// <summary>The small delta indexed before the durability checkpoint is timed.</summary>
    public int DeltaCount => Math.Clamp(N / 100, 100, 1_000);

    public const string Usage = """
        dotnet run -c Release [-- options]

          --n=<count>            vectors indexed
          --dims=<n>             vector length (384 = a small sentence model, 1536 = OpenAI small)
          --clusters=<n>         cluster centers the vectors are drawn around; 0 = uniformly random
                                 directions, the worst case for a clustering index
          --noise=<f>            how loosely vectors scatter around their center
          --batch=<n>            vectors between state saves during the load
          --cache=<MB>           cached vectors and blocks; the HNSW index's whole memory budget
          --accuracy=<f>         fraction of clusters the IVF disk index probes per search
          --hnsw-m=<n>           graph degree (connectivity) — both graph indexes
          --hnsw-ef-add=<n>      build effort (expansionAdd) — both graph indexes
          --hnsw-ef=<n>          search effort (expansionSearch) — their accuracy dial
          --min-sim=<f>          the similarity floor the searches pass down (default: the
                                 similarity of the 500th exact neighbour, ~500 candidates)
          --engines=all|<list>   all = memory,ivs,hnsw,hnsw-lowmem,usearch; also ivs-exact,
                                 ivs-lowmem and sqlitevec. sqlite-vec scans every vector on every
                                 query, and hnsw-lowmem indexes at a fraction of the speed, so both
                                 dominate the runtime — drop them for quick iterations.
          --persist=batch        save state after every batch instead of once after the load
          --data=<dir>           working directory for index files (default: %TEMP%)
          --in-process           run everything in this process (memory numbers get noisy)
          --skip-searches        skip the recall measurement and the search phases, leaving the
                                 write rates and the footprint (the mixed phase still searches)
          --help                 this list

        A sweep runs the whole grid and writes one CSV row per run instead of the two tables:

          --matrix               run the grid at the top of Program.cs
          --matrix-n=<list>      corpus sizes to sweep, e.g. 1000,10000,100000 (implies --matrix)
          --matrix-cache=<list>  cache budgets in MB to sweep, e.g. 10,100,1000 (implies --matrix)
          --csv=<path>           where the rows go (default: vector-matrix.csv)
          --html=<path>          where the HTML report goes (default: next to the csv)
          --engines=<list>       the engines swept, as in a single run

        Defaults, and the grid, are at the top of Program.cs.
        """;

    /// <summary>Applies the command line on top of <paramref name="defaults"/> and
    /// <paramref name="matrix"/>, which are the settings blocks in <c>Program.cs</c>.</summary>
    public static BenchOptions Parse(string[] args, BenchOptions defaults, MatrixOptions matrix) {
        var o = defaults;
        foreach (var a in args) {
            var kv = a.Split('=', 2);
            switch (kv[0]) {
                case "--n": o.N = int.Parse(value(a, kv)); break;
                case "--dims": o.Dimensions = int.Parse(value(a, kv)); break;
                case "--clusters": o.Clusters = int.Parse(value(a, kv)); break;
                case "--noise": o.ClusterNoise = float.Parse(value(a, kv)); break;
                case "--batch": o.BatchSize = int.Parse(value(a, kv)); break;
                case "--cache": o.CacheMB = int.Parse(value(a, kv)); break;
                case "--accuracy": o.Accuracy = float.Parse(value(a, kv)); break;
                case "--hnsw-m": o.HnswConnectivity = int.Parse(value(a, kv)); break;
                case "--hnsw-ef-add": o.HnswExpansionAdd = int.Parse(value(a, kv)); break;
                case "--hnsw-ef": o.HnswExpansionSearch = int.Parse(value(a, kv)); break;
                case "--min-sim": o.MinSimilarity = float.Parse(value(a, kv)); break;
                case "--persist": o.PersistEveryBatch = value(a, kv) == "batch"; break;
                // one engine list, whichever mode is running: a sweep of the engines named here
                case "--engines": o.EngineNames = matrix.EngineNames = value(a, kv) == "all" ? Engines.All : value(a, kv).Split(','); break;
                case "--data": o.DataDir = value(a, kv); break;
                case "--in-process": o.InProcess = true; break;
                case "--skip-searches": o.SkipSearches = true; break;
                case "--matrix": o.Matrix = true; break;
                case "--matrix-n": matrix.VectorCounts = ints(a, value(a, kv)); o.Matrix = true; break;
                case "--matrix-cache": matrix.CacheMB = ints(a, value(a, kv)); o.Matrix = true; break;
                case "--csv": matrix.CsvPath = value(a, kv); break;
                case "--html": matrix.HtmlPath = value(a, kv); break;
                case "--help" or "-h" or "-?": o.ShowHelp = true; break;
                case "--child-engine": o.ChildEngine = value(a, kv); break;
                case "--child-dir": o.ChildDir = value(a, kv); break;
                default: throw new ArgumentException($"Unknown option '{a}'.");
            }
        }
        if (o.ShowHelp) return o;
        if (o.Dimensions < 2) throw new ArgumentException("--dims must be at least 2.");
        if (o.N < 1) throw new ArgumentException("--n must be at least 1.");
        if (o.BatchSize < 1) throw new ArgumentException("--batch must be at least 1.");
        if (o.CacheMB < 1) throw new ArgumentException("--cache must be at least 1 MB.");
        foreach (var e in o.EngineNames) known(e);
        if (o.Matrix) {
            if (matrix.VectorCounts.Length == 0) throw new ArgumentException("--matrix-n needs at least one corpus size.");
            if (matrix.CacheMB.Length == 0) throw new ArgumentException("--matrix-cache needs at least one cache budget.");
            if (matrix.EngineNames.Length == 0) throw new ArgumentException("--engines needs at least one engine.");
            if (matrix.VectorCounts.Any(v => v < 1)) throw new ArgumentException("--matrix-n values must be at least 1.");
            if (matrix.CacheMB.Any(v => v < 1)) throw new ArgumentException("--matrix-cache values must be at least 1 MB.");
            foreach (var e in matrix.EngineNames) known(e);
        }
        return o;
    }

    static void known(string engine) {
        if (!Engines.Known.Contains(engine))
            throw new ArgumentException($"Unknown engine '{engine}'. Known: {string.Join(", ", Engines.Known)}.");
    }

    static string value(string arg, string[] kv)
        => kv.Length == 2 ? kv[1] : throw new ArgumentException($"Option '{arg}' needs a value: {arg}=<value>.");

    static int[] ints(string arg, string list) {
        var values = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0) throw new ArgumentException($"Option '{arg}' needs a comma-separated list of numbers.");
        return [.. values.Select(v => int.TryParse(v, out var i) ? i : throw new ArgumentException($"'{v}' in '{arg}' is not a number."))];
    }
}
