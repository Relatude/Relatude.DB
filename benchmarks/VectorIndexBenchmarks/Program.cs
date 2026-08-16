using System.Diagnostics;
using System.Text.Json;
using VectorIndexBenchmarks.Harness;

// dotnet run -c Release --project benchmarks/VectorIndexBenchmarks -- --matrix

// VectorIndexBenchmarks — benchmarks the vector indexes below against each other on one set of
// generated vectors and one query stream:
//
//   memory       Relatude MemorySemanticIndex — every vector on the managed heap, exact SIMD scan
//   ivs          Relatude IVS VectorIndex     — disk segments, IVF clusters, byte-budgeted block cache
//   hnsw         Relatude HnswVectorIndex     — the HNSW graph resident in flat int8 arenas, floats
//                                               mirrored when the budget allows, else re-score reads
//   hnsw-lowmem  the same, low-memory budget  — the graph on disk behind a small cache of
//                                               quarter-size routing records
//   sqlitevec    sqlite-vec                   — a vec0 virtual table in a SQLite file, exact KNN
//   usearch      USearch                      — an HNSW graph in native memory, top-k only
//
// The Relatude ones are driven through ISemanticIndex, the interface the data store uses; the
// third-party ones take vectors directly. Both forms of every query are the same query.
//
// The grid is deliberate: ivs and hnsw differ only in algorithm, hnsw and usearch only in where
// the vectors sit, hnsw and hnsw-lowmem only in how much memory they are allowed, and memory is
// the exact reference all of them are scored against.
//
//   dotnet run -c Release [-- options]        --help lists the options.

// ─────────────────────────────────── defaults ───────────────────────────────────
// What a plain `dotnet run -c Release` measures. These are the settings worth changing by hand;
// each one can also be given on the command line (the option is named beside it), and everything
// else the harness takes is in BenchOptions.
var defaults = new BenchOptions {
    // the data and the workload
    N = 500_00,                // --n           vectors indexed
    Dimensions = 1536,          // --dims        384 = a small sentence model, 1536 = OpenAI text-embedding-3-small
    Clusters = 200,             // --clusters    centers the vectors are drawn around; 0 = uniformly random directions
    ClusterNoise = 1.0f,        // --noise       how loosely they scatter around their center
    EngineNames =
    [Engines.Memory, Engines.IVS, Engines.Hnsw],



    //Engines.All.Where(f => f != Engines.HnswLowMem).ToArray(),  // --engines     memory,native,hnsw,hnsw-lowmem,usearch

    // what an index is allowed to spend, and what it may trade for it
    CacheMB = 2500,           // --cache       cached vectors and blocks; more than any run holds, so nothing is evicted
    Accuracy = 0.25f,           // --accuracy    fraction of clusters the IVF disk index probes per search
    HnswConnectivity = 16,      // --hnsw-m      graph degree, both graph indexes
    HnswExpansionAdd = 128,     // --hnsw-ef-add build effort
    HnswExpansionSearch = 64,   // --hnsw-ef     search effort — the graph indexes' accuracy dial

    // how the run is driven
    BatchSize = 5_000,          // --batch       vectors between state saves during the load
};

// ──────────────────────────────────── the sweep ─────────────────────────────────
// `dotnet run -c Release -- --matrix` runs this grid instead of a single benchmark — every engine
// at every corpus size at every cache budget — and writes one CSV row per run, appended as each
// one finishes. Everything the defaults above set that is not an axis here (dimensions, clusters,
// the HNSW dials) stays fixed across the whole sweep and is repeated on every row.
var matrix = new MatrixOptions {
    VectorCounts = [500, 1_000, 10_000, 50_000, 100_000, 500_000, 1_000_000],  // --matrix-n
    CacheMB = [10, 100, 500], // --matrix-cache
    EngineNames = [Engines.Memory, Engines.IVS, Engines.Hnsw],          // --engines
    CsvPath = "vector-matrix.csv",                                      // --csv
    HtmlPath = "vector-matrix.html",                                    // --html
};

BenchOptions options;
try {
    options = BenchOptions.Parse(args, defaults, matrix);
} catch (ArgumentException ex) {
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(BenchOptions.Usage);
    return 2;
}
if (options.ShowHelp) {
    Console.WriteLine(BenchOptions.Usage);
    return 0;
}

