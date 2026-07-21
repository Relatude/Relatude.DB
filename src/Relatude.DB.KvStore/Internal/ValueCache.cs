using System.Numerics;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Bounded cache of decoded values keyed by id, fronting <c>GetValue</c> tree descents.
/// A lock-free, direct-mapped slot array (capacity rounded up to a power of two): a slot is
/// found by <c>id &amp; mask</c>, holds at most one immutable entry, and is simply overwritten
/// on collision. There is no eviction machinery and no lock anywhere — a miss costs one array
/// read, an insert one small allocation — so a working set larger than the cache degrades to
/// near-free misses instead of thrashing (replacements of a colliding id are sampled to keep
/// allocation churn low under uniform scans).
/// <para>
/// Consistency contract (unchanged): each entry is tagged with the txid of the snapshot it was
/// read from; a hit requires entry.TxId &lt;= the reader's snapshot, and every commit evicts the
/// ids it touched (under the commit lock, after the new snapshot is published), so a surviving
/// entry is the live value for every snapshot its tag admits. A populate that races a commit is
/// undone by the caller re-checking the committed txid after <see cref="TryAdd"/> and calling
/// <see cref="RemoveIfFrom"/>.
/// </para>
/// </summary>
internal sealed class ValueCache<T>(int capacity)
{
    private sealed class Entry(int id, long txId, T value)
    {
        public readonly int Id = id;
        public readonly long TxId = txId;
        public readonly T Value = value;
    }

    private readonly Entry?[] _slots = new Entry?[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, capacity))];
    private int _replaceTick; // racy by design: only samples replacements of colliding ids

    public bool TryGet(int id, long snapshotTxId, out T value)
    {
        Entry? e = Volatile.Read(ref _slots[id & (_slots.Length - 1)]);
        if (e is not null && e.Id == id && e.TxId <= snapshotTxId)
        {
            value = e.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Caches id → value as read at snapshot <paramref name="txId"/>; false if not admitted (slot held by a colliding id that was not sampled for replacement).</summary>
    public bool TryAdd(int id, long txId, T value)
    {
        ref Entry? slot = ref _slots[id & (_slots.Length - 1)];
        Entry? cur = Volatile.Read(ref slot);
        // Replace a DIFFERENT id only 1-in-8 times: a scan over a working set larger than the
        // cache then allocates rarely instead of on every miss, while a genuinely hot id still
        // claims its slot within a few touches and serves hits from then on.
        if (cur is not null && cur.Id != id && (++_replaceTick & 7) != 0)
            return false;
        Volatile.Write(ref slot, new Entry(id, txId, value));
        return true;
    }

    /// <summary>
    /// Undoes a <see cref="TryAdd"/> that raced a commit: clears the slot only if it still holds
    /// this exact (id, txId) entry (an equal-tag entry from another reader holds the same value,
    /// so clearing it merely costs a future miss).
    /// </summary>
    public void RemoveIfFrom(int id, long txId)
    {
        ref Entry? slot = ref _slots[id & (_slots.Length - 1)];
        Entry? e = Volatile.Read(ref slot);
        if (e is not null && e.Id == id && e.TxId == txId)
            Volatile.Write(ref slot, null);
    }

    public void Remove(int id)
    {
        ref Entry? slot = ref _slots[id & (_slots.Length - 1)];
        Entry? e = Volatile.Read(ref slot);
        if (e is not null && e.Id == id)
            Volatile.Write(ref slot, null);
    }

    public void Clear()
    {
        for (int i = 0; i < _slots.Length; i++)
            Volatile.Write(ref _slots[i], null);
    }
}
