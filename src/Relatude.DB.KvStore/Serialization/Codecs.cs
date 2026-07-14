using System.Buffers.Binary;
using System.Text;

namespace KvStore.Serialization;

/// <summary>
/// Converts a value of type <typeparamref name="TValue"/> to and from the <c>byte[]</c>
/// representation the storage engine persists. Values only need to round-trip; their byte
/// encoding has no ordering requirement.
/// </summary>
public interface IValueCodec<TValue>
{
    byte[] Encode(TValue value);
    TValue Decode(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Optional allocation-free variant of <see cref="Encode"/>: writes the encoding into
    /// <paramref name="destination"/> and reports its length. Returning false means "use
    /// <see cref="Encode"/> instead" — either the codec doesn't implement the span path (the
    /// default) or the encoding doesn't fit. When it returns true the written bytes must be
    /// identical to what <see cref="Encode"/> produces.
    /// </summary>
    bool TryEncode(TValue value, Span<byte> destination, out int written)
    {
        written = 0;
        return false;
    }
}

/// <summary>
/// A <see cref="IValueCodec{TKey}"/> that also defines the sort order of keys. The engine
/// stores the encoded bytes but orders entries by <see cref="Compare"/> on the decoded keys,
/// so the byte encoding need not be order-preserving — any round-trippable encoding works.
/// </summary>
public interface IKeyCodec<TKey> : IValueCodec<TKey>
{
    /// <summary>Total order over keys, with the same contract as <see cref="IComparer{T}"/>.</summary>
    int Compare(TKey a, TKey b);

    /// <summary>
    /// Orders two <i>encoded</i> keys. Must agree exactly with decoding both operands and calling
    /// <see cref="Compare"/> — which is what the default implementation does. Built-in codecs
    /// override this to compare straight off the bytes (the engine compares keys on every binary
    /// search step, so skipping the decode is a real saving on hot paths).
    /// </summary>
    int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Compare(Decode(a), Decode(b));
}

/// <summary>
/// Built-in codecs for the common scalar types, plus <see cref="For{T}"/> to resolve one by
/// type. Types without a built-in (your own structs/classes) require a hand-written
/// <see cref="IKeyCodec{T}"/>/<see cref="IValueCodec{T}"/> passed to the <c>Open</c> overload
/// that takes codecs.
/// </summary>
public static class Codecs
{
    /// <summary>
    /// Returns the built-in codec for <typeparamref name="T"/>, or throws
    /// <see cref="NotSupportedException"/> if there isn't one (supply a custom codec instead).
    /// </summary>
    public static IKeyCodec<T> For<T>()
    {
        var t = typeof(T);
        object? codec =
            t == typeof(bool) ? BoolCodec.Instance :
            t == typeof(byte) ? ByteCodec.Instance :
            t == typeof(sbyte) ? SByteCodec.Instance :
            t == typeof(short) ? Int16Codec.Instance :
            t == typeof(ushort) ? UInt16Codec.Instance :
            t == typeof(int) ? Int32Codec.Instance :
            t == typeof(uint) ? UInt32Codec.Instance :
            t == typeof(long) ? Int64Codec.Instance :
            t == typeof(ulong) ? UInt64Codec.Instance :
            t == typeof(float) ? SingleCodec.Instance :
            t == typeof(double) ? DoubleCodec.Instance :
            t == typeof(string) ? StringCodec.Instance :
            t == typeof(Guid) ? GuidCodec.Instance :
            t == typeof(DateTime) ? DateTimeCodec.Instance :
            t == typeof(byte[]) ? ByteArrayCodec.Instance :
            null;

        if (codec is null)
            throw new NotSupportedException(
                $"No built-in codec for '{t}'. Pass a custom IKeyCodec<{t.Name}>/IValueCodec<{t.Name}> " +
                "to the Database.Open overload that accepts codecs.");

        return (IKeyCodec<T>)codec;
    }

    // ---- Scalar codecs ----------------------------------------------------
    // Encodings are little-endian and need only round-trip; ordering comes from Compare.

    public sealed class BoolCodec : IKeyCodec<bool>
    {
        public static readonly BoolCodec Instance = new();
        public byte[] Encode(bool v) => new[] { v ? (byte)1 : (byte)0 };
        public bool TryEncode(bool v, Span<byte> d, out int written)
        {
            if (d.IsEmpty) { written = 0; return false; }
            d[0] = v ? (byte)1 : (byte)0; written = 1; return true;
        }
        public bool Decode(ReadOnlySpan<byte> b) => b[0] != 0;
        public int Compare(bool a, bool b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a[0].CompareTo(b[0]);
    }

    public sealed class ByteCodec : IKeyCodec<byte>
    {
        public static readonly ByteCodec Instance = new();
        public byte[] Encode(byte v) => new[] { v };
        public bool TryEncode(byte v, Span<byte> d, out int written)
        {
            if (d.IsEmpty) { written = 0; return false; }
            d[0] = v; written = 1; return true;
        }
        public byte Decode(ReadOnlySpan<byte> b) => b[0];
        public int Compare(byte a, byte b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a[0].CompareTo(b[0]);
    }

    public sealed class SByteCodec : IKeyCodec<sbyte>
    {
        public static readonly SByteCodec Instance = new();
        public byte[] Encode(sbyte v) => new[] { (byte)v };
        public bool TryEncode(sbyte v, Span<byte> d, out int written)
        {
            if (d.IsEmpty) { written = 0; return false; }
            d[0] = (byte)v; written = 1; return true;
        }
        public sbyte Decode(ReadOnlySpan<byte> b) => (sbyte)b[0];
        public int Compare(sbyte a, sbyte b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => ((sbyte)a[0]).CompareTo((sbyte)b[0]);
    }

