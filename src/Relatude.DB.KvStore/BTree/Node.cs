using System.Buffers.Binary;
using KvStore.Paging;

namespace KvStore.BTree;

internal enum NodeType : byte
{
    Internal = 1,
    Leaf = 2,
}

/// <summary>
/// Read/write helpers for a single B+tree node stored in one <see cref="Pager.PageSize"/> page,
/// using a slotted-page layout (SQLite-style): a fixed header, a sorted array of 2-byte slot
/// pointers growing up from the header, and variable-length cells growing down from the end.
///
/// <para>Lookups read cells directly through the slot array. Mutations (insert/delete/split)
/// go through the rebuild path: the caller materialises the entries, edits the list, and calls
/// <see cref="WriteLeaf"/>/<see cref="WriteInternal"/> to re-serialise the page packed. This
/// trades a little per-write CPU for substantially simpler, easier-to-verify code.</para>
///
/// <para>Internal node model: <c>FirstChild</c> (stored in the shared <c>ExtraPtr</c> field) is the
/// subtree for keys below the first separator; each cell pairs a separator key with the child to
/// its right. So with keys k0..k(n-1) the children are FirstChild, cell0.child, ..., cell(n-1).child.
/// Leaf nodes store key/value pairs and use <c>ExtraPtr</c> as the <c>NextLeaf</c> pointer for range
/// scans.</para>
/// </summary>
internal static class Node
{
    public const int HeaderSize = 16;
    private const int SlotSize = 2;
    private const int LeafCellHeader = 4;   // u16 keyLen + u16 valLen
    private const int InternalCellHeader = 6; // u32 childRight + u16 keyLen

    // Header field offsets.
    private const int TypeOffset = 0;        // byte
    private const int CellCountOffset = 2;   // u16
    private const int ContentStartOffset = 4;// u16
    private const int ExtraPtrOffset = 8;    // u32 (NextLeaf for leaf, FirstChild for internal)

    // ---- Header accessors -------------------------------------------------

    public static NodeType GetType(ReadOnlySpan<byte> page) => (NodeType)page[TypeOffset];
    public static bool IsLeaf(ReadOnlySpan<byte> page) => GetType(page) == NodeType.Leaf;

    public static int CellCount(ReadOnlySpan<byte> page)
        => BinaryPrimitives.ReadUInt16LittleEndian(page[CellCountOffset..]);

    private static int ContentStart(ReadOnlySpan<byte> page)
        => BinaryPrimitives.ReadUInt16LittleEndian(page[ContentStartOffset..]);

    public static uint ExtraPtr(ReadOnlySpan<byte> page)
        => BinaryPrimitives.ReadUInt32LittleEndian(page[ExtraPtrOffset..]);

    public static uint NextLeaf(ReadOnlySpan<byte> page) => ExtraPtr(page);
    public static uint FirstChild(ReadOnlySpan<byte> page) => ExtraPtr(page);

