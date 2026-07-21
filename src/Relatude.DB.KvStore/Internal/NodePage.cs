using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Slotted-page B+Tree node layout over a raw 4 KiB page:
/// <code>
/// [0]    type: 1 = branch, 2 = leaf
/// [2..4] cell count (u16)
/// [4..6] cellStart: lowest byte offset used by the cell heap, grows downward (u16)
/// [6..8] fragmented (reclaimable-by-compaction) bytes inside the cell heap (u16)
/// [8..12]  rightmost child page id (branch only, u32)
/// [12..16] entry count of the rightmost child's subtree (branch only, u32)
/// [16..] slot array: u16 cell offsets, sorted by key
/// </code>
/// Leaf cell: <c>[keyLen:u16][valLen:u16][key][value]</c>.
/// Branch cell: <c>[keyLen:u16][child:u32][count:u32][key]</c> — child holds keys &lt; this key
/// and count is the number of leaf entries in its subtree; keys ≥ the last cell key live under
/// the rightmost child. The per-child counts make order-statistic queries (count of keys below
/// a bound) a single descent instead of a leaf scan.
/// </summary>
internal static class NodePage
{
    public const int PageSize = Pager.PageSize;
    public const int HeaderSize = 16;
    public const byte TypeBranch = 1;
    public const byte TypeLeaf = 2;

