using System.Buffers.Binary;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Spill pages for values that do not fit a bucket cell, and the operations over a chain of them.
/// A slotted page can only hold values small enough that a handful of cells still share it (see
/// <see cref="HashPage.MaxInlineValueSize"/>); anything larger is written to a private chain of
/// pages and the cell keeps an 8-byte reference instead:
/// <code>
/// [0..4] next page id (u32, 0 = last page of the chain)
/// [4..6] payload bytes in this page (u16, ≤ Capacity)
/// [6..8] reserved (0)
/// [8..]  payload
/// </code>
/// <para>
/// A chain is private to the one cell that references it, which is what keeps this simple: chains
/// are written whole, read whole and freed whole, and are never edited in place or shared between
/// entries. A cell moving between pages (a bucket copy or a split) carries the reference bytes
/// along and leaves the chain untouched; replacing or removing the entry writes a fresh chain and
/// frees the old one through the transaction, so pinned readers keep reaching the pages their
/// snapshot references (the pager's recycle gate holds them until no reader can).
/// </para>
/// </summary>
internal static class OverflowStore
{
    public const int HeaderSize = 8;

    /// <summary>Payload bytes one chain page carries.</summary>
    public const int Capacity = Pager.PageSize - HeaderSize;

    /// <summary>Pages a payload of <paramref name="length"/> bytes occupies.</summary>
    public static int PageCountFor(int length) => (length + Capacity - 1) / Capacity;

    /// <summary>
    /// Writes <paramref name="payload"/> (never empty — an empty value always fits inline) as a
    /// fresh chain and returns its first page. Pages are allocated up front so each can name its
    /// successor; nothing references them until the caller stores the reference in a cell, so a
    /// failure before that leaves them to the transaction's rollback.
    /// </summary>
    public static uint Write(IWritePageSource txn, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            throw new ArgumentException("An empty value is always stored inline; a chain must hold at least one byte.", nameof(payload));
        int pageCount = PageCountFor(payload.Length);
        var ids = new uint[pageCount];
        var pages = new byte[pageCount][];
        for (int i = 0; i < pageCount; i++)
            (ids[i], pages[i]) = txn.Allocate();

        int written = 0;
        for (int i = 0; i < pageCount; i++)
        {
            byte[] page = pages[i];
            int len = Math.Min(Capacity, payload.Length - written);
            BinaryPrimitives.WriteUInt32LittleEndian(page, i + 1 < pageCount ? ids[i + 1] : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(4), (ushort)len);
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(6), 0);
            payload.Slice(written, len).CopyTo(page.AsSpan(HeaderSize));
            // Pages come back uninitialized: the tail after the payload is never read (the length
            // above bounds every read), so it is deliberately left as it is.
            written += len;
        }
        return ids[0];
    }

    /// <summary>Materializes the chain starting at <paramref name="firstPage"/>, whose payload is <paramref name="totalLength"/> bytes.</summary>
    public static byte[] Read(IPageSource src, uint firstPage, int totalLength)
    {
        var result = GC.AllocateUninitializedArray<byte>(totalLength); // fully overwritten below or we throw
        int written = 0;
        uint pageId = firstPage;
        while (pageId != 0)
        {
            byte[] page = src.GetPage(pageId);
            int len = ChunkLength(page, firstPage);
            if (written + len > totalLength)
                throw Corrupt(firstPage, totalLength);
            page.AsSpan(HeaderSize, len).CopyTo(result.AsSpan(written));
            written += len;
            pageId = BinaryPrimitives.ReadUInt32LittleEndian(page);
        }
        if (written != totalLength)
            throw Corrupt(firstPage, totalLength);
        return result;
    }

    /// <summary>
    /// Compares the chain against <paramref name="candidate"/> without materializing it — the
    /// "this id already maps to this value" check on every write, which must not allocate a copy
    /// of a multi-page value just to discover nothing changed.
    /// </summary>
    public static bool PayloadEquals(IPageSource src, uint firstPage, int totalLength, ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length != totalLength)
            return false;
        int compared = 0;
        uint pageId = firstPage;
        while (pageId != 0)
        {
            byte[] page = src.GetPage(pageId);
            int len = ChunkLength(page, firstPage);
            if (compared + len > totalLength)
                throw Corrupt(firstPage, totalLength);
            if (!page.AsSpan(HeaderSize, len).SequenceEqual(candidate.Slice(compared, len)))
                return false;
            compared += len;
            pageId = BinaryPrimitives.ReadUInt32LittleEndian(page);
        }
        return compared == totalLength;
    }

    /// <summary>
    /// Frees every page of the chain starting at <paramref name="firstPage"/>. Each page's
    /// successor is read before the page is freed: a page allocated by this same transaction is
    /// recycled immediately and may be handed to the next allocation.
    /// </summary>
    public static void Free(IWritePageSource txn, uint firstPage)
    {
        uint pageId = firstPage;
        while (pageId != 0)
        {
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(txn.GetPage(pageId));
            txn.Free(pageId);
            pageId = next;
        }
    }

    private static int ChunkLength(byte[] page, uint firstPage)
    {
        int len = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(4));
        if (len == 0 || len > Capacity)
            throw new InvalidDataException($"Overflow chain at page {firstPage} is corrupt: a chain page claims {len} payload bytes (1..{Capacity} expected).");
        return len;
    }

    private static InvalidDataException Corrupt(uint firstPage, int totalLength)
        => new($"Overflow chain at page {firstPage} does not hold the {totalLength} bytes its entry claims. The file is corrupt.");
}
