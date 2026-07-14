using System.Buffers.Binary;
using KvStore.Serialization;

namespace KvStore.BTree;

/// <summary>
/// Orders two encoded keys. The B+tree never interprets key bytes itself; it defers every
/// ordering decision (binary search, child selection, range bounds) to this strategy, which is
/// the one place key semantics live below the public API.
///
/// <para>Implementations are <b>structs</b> and flow through the engine as a generic type
/// parameter (<c>TCmp : struct, IByteKeyComparer</c>) rather than as an interface reference, so
/// the JIT specialises the search code per comparer and inlines <see cref="Compare"/> — a binary
/// search makes ~log2(entries) comparisons per node, which is too hot for interface dispatch.</para>
/// </summary>
internal interface IByteKeyComparer
{
    int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    /// <summary>
    /// Optional monotone rank of an encoded key, enabling interpolation search inside nodes:
    /// implementations must guarantee <c>rank(a) &lt; rank(b)</c> implies <c>Compare(a, b) &lt; 0</c>
    /// (ties in rank carry no ordering information, so a coarse rank — e.g. a fixed-length prefix —
    /// is fine). Return false when no such rank exists; searches then stay purely binary.
    /// Implement explicitly on every struct — a default interface method would box the struct
    /// comparer on each call.
    /// </summary>
    bool TryRank(ReadOnlySpan<byte> key, out ulong rank);
}

/// <summary>Raw unsigned-byte (memcmp) ordering — the fast path for the byte[]/byte[] store.</summary>
internal readonly struct RawByteComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);

    // Rank = the first eight bytes, big-endian, zero-padded: exactly memcmp order on that prefix,
    // and keys sharing the prefix tie — which the rank contract allows.
    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        if (key.Length >= 8)
        {
            rank = BinaryPrimitives.ReadUInt64BigEndian(key);
            return true;
        }
        ulong r = 0;
        for (int i = 0; i < key.Length; i++) r = (r << 8) | key[i];
        rank = r << (8 * (8 - key.Length));
        return true;
    }
}

/// <summary>
/// Compares encoded keys through an <see cref="IKeyCodec{TKey}"/>. This is what lets a generic
/// <c>Database&lt;TKey,TValue&gt;</c> order by the key's own semantics rather than by its byte
/// layout. Routes through <see cref="IKeyCodec{TKey}.CompareEncoded"/>, so built-in codecs compare
/// straight off the bytes; custom codecs fall back to decode-then-Compare unless they override it.
/// </summary>
internal readonly struct CodecKeyComparer<TKey> : IByteKeyComparer
{
    private readonly IKeyCodec<TKey> _codec;
    public CodecKeyComparer(IKeyCodec<TKey> codec) => _codec = codec;
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => _codec.CompareEncoded(a, b);

    // A custom codec's ordering is opaque — no rank, searches stay binary.
    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = 0;
        return false;
    }
}

// ---- Specialised comparers for the built-in codecs ------------------------
//
// CodecKeyComparer's Compare is an interface call per comparison (~log2(n) of them per lookup),
// which the JIT cannot devirtualise through the IKeyCodec reference. For the built-in codecs the
// encoded ordering is a fixed, known function of the bytes, so StorageEngine.GetTable(name, codec)
// maps each built-in codec to one of these structs instead: the tree is then specialised for it
// and every comparison inlines to a couple of loads and a compare. Each struct must order exactly
// like the matching codec's CompareEncoded.

/// <summary>Single unsigned byte (bool, byte codecs).</summary>
internal readonly struct UInt8KeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a[0].CompareTo(b[0]);

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = key[0];
        return true;
    }
}

/// <summary>Single signed byte (sbyte codec).</summary>
internal readonly struct Int8KeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => ((sbyte)a[0]).CompareTo((sbyte)b[0]);

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = (byte)(key[0] ^ 0x80); // flip the sign bit: order-preserving signed->unsigned map
        return true;
    }
}

/// <summary>Little-endian signed 16-bit (short codec).</summary>
internal readonly struct Int16LeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadInt16LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt16LittleEndian(b));

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(key) ^ 0x8000);
        return true;
    }
}

/// <summary>Little-endian unsigned 16-bit (ushort codec).</summary>
internal readonly struct UInt16LeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadUInt16LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(b));

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = BinaryPrimitives.ReadUInt16LittleEndian(key);
        return true;
    }
}

/// <summary>Little-endian signed 32-bit (int codec).</summary>
internal readonly struct Int32LeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt32LittleEndian(b));

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = BinaryPrimitives.ReadUInt32LittleEndian(key) ^ 0x8000_0000u;
        return true;
    }
}

/// <summary>Little-endian unsigned 32-bit (uint codec).</summary>
internal readonly struct UInt32LeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadUInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(b));

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = BinaryPrimitives.ReadUInt32LittleEndian(key);
        return true;
    }
}

/// <summary>Little-endian signed 64-bit (long and DateTime-ticks codecs).</summary>
internal readonly struct Int64LeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(b));

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = BinaryPrimitives.ReadUInt64LittleEndian(key) ^ 0x8000_0000_0000_0000ul;
        return true;
    }
}

/// <summary>Little-endian unsigned 64-bit (ulong codec).</summary>
internal readonly struct UInt64LeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadUInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b));

    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = BinaryPrimitives.ReadUInt64LittleEndian(key);
        return true;
    }
}

/// <summary>Little-endian float (float codec; CompareTo gives the codec's NaN ordering).</summary>
internal readonly struct SingleLeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadSingleLittleEndian(a).CompareTo(BinaryPrimitives.ReadSingleLittleEndian(b));

    // No rank: an IEEE-bits rank would order NaN above +inf, but CompareTo orders NaN below all.
    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = 0;
        return false;
    }
}

/// <summary>Little-endian double (double codec; CompareTo gives the codec's NaN ordering).</summary>
internal readonly struct DoubleLeKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => BinaryPrimitives.ReadDoubleLittleEndian(a).CompareTo(BinaryPrimitives.ReadDoubleLittleEndian(b));

    // No rank: an IEEE-bits rank would order NaN above +inf, but CompareTo orders NaN below all.
    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = 0;
        return false;
    }
}

/// <summary>Guid in ToByteArray layout (Guid codec).</summary>
internal readonly struct GuidKeyComparer : IByteKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => new Guid(a).CompareTo(new Guid(b));

    // No rank: Guid.CompareTo's field-by-field order doesn't map to a simple byte prefix.
    public bool TryRank(ReadOnlySpan<byte> key, out ulong rank)
    {
        rank = 0;
        return false;
    }
}