    // Guarantee that a split can always place any two max-size cells into separate halves.
    public const int MaxKeySize = 1024;
    public const int MaxValueSize = 1024;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeaf(byte[] p) => p[0] == TypeLeaf;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCount(byte[] p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellStart(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCellStart(byte[] p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Frag(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetFrag(byte[] p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)v);

    public static uint Rightmost(byte[] p) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8));
    public static void SetRightmost(byte[] p, uint child) => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), child);

    public static int RightmostCount(byte[] p) => (int)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12));
    public static void SetRightmostCount(byte[] p, int count) => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), (uint)count);

    public static void InitLeaf(byte[] p)
    {
        Array.Clear(p, 0, HeaderSize);
        p[0] = TypeLeaf;
        SetCellStart(p, PageSize);
    }

    public static void InitBranch(byte[] p, uint rightmost)
    {
        Array.Clear(p, 0, HeaderSize);
        p[0] = TypeBranch;
        SetCellStart(p, PageSize);
        SetRightmost(p, rightmost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellOffset(byte[] p, int i)
        => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(HeaderSize + 2 * i));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> GetKey(byte[] p, int i)
    {
        int off = CellOffset(p, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off));
        int dataOff = p[0] == TypeLeaf ? off + 4 : off + 10;
        return p.AsSpan(dataOff, keyLen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> LeafValue(byte[] p, int i)
    {
        int off = CellOffset(p, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off));
        int valLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off + 2));
        return p.AsSpan(off + 4 + keyLen, valLen);
    }

    /// <summary>Key as a Memory slice backed by the page buffer (no copy).</summary>
    public static ReadOnlyMemory<byte> GetKeyMemory(byte[] p, int i)
    {
        int off = CellOffset(p, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off));
        int dataOff = p[0] == TypeLeaf ? off + 4 : off + 10;
        return p.AsMemory(dataOff, keyLen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BranchChild(byte[] p, int i)
        => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(CellOffset(p, i) + 2));

    public static void SetBranchChild(byte[] p, int i, uint child)
        => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(CellOffset(p, i) + 2), child);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BranchCount(byte[] p, int i)
        => (int)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(CellOffset(p, i) + 6));

    public static void SetBranchCount(byte[] p, int i, int count)
        => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(CellOffset(p, i) + 6), (uint)count);

    /// <summary>Child covering index <paramref name="i"/> in the inclusive range [0, Count]; Count maps to the rightmost child.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ChildAt(byte[] p, int i) => i < Count(p) ? BranchChild(p, i) : Rightmost(p);

    /// <summary>Subtree entry count of the child at inclusive index <paramref name="i"/> (see <see cref="ChildAt"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChildCountAt(byte[] p, int i) => i < Count(p) ? BranchCount(p, i) : RightmostCount(p);

    public static void SetChildCountAt(byte[] p, int i, int count)
    {
        if (i < Count(p)) SetBranchCount(p, i, count);
        else SetRightmostCount(p, count);
    }

    private static int CellSizeAt(byte[] p, int i)
    {
        int off = CellOffset(p, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off));
        return p[0] == TypeLeaf
            ? 4 + keyLen + BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off + 2))
            : 10 + keyLen;
    }

    public static int LeafCellSize(int keyLen, int valLen) => 4 + keyLen + valLen;
    public static int BranchCellSize(int keyLen) => 10 + keyLen;

    // ---- search ----

    /// <summary>First index whose key is ≥ <paramref name="key"/>; sets <paramref name="exact"/> on equality. (Leaf lookup.)</summary>
    public static int LowerBound(byte[] p, ReadOnlySpan<byte> key, out bool exact)
    {
        int lo = 0, hi = Count(p);
        exact = false;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            int c = GetKey(p, mid).SequenceCompareTo(key);
            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                if (c == 0) exact = true;
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>First index whose key is &gt; <paramref name="key"/>. (Branch routing: keys equal to a separator belong right.)</summary>
    public static int UpperBound(byte[] p, ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = Count(p);
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetKey(p, mid).SequenceCompareTo(key) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // ---- modification ----

    public static bool TryInsertLeafCell(byte[] p, int pos, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        int cellSize = LeafCellSize(key.Length, value.Length);
        if (!EnsureSpace(p, cellSize))
            return false;
        int off = CellStart(p) - cellSize;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(off), (ushort)key.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(off + 2), (ushort)value.Length);
        key.CopyTo(p.AsSpan(off + 4));
        value.CopyTo(p.AsSpan(off + 4 + key.Length));
        FinishInsert(p, pos, off);
        return true;
    }

    public static bool TryInsertBranchCell(byte[] p, int pos, ReadOnlySpan<byte> key, uint child, int count)
    {
        int cellSize = BranchCellSize(key.Length);
        if (!EnsureSpace(p, cellSize))
            return false;
        int off = CellStart(p) - cellSize;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(off), (ushort)key.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(off + 2), child);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(off + 6), (uint)count);
        key.CopyTo(p.AsSpan(off + 10));
        FinishInsert(p, pos, off);
        return true;
    }

    private static bool EnsureSpace(byte[] p, int cellSize)
    {
        int count = Count(p);
        int contiguous = CellStart(p) - (HeaderSize + 2 * count);
        int need = cellSize + 2;
        if (contiguous >= need)
            return true;
        if (contiguous + Frag(p) < need)
            return false;
        Compact(p);
        return true;
    }

    private static void FinishInsert(byte[] p, int pos, int cellOffset)
    {
        int count = Count(p);
        int slotBase = HeaderSize + 2 * pos;
        p.AsSpan(slotBase, 2 * (count - pos)).CopyTo(p.AsSpan(slotBase + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(slotBase), (ushort)cellOffset);
        SetCellStart(p, cellOffset);
        SetCount(p, count + 1);
    }

    public static void RemoveCell(byte[] p, int pos)
    {
        int count = Count(p);
        SetFrag(p, Frag(p) + CellSizeAt(p, pos));
        int slotBase = HeaderSize + 2 * pos;
        p.AsSpan(slotBase + 2, 2 * (count - pos - 1)).CopyTo(p.AsSpan(slotBase));
        SetCount(p, count - 1);
    }

    private static void Compact(byte[] p)
    {
        Span<byte> tmp = stackalloc byte[PageSize];
        p.CopyTo(tmp);
        int count = Count(p);
        int write = PageSize;
        for (int i = 0; i < count; i++)
        {
            int off = BinaryPrimitives.ReadUInt16LittleEndian(tmp[(HeaderSize + 2 * i)..]);
            int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(tmp[off..]);
            int size = tmp[0] == TypeLeaf
                ? 4 + keyLen + BinaryPrimitives.ReadUInt16LittleEndian(tmp[(off + 2)..])
                : 10 + keyLen;
            write -= size;
            tmp.Slice(off, size).CopyTo(p.AsSpan(write));
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(HeaderSize + 2 * i), (ushort)write);
        }
        SetCellStart(p, write);
        SetFrag(p, 0);
    }
}
