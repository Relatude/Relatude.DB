using System.Runtime.CompilerServices;

namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// The synthetic vector collection and query workload both indexes are measured on. Built once
/// from a fixed seed, so every engine indexes the same vectors and answers the same queries in the
/// same order.
///
/// <para>Vectors are L2-normalized (cosine similarity is a plain dot product, which both indexes
/// require). By default they are drawn around a set of random cluster centers, because that is
/// what real embeddings look like and what an IVF index is built for; <c>--clusters=0</c> gives
/// uniformly random directions instead, which is the worst case for any clustering index — nothing
/// to cluster, so probing a fraction of the clusters is a fraction of a random sample.</para>
///
/// <para>All vectors live in one contiguous block rather than one array each, so the harness's own
/// footprint is a single allocation that is already counted in the memory baseline; every vector
/// handed to an index is a fresh copy, exactly as an embedding arriving from an AI provider would
/// be, so what an index retains shows up as its own memory.</para>
/// </summary>
public sealed class Corpus {
    /// <summary>Distinct queries in the search phases.</summary>
    public const int QueryCount = 200;
    /// <summary>Queries the exact (brute-force) answer is computed for, to measure recall.</summary>
    public const int RecallQueryCount = 50;
    /// <summary>Recall is measured over the first page of hits.</summary>
    public const int RecallTopK = 10;
    /// <summary>The similarity floor the searches use is set to the similarity of this exact
    /// neighbour, so a search has roughly this many candidates whatever the data looks like.</summary>
    public const int FilterRank = 500;

    /// <summary>Node id of vector <paramref name="index"/>. Ids start at 1: 0 is a sentinel in
    /// several index implementations.</summary>
    public static int NodeId(int index) => index + 1;

    float[] _block = [];      // the corpus vectors, back to back
    float[] _extra = [];      // update, delta and mixed vectors, back to back
    int _updateOffset, _deltaOffset, _mixedOffset;

    public int Dimensions { get; private init; }
    public int Count { get; private init; }
    public int UpdateCount { get; private init; }
    public int DeltaCount { get; private init; }
    public int MixedCount { get; private init; }

    /// <summary>The query texts the search phases pass to <c>ISemanticIndex</c>; the benchmark's AI
    /// engine maps each one back to <see cref="QueryVectors"/>.</summary>
    public string[] QueryTexts { get; private set; } = [];
    public float[][] QueryVectors { get; private set; } = [];
    /// <summary>Per recall query, the ids of the <see cref="FilterRank"/> nearest vectors of the
    /// freshly loaded corpus, best first. The first <see cref="RecallTopK"/> are the exact page a
    /// ranked search should return; the whole list is the exact answer to a filter search at
    /// <see cref="MinSimilarity"/>.</summary>
    public int[][] ExactNeighbours { get; private set; } = [];
    /// <summary>The similarities of <see cref="ExactNeighbours"/>, same order.</summary>
    public float[][] ExactSimilarities { get; private set; } = [];
    /// <summary>The similarity floor the search phases pass down: derived from the exact answers
    /// so that about <see cref="FilterRank"/> of the vectors clear it, which is what a semantic
    /// query with a minimum-similarity setting looks like.</summary>
    public float MinSimilarity { get; private set; }

    /// <summary>A fresh copy of corpus vector <paramref name="i"/>.</summary>
    public float[] Vector(int i) => copy(_block, i * Dimensions);
    /// <summary>A fresh copy of the replacement vector the update phase writes over vector <paramref name="i"/>.</summary>
    public float[] UpdateVector(int i) => copy(_extra, _updateOffset + i * Dimensions);
    /// <summary>A fresh copy of a vector of the small delta indexed before the durability checkpoint is timed.</summary>
    public float[] DeltaVector(int i) => copy(_extra, _deltaOffset + i * Dimensions);
    /// <summary>A fresh copy of a vector the mixed phase inserts.</summary>
    public float[] MixedVector(int i) => copy(_extra, _mixedOffset + i * Dimensions);

    float[] copy(float[] source, int offset) {
        var v = new float[Dimensions];
        Array.Copy(source, offset, v, 0, Dimensions);
        return v;
    }

    public static Corpus Build(BenchOptions options) {
        var count = options.N;
        var dims = options.Dimensions;
        var rnd = new Random(20260810);
        var corpus = new Corpus {
            Dimensions = dims,
            Count = count,
            UpdateCount = options.UpdateCount,
            DeltaCount = options.DeltaCount,
            MixedCount = options.MixedCount,
        };

        // Cluster centers, shared by the corpus and the queries: a query is a point drawn from the
        // same distribution as the data, which is what a real semantic search is.
        var centers = new float[Math.Max(1, options.Clusters)][];
        for (var c = 0; c < centers.Length; c++) centers[c] = randomDirection(rnd, dims);
        var clustered = options.Clusters > 0;

        if ((long)count * dims > int.MaxValue)
            throw new ArgumentException($"{count:N0} x {dims} vectors do not fit in one array; lower --n or --dims. ");
        var extra = options.UpdateCount + options.DeltaCount + options.MixedCount;
        Progress.Phase("generating vectors");
        corpus._block = new float[count * dims];
        fill(corpus._block, count, dims, rnd, centers, clustered, options.ClusterNoise, 0, count + extra);

        corpus._extra = new float[extra * dims];
        fill(corpus._extra, extra, dims, rnd, centers, clustered, options.ClusterNoise, count, count + extra);
        corpus._updateOffset = 0;
        corpus._deltaOffset = options.UpdateCount * dims;
        corpus._mixedOffset = corpus._deltaOffset + options.DeltaCount * dims;

        var queries = new float[QueryCount][];
        var texts = new string[QueryCount];
        for (var i = 0; i < QueryCount; i++) {
            queries[i] = clustered ? nearCenter(rnd, centers[rnd.Next(centers.Length)], options.ClusterNoise) : randomDirection(rnd, dims);
            texts[i] = "q" + i; // the benchmark AI engine embeds this back to queries[i]
        }
        corpus.QueryTexts = texts;
        corpus.QueryVectors = queries;

        corpus.buildExactAnswers(options);
        return corpus;
    }

