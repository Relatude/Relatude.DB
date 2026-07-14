using KvStore.Paging;

namespace KvStore;

/// <summary>When a transaction's writes are made durable on disk.</summary>
public enum FlushMode
{
    /// <summary>Every commit writes to the WAL and fsyncs before returning — full crash-safety
    /// (the default, and the engine's original behavior).</summary>
    Sync,

    /// <summary>Commits accumulate in memory and a background thread flushes them every
    /// <see cref="DatabaseOptions.FlushDelayMs"/>. Faster, but a crash loses any transaction
    /// committed since the last flush. Use <c>forceFlush</c> on a transaction, or rely on
    /// <c>Dispose</c>, when a specific write must be durable.</summary>
    Async,

    /// <summary>Commits accumulate in memory and become durable only when you call
    /// <see cref="Database.Flush"/> (or pass <c>forceFlush</c> on a transaction, or
    /// <c>Dispose</c>). No background thread runs, so you control exactly when the durability
    /// boundary is — a crash loses any transaction committed since your last flush.</summary>
    Manual,
}

/// <summary>Tunables for a database handle. All fields have sensible defaults.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Durability policy. Defaults to <see cref="FlushMode.Sync"/>.</summary>
    public FlushMode FlushMode { get; set; } = FlushMode.Sync;

    /// <summary>How often the background flusher runs, in milliseconds, when
    /// <see cref="FlushMode"/> is <see cref="FlushMode.Async"/>. Ignored in sync mode.</summary>
    public int FlushDelayMs { get; set; } = 100;

    /// <summary>Maximum size of the in-memory clean-page cache, in bytes. Rounded down to whole
    /// 4 KB pages (with a small floor). Defaults to 16 MB.</summary>
    public long CacheSizeBytes { get; set; } = 16L * 1024 * 1024;

    internal static readonly DatabaseOptions Default = new();

    internal void Validate()
    {
        if (FlushDelayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(FlushDelayMs), "Flush delay cannot be negative.");
        // CacheSizeBytes needs no validation: Pager.Open floors it at MinCachePages.
    }
}
