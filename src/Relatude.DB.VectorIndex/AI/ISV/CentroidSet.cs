using System.Buffers.Binary;
using Relatude.DB.Common;

namespace Relatude.DB.AI.ISV;

/// <summary>
/// The cluster centroids of a trained index generation: a K x Dims matrix of unit vectors from a
/// spherical k-means (nearest = highest dot product, means are re-normalized). Searches rank all
/// centroids exactly and probe the best clusters; write-path assignment goes through a small
/// two-level structure (centroids grouped by a mini k-means over the centroids themselves) so
/// assigning a vector costs roughly sqrt(K) + neighbourhood dots instead of K. For centroid counts
/// above 1024 the training itself is two-level as well, which keeps a full retrain of millions of
/// vectors in the tens of seconds instead of minutes.
/// </summary>
internal sealed class CentroidSet {
    const long Magic = 0x3143_5644_4244_5452;   // "RTDBDVC1"
    const long EndMagicV = 0x3143_444E_4544_52; // arbitrary end marker
    const int Version = 1;
    const int headerBytes = 28;
    public long Generation { get; }
    public int Dims { get; }
    public int K { get; }
    public float[] Data { get; } // K * Dims, each row L2-normalized
    float[]? _groupData;         // group centroids, flat
    int[][]? _groupMembers;      // centroid indexes per group
    CentroidSet(long generation, int dims, float[] data) {
        Generation = generation;
        Dims = dims;
        Data = data;
        K = data.Length / dims;
        buildGroups();
    }
    public static CentroidSet FromRows(long generation, int dims, float[][] rows) {
        var data = new float[rows.Length * dims];
        for (var i = 0; i < rows.Length; i++) rows[i].CopyTo(data, i * dims);
        return new CentroidSet(generation, dims, data);
    }

    // ---- assignment and probing ------------------------------------------------------------------

    /// <summary>Cluster for a new vector; approximate (two-level) when K is large.</summary>
    public int Assign(ReadOnlySpan<float> v) {
        if (_groupMembers == null || _groupData == null) return nearestFlat(v, Data, K, Dims);
        // pick the two best groups, then the best centroid among their members
        int best = 0, second = 0;
        float bestScore = float.MinValue, secondScore = float.MinValue;
        var g = _groupMembers.Length;
        for (var i = 0; i < g; i++) {
            var s = VectorMath.Dot(v, _groupData.AsSpan(i * Dims, Dims));
            if (s > bestScore) {
                second = best; secondScore = bestScore;
                best = i; bestScore = s;
            } else if (s > secondScore) {
                second = i; secondScore = s;
            }
        }
        var bestCentroid = 0;
        var bestCentroidScore = float.MinValue;
        scanMembers(v, _groupMembers[best], ref bestCentroid, ref bestCentroidScore);
        if (second != best) scanMembers(v, _groupMembers[second], ref bestCentroid, ref bestCentroidScore);
        return bestCentroid;
    }
    void scanMembers(ReadOnlySpan<float> v, int[] members, ref int bestCentroid, ref float bestScore) {
        foreach (var c in members) {
            var s = VectorMath.Dot(v, Data.AsSpan(c * Dims, Dims));
            if (s > bestScore) {
                bestScore = s;
                bestCentroid = c;
            }
        }
    }
    /// <summary>All cluster ids ordered by exact centroid similarity to the query, best first.</summary>
    public int[] RankClusters(float[] query) {
        var scores = new float[K];
        if ((long)K * Dims >= 1 << 21) {
            Parallel.For(0, K, c => scores[c] = VectorMath.Dot(query, Data.AsSpan(c * Dims, Dims)));
        } else {
            for (var c = 0; c < K; c++) scores[c] = VectorMath.Dot(query, Data.AsSpan(c * Dims, Dims));
        }
        var order = new int[K];
        for (var i = 0; i < K; i++) order[i] = i;
        Array.Sort(scores, order); // ascending by score
        Array.Reverse(order);
        return order;
    }
    static int nearestFlat(ReadOnlySpan<float> v, float[] data, int count, int dims) {
        var best = 0;
        var bestScore = float.MinValue;
        for (var c = 0; c < count; c++) {
            var s = VectorMath.Dot(v, data.AsSpan(c * dims, dims));
            if (s > bestScore) {
                bestScore = s;
                best = c;
            }
        }
        return best;
    }
    void buildGroups() {
        if (K <= 128) return; // exact assignment is cheap enough
        var rows = new float[K][];
        for (var i = 0; i < K; i++) rows[i] = Data.AsSpan(i * Dims, Dims).ToArray();
        var g = (int)Math.Ceiling(Math.Sqrt(K));
        var groups = kMeans(rows, g, 4, new Random(987654321), null, null);
        var members = new List<int>[groups.Length];
        for (var i = 0; i < K; i++) {
            var gi = nearestRow(rows[i], groups);
            (members[gi] ??= []).Add(i);
        }
        var groupRows = new List<float[]>(groups.Length);
        var groupMembers = new List<int[]>(groups.Length);
        for (var i = 0; i < groups.Length; i++) {
            if (members[i] == null) continue; // empty groups are dropped
            groupRows.Add(groups[i]);
            groupMembers.Add(members[i].ToArray());
        }
        _groupData = new float[groupRows.Count * Dims];
        for (var i = 0; i < groupRows.Count; i++) groupRows[i].CopyTo(_groupData, i * Dims);
        _groupMembers = groupMembers.ToArray();
    }

