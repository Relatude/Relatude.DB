using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>Non-generic access to the id encoding, for the paths that only have the persisted <see cref="IdCodec{TId}.Kind"/> to go by (a catalog entry of an index nobody opened).</summary>
internal static class IdCodec
{
    /// <summary>Encoded id size for a catalog id kind (see <see cref="IdCodec{TId}.Kind"/>).</summary>
    public static int SizeOfKind(byte kind) => kind switch
    {
        0 => 4,  // int
        1 => 8,  // ulong
        2 => 16, // Guid
        _ => throw new InvalidDataException($"Unknown index id kind {kind}. The file is corrupt or was written by a newer version."),
    };
}

/// <summary>
/// Order-preserving, fixed-size binary encoding for index ids (the key side of
/// <see cref="ISortedDictionaryIndex{K,T}"/>): int, ulong or Guid. Like <see cref="IKeyCodec{T}"/>
/// encodings, byte-wise unsigned comparison of two encoded ids equals their logical order
/// (signed order for int, numeric for ulong, <see cref="Guid.CompareTo(Guid)"/> order for Guid —
/// RFC 4122 big-endian bytes compare exactly like Guid's unsigned field comparison).
/// The fixed size is what lets composite (value, id) keys be split without a length marker.
/// Every member JIT-folds per instantiation: the typeof checks are free and the casts are bitcasts.
/// </summary>
internal static class IdCodec<TId> where TId : unmanaged
{
    /// <summary>Encoded size in bytes: 4 (int), 8 (ulong) or 16 (Guid).</summary>
    public static readonly int Size = ComputeSize();

    /// <summary>Stable per-id-type tag persisted in the catalog to detect id-type mismatches on reopen. int is 0 so catalogs written before ulong/Guid ids existed read back correctly.</summary>
    public static readonly byte Kind = ComputeKind();

    private static int ComputeSize()
    {
        if (typeof(TId) == typeof(int)) return 4;
        if (typeof(TId) == typeof(ulong)) return 8;
        if (typeof(TId) == typeof(Guid)) return 16;
        throw new NotSupportedException($"Type '{typeof(TId)}' is not supported as an index id. Supported: int, ulong, Guid.");
    }

    private static byte ComputeKind()
    {
        if (typeof(TId) == typeof(int)) return 0;
        if (typeof(TId) == typeof(ulong)) return 1;
        return 2; // Guid; ComputeSize already rejected everything else
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> dst, TId id)
    {
        if (typeof(TId) == typeof(int))
            BinaryPrimitives.WriteUInt32BigEndian(dst, Unsafe.BitCast<TId, uint>(id) ^ 0x8000_0000u); // sign flip: unsigned byte order = signed order
        else if (typeof(TId) == typeof(ulong))
            BinaryPrimitives.WriteUInt64BigEndian(dst, Unsafe.BitCast<TId, ulong>(id));
        else
            Unsafe.BitCast<TId, Guid>(id).TryWriteBytes(dst, bigEndian: true, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TId Decode(ReadOnlySpan<byte> src)
    {
        if (typeof(TId) == typeof(int))
            return Unsafe.BitCast<uint, TId>(BinaryPrimitives.ReadUInt32BigEndian(src) ^ 0x8000_0000u);
        if (typeof(TId) == typeof(ulong))
            return Unsafe.BitCast<ulong, TId>(BinaryPrimitives.ReadUInt64BigEndian(src));
        return Unsafe.BitCast<Guid, TId>(new Guid(src, bigEndian: true));
    }

    /// <summary>
    /// 64-bit hash used to place an id in a <see cref="HashIndex{TId,T}"/> bucket (low bits pick
    /// the directory slot, high bits form the in-page tag), so every bit has to be well mixed.
    /// The int and ulong paths are the SplitMix64 finalizer, a bijection on 64 bits: distinct
    /// int/ulong ids can never collide, which is what guarantees a full bucket always splits.
    /// Guid folds 128 bits into 64, where collisions are possible but vanishingly rare.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(TId id)
    {
        if (typeof(TId) == typeof(int))
            return Mix(Unsafe.BitCast<TId, uint>(id));
        if (typeof(TId) == typeof(ulong))
            return Mix(Unsafe.BitCast<TId, ulong>(id));
        Guid g = Unsafe.BitCast<TId, Guid>(id);
        ref byte b = ref Unsafe.As<Guid, byte>(ref g);
        return Mix(Unsafe.ReadUnaligned<ulong>(ref b) ^ Mix(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong x)
    {
        x ^= x >> 30;
        x *= 0xbf58476d1ce4e5b9ul;
        x ^= x >> 27;
        x *= 0x94d049bb133111ebul;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// Hash used to address a <see cref="ValueCache{TId,T}"/> slot. For int it is the id itself
    /// (dense ids then map to distinct slots, as the cache always assumed); for the wider types it
    /// folds the bits so nearby ids still spread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SlotHash(TId id)
    {
        if (typeof(TId) == typeof(int))
            return Unsafe.BitCast<TId, int>(id);
        if (typeof(TId) == typeof(ulong))
        {
            ulong v = Unsafe.BitCast<TId, ulong>(id);
            return (int)v ^ (int)(v >> 32);
        }
        return id.GetHashCode();
    }
}
