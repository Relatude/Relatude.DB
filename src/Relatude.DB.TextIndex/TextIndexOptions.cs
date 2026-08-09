namespace Relatude.DB.DataStores.Indexes;

/// <summary>Tuning knobs for <see cref="TextIndexEngine"/> and the word indexes it serves.</summary>
public sealed class TextIndexOptions {
    /// <summary>
    /// Upper bound on the memory the engine may use for caching, shared by every word index it
    /// owns. The cache holds decoded term-dictionary blocks and per-word merged postings lists —
    /// everything else (per-document word counts and the un-flushed write buffer) is O(documents),
    /// not O(text). Lower it to run large indexes in little memory; searches then read more from disk.
    /// </summary>
    public long MaxCacheBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>
    /// Write-buffer size that must accumulate before a WAL flush also flushes this index to disk.
    /// 0 (the default) persists on every <see cref="IIndexEngine.MakeDurable"/>, like the other
    /// persisted engines. A larger value batches small transactions into fewer, bigger segments;
    /// the index then reports the older durable position and a crash is repaired by the normal WAL
    /// replay. Shutdown, log rewrites and explicit optimize always flush regardless.
    /// </summary>
    public long MemTableFlushThresholdBytes { get; set; } = 0;
    /// <summary>Terms per prefix-compressed dictionary block; one first-term per block is kept in
    /// memory as the skip level lookups binary-search before decoding a single block.</summary>
    public int TermsPerBlock { get; set; } = 64;
    /// <summary>
    /// Hard cap on live segments per index: reaching it triggers a full merge. Rarely hit — after
    /// every flush, adjacent segments of similar size are merged into a size ladder, which keeps
    /// the count logarithmic in index size on its own.
    /// </summary>
    public int MergeSegmentThreshold { get; set; } = 48;
}