    private static void SetType(Span<byte> page, NodeType t) => page[TypeOffset] = (byte)t;
    private static void SetCellCount(Span<byte> page, int n)
        => BinaryPrimitives.WriteUInt16LittleEndian(page[CellCountOffset..], (ushort)n);
    private static void SetContentStart(Span<byte> page, int v)
        => BinaryPrimitives.WriteUInt16LittleEndian(page[ContentStartOffset..], (ushort)v);
    private static void SetExtraPtr(Span<byte> page, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(page[ExtraPtrOffset..], v);

    private static int SlotOffset(ReadOnlySpan<byte> page, int i)
        => BinaryPrimitives.ReadUInt16LittleEndian(page[(HeaderSize + i * SlotSize)..]);

    // ---- Leaf reads -------------------------------------------------------

    public static ReadOnlySpan<byte> LeafKey(ReadOnlySpan<byte> page, int i)
    {
        int o = SlotOffset(page, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[o..]);
        return page.Slice(o + LeafCellHeader, keyLen);
    }

    public static ReadOnlySpan<byte> LeafValue(ReadOnlySpan<byte> page, int i)
    {
        int o = SlotOffset(page, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[o..]);
        int valLen = BinaryPrimitives.ReadUInt16LittleEndian(page[(o + 2)..]);
        return page.Slice(o + LeafCellHeader + keyLen, valLen);
    }

    /// <summary>Reads one leaf entry's key and value spans with a single slot/header decode —
    /// the scan path calls this per entry, where <see cref="LeafKey"/> + <see cref="LeafValue"/>
    /// would decode the cell header twice.</summary>
    public static void LeafEntry(ReadOnlySpan<byte> page, int i,
        out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        int o = SlotOffset(page, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[o..]);
        int valLen = BinaryPrimitives.ReadUInt16LittleEndian(page[(o + 2)..]);
        key = page.Slice(o + LeafCellHeader, keyLen);
        value = page.Slice(o + LeafCellHeader + keyLen, valLen);
    }

    // ---- Internal reads ---------------------------------------------------

    public static ReadOnlySpan<byte> InternalKey(ReadOnlySpan<byte> page, int i)
    {
        int o = SlotOffset(page, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[(o + 4)..]);
        return page.Slice(o + InternalCellHeader, keyLen);
    }

    public static uint InternalChild(ReadOnlySpan<byte> page, int i)
        => BinaryPrimitives.ReadUInt32LittleEndian(page[SlotOffset(page, i)..]);

    /// <summary>
    /// Returns the page id of the child whose subtree may contain <paramref name="key"/>,
    /// ordering separators with <paramref name="cmp"/>.
    /// </summary>
    public static uint ChildForKey<TCmp>(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, TCmp cmp)
        where TCmp : IByteKeyComparer
    {
        int c = UpperBound(page, key, cmp);       // count of separators <= key
        return c == 0 ? FirstChild(page) : InternalChild(page, c - 1);
    }

    /// <summary>
    /// <see cref="ChildForKey"/> that additionally narrows the caller's rank bounds to the chosen
    /// subtree's separators: every key in the returned child's subtree is &gt;= the separator left
    /// of it and &lt; the separator right of it. A bound is only tightened when that separator
    /// exists (the outermost children keep the incoming bound and flag). The separator cells were
    /// just touched by the search, so reading their ranks here is effectively free — this is what
    /// lets the leaf search start from an interpolated slot without cold endpoint reads.
    /// </summary>
    public static uint ChildForKeyWithBounds<TCmp>(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, TCmp cmp,
        ref ulong loRank, ref ulong hiRank, ref bool haveLo, ref bool haveHi)
        where TCmp : IByteKeyComparer
    {
        int c = UpperBound(page, key, cmp);
        if (c > 0 && cmp.TryRank(InternalKey(page, c - 1), out ulong lr))
        {
            loRank = lr;
            haveLo = true;
        }
        if (c < CellCount(page) && cmp.TryRank(InternalKey(page, c), out ulong hr))
        {
            hiRank = hr;
            haveHi = true;
        }
        return c == 0 ? FirstChild(page) : InternalChild(page, c - 1);
    }

    // In-node search is memory-latency bound: each binary-search probe dereferences a cell at an
    // unpredictable offset in a (usually cold) 4 KB page, and the probes are data-dependent, so a
    // lookup pays ~log2(n) serialized cache misses. When the comparer exposes a monotone rank
    // (IByteKeyComparer.TryRank — all fixed-width numeric keys and memcmp byte keys), we first
    // interpolate the slot from the key's position between the node's endpoint keys: uniform or
    // sequential key distributions (ids, timestamps) converge in one or two probes. The rounds are
    // capped, and anything not resolved by then falls back to the plain binary search below, so a
    // skewed distribution costs at most a few extra probes.
    private const int InterpolationMinRange = 8;
    private const int InterpolationMaxRounds = 3;

    /// <summary>The interpolated probe index for a key whose rank sits between the ranks of the
    /// slots at <paramref name="lo"/> and <paramref name="lo"/>+<paramref name="span"/>. The
    /// UInt128 widening is load-bearing: (rank delta) × span overflows 64 bits.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Interpolate(ulong keyRank, ulong loRank, ulong hiRank, int lo, int span)
        => lo + (int)((UInt128)(keyRank - loRank) * (uint)span / (hiRank - loRank));

    /// <summary>The exact-match binary search over leaf slots [<paramref name="lo"/>,
    /// <paramref name="hi"/>]: the slot index of a match, else the bitwise complement of the
    /// insertion point (same convention as Array.BinarySearch).</summary>
    private static int LeafBinarySearch<TCmp>(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, TCmp cmp, int lo, int hi)
        where TCmp : IByteKeyComparer
    {
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int c = cmp.Compare(LeafKey(page, mid), key);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }

    /// <summary>First separator index strictly greater than <paramref name="key"/> (i.e. count of keys &lt;= key).</summary>
    private static int UpperBound<TCmp>(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, TCmp cmp)
        where TCmp : IByteKeyComparer
    {
        int lo = 0, hi = CellCount(page);

        // Invariant throughout: keys[..lo] <= key < keys[hi..].
        if (hi - lo > InterpolationMinRange && cmp.TryRank(key, out ulong kr))
        {
            for (int round = 0; round < InterpolationMaxRounds && hi - lo > InterpolationMinRange; round++)
            {
                cmp.TryRank(InternalKey(page, lo), out ulong lr);
                cmp.TryRank(InternalKey(page, hi - 1), out ulong hr);
                if (kr < lr) return lo;   // key < keys[lo], and keys[..lo] <= key
                if (kr > hr) return hi;   // key > keys[hi-1], and key < keys[hi..]
                if (lr == hr) break;      // no slope to interpolate on
                int mid = Interpolate(kr, lr, hr, lo, hi - 1 - lo);
                if (cmp.Compare(InternalKey(page, mid), key) <= 0) lo = mid + 1;
                else hi = mid;
            }
        }

        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (cmp.Compare(InternalKey(page, mid), key) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Search a leaf. Returns the slot index of an exact match, or the bitwise
    /// complement of the insertion point if absent (same convention as Array.BinarySearch).</summary>
    public static int FindInLeaf<TCmp>(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, TCmp cmp)
        where TCmp : IByteKeyComparer
    {
        int lo = 0, hi = CellCount(page) - 1;

        // Invariant throughout: keys[..lo] < key < keys[hi+1..].
        if (hi - lo > InterpolationMinRange && cmp.TryRank(key, out ulong kr))
        {
            for (int round = 0; round < InterpolationMaxRounds && hi - lo > InterpolationMinRange; round++)
            {
                cmp.TryRank(LeafKey(page, lo), out ulong lr);
                cmp.TryRank(LeafKey(page, hi), out ulong hr);
                if (kr < lr) return ~lo;        // key < keys[lo]: insert at lo
                if (kr > hr) return ~(hi + 1);  // key > keys[hi]: insert after hi
                if (lr == hr) break;            // no slope to interpolate on
                int mid = Interpolate(kr, lr, hr, lo, hi - lo);
                int c = cmp.Compare(LeafKey(page, mid), key);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1;
                else hi = mid - 1;
            }
        }

        return LeafBinarySearch(page, key, cmp, lo, hi);
    }

    /// <summary>
    /// <see cref="FindInLeaf"/> seeded by the subtree's separator rank bounds (from
    /// <see cref="ChildForKeyWithBounds"/>): probes the slot interpolated from the key's position
    /// inside <c>[loRank, hiRank)</c> first and gallops outward from it, so a uniform or sequential
    /// key distribution resolves in a probe or two without ever touching the leaf's endpoint keys.
    /// Galloping is capped; an unresolved bracket finishes with plain binary search. Same result
    /// contract as <see cref="FindInLeaf"/> — ranks only choose probe order, comparisons decide.
    /// </summary>
    public static int FindInLeafSeeded<TCmp>(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, TCmp cmp,
        ulong keyRank, ulong loRank, ulong hiRank)
        where TCmp : IByteKeyComparer
    {
        int n = CellCount(page);
        if (n == 0) return ~0;

        // keyRank ∈ [loRank, hiRank] by the separator contract; a degenerate range (coarse ranks)
        // just seeds the middle, which is where binary search would start anyway.
        int est = hiRank > loRank
            ? Interpolate(keyRank, loRank, hiRank, 0, n - 1)
            : (n - 1) >> 1;

        int c0 = cmp.Compare(LeafKey(page, est), key);
        if (c0 == 0) return est;

        // Bracket the key by galloping away from the estimate. Nearby slots share cache lines with
        // the estimate's, so a good seed stays within a line or two; a bad seed gives up after
        // MaxStep and leaves the rest to binary search.
        const int MaxStep = 8;
        int lo, hi;
        if (c0 < 0)
        {
            lo = est + 1;
            hi = n - 1;
            for (int step = 1; step <= MaxStep; step <<= 1)
            {
                int probe = est + step;
                if (probe > hi) break;
                int c = cmp.Compare(LeafKey(page, probe), key);
                if (c == 0) return probe;
                if (c > 0) { hi = probe - 1; break; }
                lo = probe + 1;
            }
        }
        else
        {
            lo = 0;
            hi = est - 1;
            for (int step = 1; step <= MaxStep; step <<= 1)
            {
                int probe = est - step;
                if (probe < lo) break;
                int c = cmp.Compare(LeafKey(page, probe), key);
                if (c == 0) return probe;
                if (c < 0) { lo = probe + 1; break; }
                hi = probe - 1;
            }
        }

        return LeafBinarySearch(page, key, cmp, lo, hi);
    }

    // ---- Initialisation & serialisation ----------------------------------

    public static void InitLeaf(Span<byte> page)
    {
        page.Clear();
        SetType(page, NodeType.Leaf);
        SetCellCount(page, 0);
        SetContentStart(page, Pager.PageSize);
        SetExtraPtr(page, Pager.NullPage);
    }

    public static int LeafEntrySize(int keyLen, int valLen) => SlotSize + LeafCellHeader + keyLen + valLen;
    public static int InternalEntrySize(int keyLen) => SlotSize + InternalCellHeader + keyLen;

    /// <summary>
    /// Re-serialises a leaf from <paramref name="entries"/> (which must be sorted by key).
    /// Returns false without modifying the page if the entries do not fit.
    /// </summary>
    public static bool WriteLeaf(Span<byte> page, IReadOnlyList<(byte[] key, byte[] val)> entries, uint nextLeaf)
    {
        int needed = HeaderSize;
        foreach (var (k, v) in entries) needed += LeafEntrySize(k.Length, v.Length);
        if (needed > Pager.PageSize) return false;

        page.Clear();
        SetType(page, NodeType.Leaf);
        SetExtraPtr(page, nextLeaf);
        SetCellCount(page, entries.Count);

        int content = Pager.PageSize;
        for (int i = 0; i < entries.Count; i++)
        {
            var (k, v) = entries[i];
            content -= LeafCellHeader + k.Length + v.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(page[content..], (ushort)k.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(page[(content + 2)..], (ushort)v.Length);
            k.CopyTo(page[(content + LeafCellHeader)..]);
            v.CopyTo(page[(content + LeafCellHeader + k.Length)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(page[(HeaderSize + i * SlotSize)..], (ushort)content);
        }
        SetContentStart(page, content);
        return true;
    }

    /// <summary>
    /// Inserts a single new (key, value) cell into a leaf <b>in place</b> at slot index
    /// <paramref name="pos"/>, without rebuilding the page — the fast path for inserting a key that
    /// isn't already present. Returns false (leaving the page untouched) when the entry doesn't fit,
    /// so the caller can fall back to the rebuild-and-split path.
    ///
    /// <para>The new cell is written into the free gap just below the existing content and its slot
    /// pointer is spliced into sorted position; the cells themselves need not stay physically
    /// ordered because every read goes through the slot array. The page stays gap-free (packed),
    /// so the fit check mirrors <see cref="WriteLeaf"/>'s accounting exactly. Use only for keys not
    /// already in the leaf — overwrites must go through the rebuild path so the old cell is reclaimed.</para>
    /// </summary>
    public static bool TryInsertLeafCell(Span<byte> page, int pos, ReadOnlySpan<byte> key, ReadOnlySpan<byte> val)
    {
        int cellCount = CellCount(page);
        int contentStart = ContentStart(page);
        int slotEnd = HeaderSize + cellCount * SlotSize;

        // Adding this entry costs one slot plus the cell; both must fit in the free gap.
        int needed = LeafEntrySize(key.Length, val.Length);
        if (needed > contentStart - slotEnd) return false;

        // Write the new cell just below the current content.
        int cell = contentStart - (LeafCellHeader + key.Length + val.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(page[cell..], (ushort)key.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(page[(cell + 2)..], (ushort)val.Length);
        key.CopyTo(page[(cell + LeafCellHeader)..]);
        val.CopyTo(page[(cell + LeafCellHeader + key.Length)..]);

        // Splice the slot pointer into sorted position: shift slots [pos, cellCount) right by one.
        // Span.CopyTo uses memmove semantics, so the overlapping right-shift is safe.
        int slotPos = HeaderSize + pos * SlotSize;
        int tailBytes = (cellCount - pos) * SlotSize;
        if (tailBytes > 0)
            page.Slice(slotPos, tailBytes).CopyTo(page.Slice(slotPos + SlotSize, tailBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(page[slotPos..], (ushort)cell);

        SetContentStart(page, cell);
        SetCellCount(page, cellCount + 1);
        return true;
    }

    /// <summary>
    /// Overwrites the value of leaf entry <paramref name="i"/> <b>in place</b> when the new value
    /// has the same encoded length — the cell layout is unchanged, so nothing else moves. Returns
    /// false (leaving the page untouched) when the lengths differ; the caller then takes the
    /// rebuild path so the superseded cell is reclaimed rather than orphaned.
    /// </summary>
    public static bool TryReplaceLeafValue(Span<byte> page, int i, ReadOnlySpan<byte> val)
    {
        int o = SlotOffset(page, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[o..]);
        int valLen = BinaryPrimitives.ReadUInt16LittleEndian(page[(o + 2)..]);
        if (valLen != val.Length) return false;
        val.CopyTo(page.Slice(o + LeafCellHeader + keyLen, valLen));
        return true;
    }

    /// <summary>
    /// Re-serialises an internal node from <paramref name="firstChild"/> plus
    /// <paramref name="entries"/> (separator key + right child, sorted by key).
    /// Returns false without modifying the page if the entries do not fit.
    /// </summary>
    public static bool WriteInternal(Span<byte> page, uint firstChild, IReadOnlyList<(byte[] key, uint child)> entries)
    {
        int needed = HeaderSize;
        foreach (var (k, _) in entries) needed += InternalEntrySize(k.Length);
        if (needed > Pager.PageSize) return false;

        page.Clear();
        SetType(page, NodeType.Internal);
        SetExtraPtr(page, firstChild);
        SetCellCount(page, entries.Count);

        int content = Pager.PageSize;
        for (int i = 0; i < entries.Count; i++)
        {
            var (k, child) = entries[i];
            content -= InternalCellHeader + k.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(page[content..], child);
            BinaryPrimitives.WriteUInt16LittleEndian(page[(content + 4)..], (ushort)k.Length);
            k.CopyTo(page[(content + InternalCellHeader)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(page[(HeaderSize + i * SlotSize)..], (ushort)content);
        }
        SetContentStart(page, content);
        return true;
    }

    /// <summary>
    /// Reports the byte ranges of a node page that carry data: the head (header + slot array,
    /// <c>[0, headLen)</c>) and the tail (cell area, <c>[tailStart, PageSize)</c>). The gap
    /// between them is never read — order lives in the slot array, and every cell is reached
    /// through it — so page images can skip the gap. Returns false when the page does not parse
    /// as a well-formed node (e.g. a freed page); the caller must then treat the whole page as used.
    /// </summary>
    public static bool TryGetUsedExtents(ReadOnlySpan<byte> page, out int headLen, out int tailStart)
    {
        headLen = tailStart = 0;
        byte t = page[TypeOffset];
        if (t != (byte)NodeType.Internal && t != (byte)NodeType.Leaf) return false;

        int n = CellCount(page);
        int content = ContentStart(page);
        int head = HeaderSize + n * SlotSize;
        if (head > Pager.PageSize || content < head || content > Pager.PageSize) return false;

        headLen = head;
        tailStart = content;
        return true;
    }

    // ---- Materialisation for the rebuild path -----------------------------

    public static List<(byte[] key, byte[] val)> ReadLeafEntries(ReadOnlySpan<byte> page)
    {
        int n = CellCount(page);
        var list = new List<(byte[], byte[])>(n);
        for (int i = 0; i < n; i++)
            list.Add((LeafKey(page, i).ToArray(), LeafValue(page, i).ToArray()));
        return list;
    }

    public static List<(byte[] key, uint child)> ReadInternalEntries(ReadOnlySpan<byte> page)
    {
        int n = CellCount(page);
        var list = new List<(byte[], uint)>(n);
        for (int i = 0; i < n; i++)
            list.Add((InternalKey(page, i).ToArray(), InternalChild(page, i)));
        return list;
    }
}
