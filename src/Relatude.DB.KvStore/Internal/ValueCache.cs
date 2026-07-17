using System.Collections.Concurrent;

namespace SuperFastIndex.Internal;

/// <summary>
/// Bounded cache of decoded values keyed by id, fronting <c>GetValue</c> tree descents.
/// Each entry is tagged with the txid of the snapshot it was read from; a hit requires
/// entry.TxId &lt;= the reader's snapshot, and every commit evicts the ids it touched
/// (under the commit lock, after the new snapshot is published), so a surviving entry is
/// the live value for every snapshot its tag admits. A populate that races a commit is
/// undone by the caller re-checking the committed txid after <see cref="TryAdd"/> and
/// calling <see cref="RemoveIfFrom"/>. Eviction mirrors <see cref="PageCache"/>:
/// an atomic count plus a single-threaded second-chance sweep.
/// </summary>
internal sealed class ValueCache<T>(int capacity)
{
    private sealed class Entry(long txId, T value)
    {
        public readonly long TxId = txId;
        public readonly T Value = value;
        public bool Touched = true;
    }

    private readonly ConcurrentDictionary<int, Entry> _map = new();
    private readonly object _evictLock = new();
    private int _count;

    public bool TryGet(int id, long snapshotTxId, out T value)
    {
        if (_map.TryGetValue(id, out var e) && e.TxId <= snapshotTxId)
        {
            e.Touched = true;
            value = e.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Caches id → value as read at snapshot <paramref name="txId"/>; false if an entry is already present.</summary>
    public bool TryAdd(int id, long txId, T value)
    {
        if (!_map.TryAdd(id, new Entry(txId, value)))
            return false;
        if (Interlocked.Increment(ref _count) > capacity)
            Evict();
        return true;
    }

    /// <summary>
    /// Undoes a <see cref="TryAdd"/> that raced a commit: removes the entry only if it still
    /// carries <paramref name="txId"/> (an equal-tag entry from another reader holds the same
    /// value, so removing it merely costs a future miss).
    /// </summary>
    public void RemoveIfFrom(int id, long txId)
    {
        if (_map.TryGetValue(id, out var e) && e.TxId == txId)
            RemovePair(id, e);
    }

    public void Remove(int id)
    {
        if (_map.TryRemove(id, out _))
            Interlocked.Decrement(ref _count);
    }

    public void Clear()
    {
        foreach (var kv in _map)
            RemovePair(kv.Key, kv.Value);
    }

    private void RemovePair(int id, Entry e)
    {
        if (((ICollection<KeyValuePair<int, Entry>>)_map).Remove(new KeyValuePair<int, Entry>(id, e)))
            Interlocked.Decrement(ref _count);
    }

    private void Evict()
    {
        if (!Monitor.TryEnter(_evictLock))
            return; // someone else is already sweeping
        try
        {
            int target = capacity - capacity / 8; // free ~12.5% headroom per sweep
            for (int pass = 0; pass < 2 && Volatile.Read(ref _count) > target; pass++)
            {
                foreach (var kv in _map)
                {
                    if (Volatile.Read(ref _count) <= target)
                        break;
                    if (kv.Value.Touched && pass == 0)
                        kv.Value.Touched = false; // second chance
                    else
                        RemovePair(kv.Key, kv.Value);
                }
            }
        }
        finally
        {
            Monitor.Exit(_evictLock);
        }
    }
}