if (options.ChildEngine is not null) {
    // Child mode: one engine, one process, so its memory numbers are its own. Progress goes to the
    // parent as marked stderr lines, which it labels and renders.
    Progress.SendToParent();
    var childCorpus = Corpus.Build(options);
    BenchResult childResult;
    try {
        childResult = BenchRunner.Run(options.ChildEngine, childCorpus, options, options.ChildDir!);
    } catch (Exception ex) {
        childResult = new BenchResult { Engine = options.ChildEngine, N = options.N, Error = ex.ToString() };
    }
    Console.WriteLine(ChildProcess.ResultMarker + JsonSerializer.Serialize(childResult));
    return 0;
}

if (options.Matrix) return Matrix.Run(options, matrix);

Console.WriteLine("VectorIndexBenchmarks — Relatude vs sqlite-vec vs USearch");
Console.WriteLine($"{options.N:N0} vectors x {options.Dimensions} dimensions "
    + $"({options.N * (long)options.Dimensions * 4 / (1024.0 * 1024.0):N0} MB of raw float32) | "
    + $"{(options.Clusters > 0 ? $"{options.Clusters:N0} clusters, noise {options.ClusterNoise:0.##}" : "uniformly random directions")} | "
    + $"engines: {string.Join(", ", options.EngineNames)}");
Console.WriteLine();

var root = Path.Combine(options.DataDir, $"vecidxbench_{Environment.ProcessId}");
Directory.CreateDirectory(root);

try {
    var sw = Stopwatch.StartNew();
    var corpus = Corpus.Build(options);
    ProgressDisplay.Clear();
    Console.WriteLine($"Built the vectors and their exact answers in {sw.Elapsed.TotalSeconds:N1} s.");
    Console.WriteLine($"Searches use a minimum similarity of {corpus.MinSimilarity:0.000} "
        + $"(the {Corpus.FilterRank}th exact neighbour), recall is measured over "
        + $"{corpus.ExactNeighbours.Length} of the {Corpus.QueryCount} queries.");
    Console.WriteLine();

    var results = new List<BenchResult>();
    foreach (var engine in options.EngineNames) {
        var label = Engines.DisplayName(engine);
        var dir = Path.Combine(root, engine);
        var engineClock = Stopwatch.StartNew();
        BenchResult res;
        if (options.InProcess) {
            Progress.SendTo(text => ProgressDisplay.Show($"[{label}] {text}"));
            res = BenchRunner.Run(engine, corpus, options, dir);
            Progress.SendToConsole();
        } else {
            res = ChildProcess.Run(engine, options, dir, label);
        }
        ProgressDisplay.Clear();
        Console.WriteLine($"{label}: done in {engineClock.Elapsed.TotalSeconds:N1} s.");
        checkExactness(res, options);
        results.Add(res);
        tryDelete(dir);
    }

    Console.WriteLine();
    ResultTable.Print($"searches — queries/sec over {Corpus.QueryCount} queries, and accuracy against a brute-force scan", Columns.Search, results);
    Console.WriteLine();
    ResultTable.Print($"writes and footprint — state saves every {options.BatchSize:N0} vectors, "
        + $"{BenchRunner.WritePhaseOps:N0} updates and removes", Columns.Write, results);
    Console.WriteLine();
    Notes.Print(options, corpus);
    return results.Any(r => r.Error != null) ? 1 : 0;
} finally {
    tryDelete(root);
}

/// <summary>An implementation that answers exactly must reproduce the brute-force answer. Anything
/// less is a defect rather than a speed/accuracy trade-off, so it is reported as an error.</summary>
static void checkExactness(BenchResult res, BenchOptions options) {
    if (!Engines.IsExact(res.Engine, options)) return;
    var recall = res.Quality0To1("Recall");
    var filter = res.Quality0To1("FilterRecall");
    if (recall is < 0.999) res.Error ??= $"sanity: {Engines.DisplayName(res.Engine)} searches exactly but its recall is {recall:P1}";
    else if (filter is < 0.999) res.Error ??= $"sanity: {Engines.DisplayName(res.Engine)} searches exactly but its filter recall is {filter:P1}";
}

static void tryDelete(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* still held by a child or an AV scanner; the temp root is cleaned next run */ }
}