    // ---- training ----------------------------------------------------------------------------------

    public static CentroidSet Train(long generation, List<float[]> samples, int k, int iterations, Action<string>? log) {
        var dims = samples[0].Length;
        k = Math.Min(k, samples.Count);
        var rnd = new Random(1234567); // deterministic; vector data varies more than seeding ever will
        float[][] rows;
        if (k <= 1024) {
            rows = kMeans(samples, k, iterations, rnd, log, "clusters");
        } else {
            // two-level: k1 coarse clusters, then each coarse cluster split proportionally to its size
            var k1 = (int)Math.Ceiling(Math.Sqrt(k));
            var top = kMeans(samples, k1, iterations, rnd, log, "coarse clusters");
            var assign = new int[samples.Count];
            Parallel.For(0, samples.Count, i => assign[i] = nearestRow(samples[i], top));
            var parts = new List<float[]>?[top.Length];
            for (var i = 0; i < samples.Count; i++) (parts[assign[i]] ??= []).Add(samples[i]);
            var all = new List<float[]>(k);
            foreach (var part in parts) {
                if (part == null) continue;
                var kc = (int)Math.Clamp(Math.Round((double)k * part.Count / samples.Count), 1, part.Count);
                all.AddRange(kMeans(part, kc, iterations, rnd, null, null));
            }
            rows = [.. all];
            log?.Invoke("Split " + top.Length + " coarse clusters into " + rows.Length + " clusters. ");
        }
        return FromRows(generation, dims, rows);
    }
    static float[][] kMeans(IReadOnlyList<float[]> points, int k, int iterations, Random rnd, Action<string>? log, string? label) {
        var n = points.Count;
        var dims = points[0].Length;
        if (k >= n) { // degenerate: every point is its own centroid
            var copy = new float[n][];
            for (var i = 0; i < n; i++) copy[i] = (float[])points[i].Clone();
            return copy;
        }
        // seed with k distinct random points (partial Fisher-Yates)
        var idx = new int[n];
        for (var i = 0; i < n; i++) idx[i] = i;
        for (var i = 0; i < k; i++) {
            var j = i + rnd.Next(n - i);
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        var centroids = new float[k][];
        for (var i = 0; i < k; i++) centroids[i] = (float[])points[idx[i]].Clone();
        var assign = new int[n];
        for (var it = 0; it < iterations; it++) {
            Parallel.For(0, n, i => assign[i] = nearestRow(points[i], centroids));
            var sums = new float[k][];
            var counts = new int[k];
            for (var i = 0; i < n; i++) {
                var c = assign[i];
                VectorMath.AddInPlace(sums[c] ??= new float[dims], points[i]);
                counts[c]++;
            }
            for (var c = 0; c < k; c++) {
                if (counts[c] == 0 || !VectorMath.NormalizeInPlace(sums[c]!)) {
                    centroids[c] = (float[])points[rnd.Next(n)].Clone(); // reseed a dead centroid
                } else {
                    centroids[c] = sums[c]!;
                }
            }
            if (label != null) log?.Invoke("Training " + k + " " + label + ": iteration " + (it + 1) + "/" + iterations);
        }
        return centroids;
    }
    static int nearestRow(float[] p, float[][] centroids) {
        var best = 0;
        var bestScore = float.MinValue;
        for (var c = 0; c < centroids.Length; c++) {
            var s = VectorMath.Dot(p, centroids[c]);
            if (s > bestScore) {
                bestScore = s;
                best = c;
            }
        }
        return best;
    }

    // ---- persistence -------------------------------------------------------------------------------

    public void Write(string path) {
        var payloadBytes = Data.Length * 4;
        var bytes = new byte[headerBytes + payloadBytes + 16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), Generation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), K);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), Dims);
        Buffer.BlockCopy(Data, 0, bytes, headerBytes, payloadBytes);
        var pos = headerBytes + payloadBytes;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(pos), VectorIndexManifest.fnv1a(bytes.AsSpan(0, pos)));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos + 8), EndMagicV);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            fs.Write(bytes);
            fs.Flush(true);
        }
        FileOpenRetry.Replace(tmp, path);
    }
    public static CentroidSet? TryRead(string path, long expectedGeneration, int expectedDims) {
        try {
            if (!File.Exists(path)) return null;
            // wait out a lock rather than reporting "no centroids": the caller answers that by
            // retraining from scratch, which is far more expensive than waiting a moment
            var bytes = FileOpenRetry.Open(path, () => File.ReadAllBytes(path));
            if (bytes.Length < headerBytes + 16) return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes) != Magic) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version) return null;
            var generation = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12));
            var k = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
            var dims = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
            if (generation != expectedGeneration || dims != expectedDims) return null;
            if (k <= 0 || dims <= 0) return null;
            var payloadBytes = (long)k * dims * 4;
            if (bytes.Length != headerBytes + payloadBytes + 16) return null;
            var pos = (int)(headerBytes + payloadBytes);
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos)) != VectorIndexManifest.fnv1a(bytes.AsSpan(0, pos))) return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos + 8)) != EndMagicV) return null;
            var data = new float[k * dims];
            Buffer.BlockCopy(bytes, headerBytes, data, 0, (int)payloadBytes);
            return new CentroidSet(generation, dims, data);
        } catch {
            return null; // unreadable centroid files reset the index like any other corruption
        }
    }
}