    static void fill(float[] block, int count, int dims, Random rnd, float[][] centers, bool clustered, float noise, int reportedBefore, int reportedTotal) {
        for (var i = 0; i < count; i++) {
            var v = clustered ? nearCenter(rnd, centers[rnd.Next(centers.Length)], noise) : randomDirection(rnd, dims);
            Array.Copy(v, 0, block, i * dims, dims);
            Progress.Item(reportedBefore + i + 1, reportedTotal);
        }
    }

    static float[] randomDirection(Random rnd, int dims) {
        var v = new float[dims];
        for (var i = 0; i < dims; i++) v[i] = (float)(rnd.NextDouble() * 2 - 1);
        normalize(v);
        return v;
    }

    /// <summary>
    /// A point near <paramref name="center"/>. The per-component noise is scaled by 1/sqrt(dims),
    /// so the resulting similarity to the center — roughly 1/sqrt(1+noise²/3) — depends on
    /// <paramref name="noise"/> alone and not on the number of dimensions: a cluster is equally
    /// tight at 384 and at 1536 dimensions, and the two are comparable runs.
    /// </summary>
    static float[] nearCenter(Random rnd, float[] center, float noise) {
        var dims = center.Length;
        var scale = noise / MathF.Sqrt(dims);
        var v = new float[dims];
        for (var i = 0; i < dims; i++) v[i] = center[i] + (float)(rnd.NextDouble() * 2 - 1) * scale;
        normalize(v);
        return v;
    }

    static void normalize(float[] v) {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        var length = Math.Sqrt(sum);
        if (length == 0) { v[0] = 1; return; }
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / length);
    }

    /// <summary>
    /// Brute-force nearest neighbours for the recall queries over the freshly loaded corpus, and
    /// the filter threshold derived from them. Computed here, before any index exists, so it costs
    /// the engines neither time nor measured memory — and so both engines are scored against the
    /// same exact answer rather than against each other.
    /// </summary>
    void buildExactAnswers(BenchOptions options) {
        var k = Math.Min(FilterRank, Count);
        var queryCount = Math.Min(RecallQueryCount, QueryCount);
        var neighbours = new int[queryCount][];
        var similarities = new float[queryCount][];
        var cutoffs = new float[queryCount];
        Progress.Phase("brute-forcing the exact answers");
        var answered = 0;
        Parallel.For(0, queryCount, q => {
            var query = QueryVectors[q];
            var best = new PriorityQueue<int, float>(k + 1); // min-heap of the k best so far
            for (var i = 0; i < Count; i++) {
                var sim = Dot(query, _block.AsSpan(i * Dimensions, Dimensions));
                if (best.Count < k) best.Enqueue(NodeId(i), sim);
                else if (best.TryPeek(out _, out var worst) && sim > worst) {
                    best.Dequeue();
                    best.Enqueue(NodeId(i), sim);
                }
            }
            var ordered = new List<(int id, float sim)>(best.Count);
            while (best.TryDequeue(out var id, out var sim)) ordered.Add((id, sim));
            ordered.Sort((a, b) => b.sim.CompareTo(a.sim)); // best first
            neighbours[q] = ordered.Select(o => o.id).ToArray();
            similarities[q] = ordered.Select(o => o.sim).ToArray();
            cutoffs[q] = ordered.Count > 0 ? ordered[^1].sim : 0f;
            Progress.Item(Interlocked.Increment(ref answered), queryCount);
        });
        ExactNeighbours = neighbours;
        ExactSimilarities = similarities;
        // One threshold for every query, so the phases measure one workload: the average of the
        // per-query cutoffs, which leaves a search roughly FilterRank candidates.
        MinSimilarity = options.MinSimilarity ?? (queryCount > 0 ? cutoffs.Average() : 0f);
    }

    /// <summary>Cosine similarity of two unit vectors: a plain dot product, SIMD where available.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        var sum = 0f;
        var i = 0;
        // fully qualified: this class has its own Vector(int) accessor
        if (System.Numerics.Vector.IsHardwareAccelerated) {
            var w = System.Numerics.Vector<float>.Count;
            for (; i <= a.Length - w; i += w)
                sum += System.Numerics.Vector.Dot(new System.Numerics.Vector<float>(a.Slice(i, w)), new System.Numerics.Vector<float>(b.Slice(i, w)));
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