    public sealed class Int16Codec : IKeyCodec<short>
    {
        public static readonly Int16Codec Instance = new();
        public byte[] Encode(short v) { var b = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); return b; }
        public bool TryEncode(short v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteInt16LittleEndian(d, v) ? 2 : 0;
            return written != 0;
        }
        public short Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt16LittleEndian(b);
        public int Compare(short a, short b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadInt16LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt16LittleEndian(b));
    }

    public sealed class UInt16Codec : IKeyCodec<ushort>
    {
        public static readonly UInt16Codec Instance = new();
        public byte[] Encode(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
        public bool TryEncode(ushort v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteUInt16LittleEndian(d, v) ? 2 : 0;
            return written != 0;
        }
        public ushort Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16LittleEndian(b);
        public int Compare(ushort a, ushort b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadUInt16LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(b));
    }

    public sealed class Int32Codec : IKeyCodec<int>
    {
        public static readonly Int32Codec Instance = new();
        public byte[] Encode(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
        public bool TryEncode(int v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteInt32LittleEndian(d, v) ? 4 : 0;
            return written != 0;
        }
        public int Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b);
        public int Compare(int a, int b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt32LittleEndian(b));
    }

    public sealed class UInt32Codec : IKeyCodec<uint>
    {
        public static readonly UInt32Codec Instance = new();
        public byte[] Encode(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
        public bool TryEncode(uint v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteUInt32LittleEndian(d, v) ? 4 : 0;
            return written != 0;
        }
        public uint Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
        public int Compare(uint a, uint b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadUInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(b));
    }

    public sealed class Int64Codec : IKeyCodec<long>
    {
        public static readonly Int64Codec Instance = new();
        public byte[] Encode(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); return b; }
        public bool TryEncode(long v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteInt64LittleEndian(d, v) ? 8 : 0;
            return written != 0;
        }
        public long Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt64LittleEndian(b);
        public int Compare(long a, long b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(b));
    }

    public sealed class UInt64Codec : IKeyCodec<ulong>
    {
        public static readonly UInt64Codec Instance = new();
        public byte[] Encode(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
        public bool TryEncode(ulong v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteUInt64LittleEndian(d, v) ? 8 : 0;
            return written != 0;
        }
        public ulong Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt64LittleEndian(b);
        public int Compare(ulong a, ulong b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadUInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b));
    }

    public sealed class SingleCodec : IKeyCodec<float>
    {
        public static readonly SingleCodec Instance = new();
        public byte[] Encode(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); return b; }
        public bool TryEncode(float v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteSingleLittleEndian(d, v) ? 4 : 0;
            return written != 0;
        }
        public float Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadSingleLittleEndian(b);
        public int Compare(float a, float b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadSingleLittleEndian(a).CompareTo(BinaryPrimitives.ReadSingleLittleEndian(b));
    }

    public sealed class DoubleCodec : IKeyCodec<double>
    {
        public static readonly DoubleCodec Instance = new();
        public byte[] Encode(double v) { var b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); return b; }
        public bool TryEncode(double v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteDoubleLittleEndian(d, v) ? 8 : 0;
            return written != 0;
        }
        public double Decode(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadDoubleLittleEndian(b);
        public int Compare(double a, double b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadDoubleLittleEndian(a).CompareTo(BinaryPrimitives.ReadDoubleLittleEndian(b));
    }

    public sealed class StringCodec : IKeyCodec<string>
    {
        public static readonly StringCodec Instance = new();
        public byte[] Encode(string v) => Encoding.UTF8.GetBytes(v);
        public bool TryEncode(string v, Span<byte> d, out int written)
            => Encoding.UTF8.TryGetBytes(v, d, out written);
        public string Decode(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
        // Ordinal: predictable, culture-independent ordering for keys.
        public int Compare(string a, string b) => string.CompareOrdinal(a, b);
        // No CompareEncoded override: memcmp over UTF-8 orders by code point, but CompareOrdinal
        // orders by UTF-16 code unit — they disagree for supplementary characters (surrogate pairs
        // sort below U+E000..U+FFFF in UTF-16). The default decode-then-Compare keeps the order exact.
    }

    public sealed class GuidCodec : IKeyCodec<Guid>
    {
        public static readonly GuidCodec Instance = new();
        public byte[] Encode(Guid v) => v.ToByteArray();
        public bool TryEncode(Guid v, Span<byte> d, out int written)
        {
            written = v.TryWriteBytes(d) ? 16 : 0;
            return written != 0;
        }
        public Guid Decode(ReadOnlySpan<byte> b) => new(b);
        public int Compare(Guid a, Guid b) => a.CompareTo(b);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => new Guid(a).CompareTo(new Guid(b));
    }

    public sealed class DateTimeCodec : IKeyCodec<DateTime>
    {
        public static readonly DateTimeCodec Instance = new();
        // Stores ticks only; the decoded value has DateTimeKind.Unspecified. Compared by ticks.
        public byte[] Encode(DateTime v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v.Ticks); return b; }
        public bool TryEncode(DateTime v, Span<byte> d, out int written)
        {
            written = BinaryPrimitives.TryWriteInt64LittleEndian(d, v.Ticks) ? 8 : 0;
            return written != 0;
        }
        public DateTime Decode(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadInt64LittleEndian(b));
        public int Compare(DateTime a, DateTime b) => a.Ticks.CompareTo(b.Ticks);
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => BinaryPrimitives.ReadInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(b));
    }

    public sealed class ByteArrayCodec : IKeyCodec<byte[]>
    {
        public static readonly ByteArrayCodec Instance = new();
        public byte[] Encode(byte[] v) => v;
        public byte[] Decode(ReadOnlySpan<byte> b) => b.ToArray();
        public int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);
        // Saves two ToArray allocations per comparison.
        public int CompareEncoded(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
    }
}
