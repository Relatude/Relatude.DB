using System.Runtime.InteropServices;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Tracks the transaction ids of in-flight snapshot readers so the writer knows which
/// freed pages are still referenced by an active snapshot and must not be recycled yet.
/// Registration is a single CAS on a padded slot array; no locks on the read path.
/// </summary>
internal sealed class ReaderTable
{
    private const long Free = long.MaxValue;
    private const int SlotCount = 64;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedSlot
    {
        [FieldOffset(0)] public long TxId;
    }

    private readonly PaddedSlot[] _slots = new PaddedSlot[SlotCount];

    public ReaderTable()
    {
        for (int i = 0; i < SlotCount; i++)
            _slots[i].TxId = Free;
    }

    /// <summary>
    /// Claims a slot pinned at <paramref name="txId"/>; returns the slot index.
    /// Must be called BEFORE capturing the snapshot: any commit that recycles pages
    /// scans this table afterwards, so a published (older-or-equal) txid conservatively
    /// protects every page the snapshot can reach.
    /// </summary>
    public int Acquire(long txId)
    {
        var slots = _slots;
        int start = Environment.CurrentManagedThreadId & (SlotCount - 1);
        while (true)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                int idx = (start + i) & (SlotCount - 1);
                if (Volatile.Read(ref slots[idx].TxId) == Free &&
                    Interlocked.CompareExchange(ref slots[idx].TxId, txId, Free) == Free)
                {
                    return idx;
                }
            }
            Thread.Yield(); // extremely rare: > 64 concurrent enumerations in flight
        }
    }

    public void Release(int slot) => Volatile.Write(ref _slots[slot].TxId, Free);

    /// <summary>Oldest pinned reader txid, or <see cref="long.MaxValue"/> when none are active.</summary>
    public long MinActiveTxId()
    {
        long min = Free;
        var slots = _slots;
        for (int i = 0; i < SlotCount; i++)
        {
            long v = Volatile.Read(ref slots[i].TxId);
            if (v < min) min = v;
        }
        return min;
    }
}
