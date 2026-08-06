using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Bucket page of the extendible-hash index — a slotted page like <see cref="NodePage"/>, minus
/// every ordering obligation:
/// <code>
/// [0]    type: 3 = hash bucket
/// [1]    local depth: how many low hash bits address this bucket
/// [2..4] cell count (u16)
/// [4..6] cellStart: lowest byte offset used by the cell heap, grows downward (u16)
/// [6..8] fragmented (reclaimable-by-compaction) bytes inside the cell heap (u16)
/// [16..] slot array: [tag:u16][offset:u16] per entry, in insertion order
/// </code>
/// Cell: <c>[valLen:u16][id][value]</c>, with the id occupying a fixed <c>idSize</c> bytes
/// (4, 8 or 16 — see <see cref="IdCodec{TId}"/>), so cells need no key length.
/// <para>
/// Slots are unsorted, so a lookup is a linear scan — but only over the tags, 4 bytes apart and
/// contiguous from the header: the 16 high hash bits filter out virtually every entry before the
/// id itself is touched. That keeps a full bucket to a couple of cache lines of scanning and one
/// comparison, which is cheaper than the binary search a sorted page would need.
/// </para>
/// </summary>
internal static class HashPage
{
    public const int PageSize = Pager.PageSize;
    public const int HeaderSize = 16;
    public const int SlotSize = 4;
    public const byte TypeHashBucket = 3;

    /// <summary>Largest encoded value a bucket accepts; three max-size cells still fit one page, so a split can always separate two entries.</summary>
    public const int MaxValueSize = NodePage.MaxValueSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LocalDepth(byte[] p) => p[1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCount(byte[] p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CellStart(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCellStart(byte[] p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Frag(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetFrag(byte[] p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)v);

    public static void Init(byte[] p, int localDepth)
    {
        Array.Clear(p, 0, HeaderSize);
        p[0] = TypeHashBucket;
        p[1] = (byte)localDepth;
        SetCellStart(p, PageSize);
    }

    public static void SetLocalDepth(byte[] p, int localDepth) => p[1] = (byte)localDepth;

    public static int CellSize(int idSize, int valueLength) => 2 + idSize + valueLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellOffset(byte[] p, int i)
        => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(HeaderSize + SlotSize * i + 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> Key(byte[] p, int i, int idSize) => p.AsSpan(CellOffset(p, i) + 2, idSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> Value(byte[] p, int i, int idSize)
    {
        int off = CellOffset(p, i);
        return p.AsSpan(off + 2 + idSize, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off)));
    }

    public static int CellSizeAt(byte[] p, int i, int idSize)
    {
        int off = CellOffset(p, i);
        return CellSize(idSize, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off)));
    }

    /// <summary>
    /// Slot index holding <paramref name="key"/>, or -1. <paramref name="tag"/> rejects almost
    /// every mismatch without reading a cell. The slot array is scanned vectorized where the
    /// hardware allows: tags sit in the even 16-bit lanes of the slot array, so one 256-bit
    /// compare filters 8 slots (their odd lanes — cell offsets — are masked out of the match
    /// bits), which matters because a full bucket holds a few hundred slots and this scan runs
    /// on every lookup and every write.
    /// </summary>
    public static int Find(byte[] p, ushort tag, ReadOnlySpan<byte> key)
    {
        int count = Count(p);
        int i = 0;
        ref byte page = ref MemoryMarshal.GetArrayDataReference(p);

        if (Vector256.IsHardwareAccelerated && count >= 16)
        {
            var target = Vector256.Create(tag);
            for (int last = count - 16; i <= last; i += 16)
            {
                // 16 slots = 64 bytes = two 256-bit loads; even lanes are tags.
                ref ushort lanes = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref page, HeaderSize + SlotSize * i));
                uint m0 = Vector256.Equals(Vector256.LoadUnsafe(ref lanes), target).ExtractMostSignificantBits();
                uint m1 = Vector256.Equals(Vector256.LoadUnsafe(ref lanes, 16), target).ExtractMostSignificantBits();
                uint m = (m0 | m1 << 16) & 0x5555_5555u;
                while (m != 0)
                {
                    int slot = i + (BitOperations.TrailingZeroCount(m) >> 1);
                    int off = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(HeaderSize + SlotSize * slot + 2));
                    if (p.AsSpan(off + 2, key.Length).SequenceEqual(key))
                        return slot;
                    m &= m - 1;
                }
            }
        }

        for (; i < count; i++)
        {
            uint slot = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref page, HeaderSize + SlotSize * i));
            if ((ushort)slot != tag) // little-endian: low 16 bits are the tag, high 16 the offset
                continue;
            int off = (int)(slot >> 16);
            if (p.AsSpan(off + 2, key.Length).SequenceEqual(key))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// True if a cell of <paramref name="cellSize"/> bytes would fit, counting space that a pending
    /// removal is about to release (<paramref name="freedCells"/> slots and <paramref name="freedBytes"/>
    /// of heap). Answers for the read-only page, so the caller can decide to split before copying it.
    /// </summary>
    public static bool CanFit(byte[] p, int cellSize, int freedCells = 0, int freedBytes = 0)
    {
        int count = Count(p) - freedCells;
        int contiguous = CellStart(p) - (HeaderSize + SlotSize * count);
        return contiguous + Frag(p) + freedBytes >= cellSize + SlotSize;
    }

    /// <summary>Appends a cell (order is irrelevant here), compacting the heap if that is what it takes.</summary>
    public static bool TryInsert(byte[] p, ushort tag, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, int idSize)
    {
        int cellSize = CellSize(idSize, value.Length);
        int count = Count(p);
        int contiguous = CellStart(p) - (HeaderSize + SlotSize * count);
        int need = cellSize + SlotSize;
        if (contiguous < need)
        {
            if (contiguous + Frag(p) < need)
                return false;
            Compact(p, idSize);
        }

        int off = CellStart(p) - cellSize;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(off), (ushort)value.Length);
        key.CopyTo(p.AsSpan(off + 2));
        value.CopyTo(p.AsSpan(off + 2 + idSize));

        int slot = HeaderSize + SlotSize * count;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(slot), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(slot + 2), (ushort)off);
        SetCellStart(p, off);
        SetCount(p, count + 1);
        return true;
    }

    /// <summary>Removes slot <paramref name="i"/> by moving the last slot into its place — nothing here is ordered.</summary>
    public static void RemoveAt(byte[] p, int i, int idSize)
    {
        int count = Count(p);
        SetFrag(p, Frag(p) + CellSizeAt(p, i, idSize));
        int last = count - 1;
        if (i != last)
            p.AsSpan(HeaderSize + SlotSize * last, SlotSize).CopyTo(p.AsSpan(HeaderSize + SlotSize * i));
        SetCount(p, last);
    }

    private static void Compact(byte[] p, int idSize)
    {
        Span<byte> tmp = stackalloc byte[PageSize];
        p.CopyTo(tmp);
        int count = Count(p);
        int write = PageSize;
        for (int i = 0; i < count; i++)
        {
            int slot = HeaderSize + SlotSize * i;
            int off = BinaryPrimitives.ReadUInt16LittleEndian(tmp[(slot + 2)..]);
            int size = CellSize(idSize, BinaryPrimitives.ReadUInt16LittleEndian(tmp[off..]));
            write -= size;
            tmp.Slice(off, size).CopyTo(p.AsSpan(write));
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(slot + 2), (ushort)write);
        }
        SetCellStart(p, write);
        SetFrag(p, 0);
    }
}
