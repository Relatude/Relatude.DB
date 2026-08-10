using System.Buffers.Binary;
using System.Text;

namespace KvBenchmarks;

/// <summary>
/// Order-preserving, prefix-free binary encoding for the value types the benchmark scenarios use.
/// Mirrors the semantics of the native engine's key codec so all engines sort identically:
/// unsigned byte-wise comparison of encodings equals the logical value order.
/// </summary>
public interface IOrderedCodec<T> where T : notnull
{
    /// <summary>Upper bound of the encoded size for <paramref name="value"/>.</summary>
    int GetMaxSize(T value);

    /// <summary>Encodes into <paramref name="dst"/>; returns bytes written.</summary>
    int Encode(Span<byte> dst, T value);

    /// <summary>Decodes a full encoded value produced by <see cref="Encode"/>.</summary>
    T Decode(ReadOnlySpan<byte> src);
}

public static class OrderedCodec
{
    public static IOrderedCodec<T> Get<T>() where T : notnull => Cache<T>.Instance;

    private static class Cache<T> where T : notnull
    {
        public static readonly IOrderedCodec<T> Instance = (IOrderedCodec<T>)Create(typeof(T));
    }

    private static object Create(Type t)
    {
        if (t == typeof(int)) return new Int32Codec();
        if (t == typeof(long)) return new Int64Codec();
        if (t == typeof(double)) return new DoubleCodec();
        if (t == typeof(string)) return new StringCodec();
        if (t == typeof(Guid)) return new GuidCodec();
        if (t == typeof(DateTime)) return new DateTimeCodec();
        throw new NotSupportedException($"The benchmark codec does not support '{t}'.");
    }

    public const int IdSize = 4;

