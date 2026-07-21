using System.Collections.Concurrent;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Thread-safe page cache with a configurable byte budget.
/// Reads are lock-free (a <see cref="ConcurrentDictionary{TKey,TValue}"/> lookup plus an
/// unsynchronized touch flag). Eviction uses a second-chance sweep and only ever runs on
/// one thread at a time; because pages in a copy-on-write tree are immutable, a cached
/// page never needs write-back and can be dropped at any moment.
/// The entry count is tracked with an atomic counter: ConcurrentDictionary.Count acquires
/// every internal lock, which would serialize concurrent readers on the miss path.
/// </summary>
internal sealed class PageCache
{
    private sealed class Entry(byte[] page)
    {
        public readonly byte[] Page = page;
        public bool Touched = true;
    }

    private readonly ConcurrentDictionary<uint, Entry> _map = new();
    private readonly object _evictLock = new();
    private int _count;

    public int Capacity { get; }

    public PageCache(long budgetBytes, int pageSize)
    {
        Capacity = (int)Math.Max(16, budgetBytes / pageSize);
    }

    public byte[]? TryGet(uint pageId)
    {
        if (_map.TryGetValue(pageId, out var e))
        {
            if (!e.Touched)
                e.Touched = true; // write only on transition: a hot page stays read-only for other cores
            return e.Page;
        }
        return null;
    }

    public void Add(uint pageId, byte[] page)
    {
        if (!_map.TryAdd(pageId, new Entry(page)))
            return; // already cached (benign concurrent load of the same immutable page)
        if (Interlocked.Increment(ref _count) > Capacity)
            Evict();
    }

    /// <summary>Must be called when a freed page id is reallocated with new content.</summary>
    public void Invalidate(uint pageId)
    {
        if (_map.TryRemove(pageId, out _))
            Interlocked.Decrement(ref _count);
    }

    public void Clear()
    {
        foreach (var kv in _map)
        {
            if (((ICollection<KeyValuePair<uint, Entry>>)_map).Remove(kv))
                Interlocked.Decrement(ref _count);
        }
    }

    private void Evict()
    {
        if (!Monitor.TryEnter(_evictLock))
            return; // someone else is already sweeping
        try
        {
            int target = Capacity - Capacity / 8; // free ~12.5% headroom per sweep
            // Two passes: first drop untouched entries, then (if still over) touched ones.
            for (int pass = 0; pass < 2 && Volatile.Read(ref _count) > target; pass++)
            {
                foreach (var kv in _map)
                {
                    if (Volatile.Read(ref _count) <= target)
                        break;
                    if (kv.Value.Touched && pass == 0)
                        kv.Value.Touched = false; // second chance
                    else if (_map.TryRemove(kv.Key, out _))
                        Interlocked.Decrement(ref _count);
                }
            }
        }
        finally
        {
            Monitor.Exit(_evictLock);
        }
    }
}
