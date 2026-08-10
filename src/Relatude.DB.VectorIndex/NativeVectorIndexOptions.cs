namespace Relatude.DB.VectorIndex;

/// <summary>Tuning knobs for <see cref="NativeVectorIndex"/>. All sizes are in bytes.</summary>
public sealed class NativeVectorIndexOptions {
    /// <summary>Vector length. When null it is taken from the AI engine settings, or locked to the
    /// length of the first vector added. Every vector must have this exact length (throws if not).</summary>
    public int? Dimensions { get; set; }
    /// <summary>Byte budget for the in-memory cache of vector blocks read from disk. Adjustable at
    /// runtime through <see cref="NativeVectorIndex.MaxCacheBytes"/>.</summary>
    public long MaxCacheBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>Search accuracy in (0..1]: the fraction of clusters probed per search. 1 probes every
    /// cluster (exact search), lower values are proportionally faster but may miss hits whose cluster
    /// was not probed. Has no effect (searches are always exact) until the index is large enough to
    /// be clustered, see <see cref="MinVectorsForClustering"/>. Adjustable at runtime.</summary>
    public float Accuracy { get; set; } = 0.25f;
    /// <summary>Verify on every add that the vector is L2-normalized and throw if it is not.
    /// Cosine similarity is computed as a plain dot product, which requires unit vectors.</summary>
    public bool ValidateNormalized { get; set; } = true;
    /// <summary>Unflushed writes are spilled to a segment file once they exceed this size, keeping
    /// memory usage bounded during bulk loads. Durability still comes from the WAL; spilled segments
    /// are only referenced by the manifest at the next state save.</summary>
    public long MemTableFlushThresholdBytes { get; set; } = 64L * 1024 * 1024;
    /// <summary>Clustering (approximate search) kicks in once the index holds at least this many
    /// vectors. Below it every search is an exact scan, which is fast at these sizes anyway.</summary>
    public int MinVectorsForClustering { get; set; } = 16_384;
    /// <summary>Aimed-for cluster size. The centroid count is the vector count divided by this,
    /// clamped to [16, <see cref="MaxClusters"/>].</summary>
    public int TargetVectorsPerCluster { get; set; } = 512;
    /// <summary>Upper bound on the number of centroids, which bounds training time.</summary>
    public int MaxClusters { get; set; } = 4096;
    /// <summary>A flush that leaves this many segment files or more triggers a merge of all of them.</summary>
    public int MaxSegments { get; set; } = 8;
    /// <summary>Lloyd iterations for the k-means centroid training.</summary>
    public int KMeansIterations { get; set; } = 6;
    /// <summary>Upper bound on the number of vectors sampled for centroid training.</summary>
    public int KMeansMaxSamples { get; set; } = 60_000;
    /// <summary>Centroids are retrained (and all segments rewritten) when the index has grown past
    /// the trained size times this factor, keeping cluster quality in step with the data.</summary>
    public float RetrainGrowthFactor { get; set; } = 4f;
}