    /// <summary>Encodes a signed 32-bit id so unsigned byte order equals signed numeric order.</summary>
    public static void WriteId(Span<byte> dst, int id)
        => BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)id ^ 0x8000_0000u);

    public static int ReadId(ReadOnlySpan<byte> src)
        => (int)(BinaryPrimitives.ReadUInt32BigEndian(src) ^ 0x8000_0000u);

    /// <summary>Encodes a value alone (no id suffix).</summary>
    public static byte[] EncodeValue<T>(T value) where T : notnull
    {
        var codec = Get<T>();
        byte[] tmp = new byte[codec.GetMaxSize(value)];
        int n = codec.Encode(tmp, value);
        return n == tmp.Length ? tmp : tmp[..n];
    }

    /// <summary>Encodes the composite (value, id) key: value bytes followed by the 4-byte id.</summary>
    public static byte[] EncodeComposite<T>(T value, int id) where T : notnull
    {
        var codec = Get<T>();
        byte[] tmp = new byte[codec.GetMaxSize(value) + IdSize];
        int n = codec.Encode(tmp, value);
        WriteId(tmp.AsSpan(n), id);
        return tmp.Length == n + IdSize ? tmp : tmp[..(n + IdSize)];
    }

    public static int IdOfComposite(ReadOnlySpan<byte> composite) => ReadId(composite[^IdSize..]);
    public static ReadOnlySpan<byte> ValueOfComposite(ReadOnlySpan<byte> composite) => composite[..^IdSize];

    /// <summary>Unsigned byte-wise comparison, the order every encoding above is designed for.</summary>
    public static int Compare(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) => x.SequenceCompareTo(y);

    private sealed class Int32Codec : IOrderedCodec<int>
    {
        public int GetMaxSize(int value) => 4;
        public int Encode(Span<byte> dst, int value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)value ^ 0x8000_0000u);
            return 4;
        }
        public int Decode(ReadOnlySpan<byte> src) => (int)(BinaryPrimitives.ReadUInt32BigEndian(src) ^ 0x8000_0000u);
    }

    private sealed class Int64Codec : IOrderedCodec<long>
    {
        public int GetMaxSize(long value) => 8;
        public int Encode(Span<byte> dst, long value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value ^ 0x8000_0000_0000_0000ul);
            return 8;
        }
        public long Decode(ReadOnlySpan<byte> src) => (long)(BinaryPrimitives.ReadUInt64BigEndian(src) ^ 0x8000_0000_0000_0000ul);
    }

    private sealed class DoubleCodec : IOrderedCodec<double>
    {
        public int GetMaxSize(double value) => 8;
        public int Encode(Span<byte> dst, double value)
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            bits = (bits & 0x8000_0000_0000_0000ul) != 0 ? ~bits : bits ^ 0x8000_0000_0000_0000ul;
            BinaryPrimitives.WriteUInt64BigEndian(dst, bits);
            return 8;
        }
        public double Decode(ReadOnlySpan<byte> src)
        {
            ulong bits = BinaryPrimitives.ReadUInt64BigEndian(src);
            bits = (bits & 0x8000_0000_0000_0000ul) != 0 ? bits ^ 0x8000_0000_0000_0000ul : ~bits;
            return BitConverter.UInt64BitsToDouble(bits);
        }
    }

    private sealed class GuidCodec : IOrderedCodec<Guid>
    {
        public int GetMaxSize(Guid value) => 16;
        public int Encode(Span<byte> dst, Guid value)
        {
            value.TryWriteBytes(dst, bigEndian: true, out _);
            return 16;
        }
        public Guid Decode(ReadOnlySpan<byte> src) => new(src, bigEndian: true);
    }

    private sealed class DateTimeCodec : IOrderedCodec<DateTime>
    {
        // Ordered by Ticks; Kind carried in a trailing byte, exactly like the native codec.
        public int GetMaxSize(DateTime value) => 9;
        public int Encode(Span<byte> dst, DateTime value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value.Ticks);
            dst[8] = (byte)value.Kind;
            return 9;
        }
        public DateTime Decode(ReadOnlySpan<byte> src)
            => new((long)BinaryPrimitives.ReadUInt64BigEndian(src), (DateTimeKind)src[8]);
    }

    /// <summary>
    /// UTF-8 with 0x00 escaped as (0x00, 0xFF) and terminated by (0x00, 0x00); order-preserving
    /// and prefix-free, so composites (value + id suffix) still compare value-first.
    /// </summary>
    private sealed class StringCodec : IOrderedCodec<string>
    {
        public int GetMaxSize(string value) => Encoding.UTF8.GetMaxByteCount(value.Length) * 2 + 2;

        public int Encode(Span<byte> dst, string value)
        {
            int utf8Len = Encoding.UTF8.GetBytes(value, dst);
            int zeros = dst[..utf8Len].Count((byte)0);
            int total = utf8Len + zeros;
            if (zeros > 0)
            {
                int w = total;
                for (int r = utf8Len - 1; r >= 0; r--)
                {
                    byte b = dst[r];
                    if (b == 0) dst[--w] = 0xFF;
                    dst[--w] = b;
                }
            }
            dst[total] = 0;
            dst[total + 1] = 0;
            return total + 2;
        }

        public string Decode(ReadOnlySpan<byte> src)
        {
            src = src[..^2]; // strip terminator
            int esc = src.IndexOf((byte)0);
            if (esc < 0) return Encoding.UTF8.GetString(src);

            byte[] buf = new byte[src.Length];
            int w = 0;
            for (int r = 0; r < src.Length; r++)
            {
                byte b = src[r];
                buf[w++] = b;
                if (b == 0) r++; // skip 0xFF escape marker
            }
            return Encoding.UTF8.GetString(buf.AsSpan(0, w));
        }
    }
}

/// <summary>memcmp comparer for encoded keys (used by the FASTER engine's in-memory ordered index).</summary>
public sealed class ByteArrayMemCmp : IComparer<byte[]>
{
    public static readonly ByteArrayMemCmp Instance = new();
    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
}
