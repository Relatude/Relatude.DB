using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SuperFastIndex.Internal;

/// <summary>
/// Order-preserving, prefix-free binary encoding for index keys.
/// Encodings compare correctly with an unsigned byte-wise comparison
/// (<see cref="MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>),
/// which lets the B+Tree work on raw bytes for every supported type.
/// Prefix-freedom guarantees that no encoded value is a byte-prefix of another,
/// which is required for composite (value, id) keys.
/// </summary>
internal interface IKeyCodec<T> where T : notnull
{
    /// <summary>Fixed encoded size in bytes, or -1 if variable.</summary>
    int FixedSize { get; }

    /// <summary>Upper bound of the encoded size for <paramref name="value"/>.</summary>
    int GetMaxSize(T value);

    /// <summary>Encodes into <paramref name="dst"/>; returns bytes written.</summary>
    int Encode(Span<byte> dst, T value);

    /// <summary>Decodes a full encoded value produced by <see cref="Encode"/>.</summary>
    T Decode(ReadOnlySpan<byte> src);
}

internal static class KeyCodec
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IKeyCodec<T> Get<T>() where T : notnull => Cache<T>.Instance;

    /// <summary>Stable per-type id persisted in the catalog to detect type mismatches on reopen.</summary>
    public static byte GetTypeId<T>() where T : notnull => Cache<T>.TypeId;

    private static class Cache<T> where T : notnull
    {
        public static readonly IKeyCodec<T> Instance;
        public static readonly byte TypeId;

        static Cache()
        {
            (object codec, byte id) = Create(typeof(T));
            Instance = (IKeyCodec<T>)codec;
            TypeId = id;
        }
    }

    private static (object Codec, byte TypeId) Create(Type t)
    {
        if (t == typeof(int)) return (new Int32Codec(), 1);
        if (t == typeof(long)) return (new Int64Codec(), 2);
        if (t == typeof(short)) return (new Int16Codec(), 3);
        if (t == typeof(sbyte)) return (new SByteCodec(), 4);
        if (t == typeof(uint)) return (new UInt32Codec(), 5);
        if (t == typeof(ulong)) return (new UInt64Codec(), 6);
        if (t == typeof(ushort)) return (new UInt16Codec(), 7);
        if (t == typeof(byte)) return (new ByteCodec(), 8);
        if (t == typeof(bool)) return (new BoolCodec(), 9);
        if (t == typeof(char)) return (new CharCodec(), 10);
        if (t == typeof(float)) return (new SingleCodec(), 11);
        if (t == typeof(double)) return (new DoubleCodec(), 12);
        if (t == typeof(DateTime)) return (new DateTimeCodec(), 13);
        if (t == typeof(TimeSpan)) return (new TimeSpanCodec(), 14);
        if (t == typeof(Guid)) return (new GuidCodec(), 15);
        if (t == typeof(string)) return (new StringCodec(), 16);
        if (t == typeof(DateTimeOffset)) return (new DateTimeOffsetCodec(), 17);
        throw new NotSupportedException(
            $"Type '{t}' is not supported as an index value. Supported: integral types, bool, char, " +
            "float, double, DateTime, DateTimeOffset, TimeSpan, Guid, string.");
    }

    /// <summary>Encodes a signed 32-bit id so unsigned byte order equals signed numeric order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeId(Span<byte> dst, int id)
        => BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)id ^ 0x8000_0000u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeId(ReadOnlySpan<byte> src)
        => (int)(BinaryPrimitives.ReadUInt32BigEndian(src) ^ 0x8000_0000u);

    public const int IdSize = 4;

    private sealed class Int32Codec : IKeyCodec<int>
    {
        public int FixedSize => 4;
        public int GetMaxSize(int value) => 4;
        public int Encode(Span<byte> dst, int value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)value ^ 0x8000_0000u);
            return 4;
        }
        public int Decode(ReadOnlySpan<byte> src) => (int)(BinaryPrimitives.ReadUInt32BigEndian(src) ^ 0x8000_0000u);
    }

    private sealed class Int64Codec : IKeyCodec<long>
    {
        public int FixedSize => 8;
        public int GetMaxSize(long value) => 8;
        public int Encode(Span<byte> dst, long value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value ^ 0x8000_0000_0000_0000ul);
            return 8;
        }
        public long Decode(ReadOnlySpan<byte> src) => (long)(BinaryPrimitives.ReadUInt64BigEndian(src) ^ 0x8000_0000_0000_0000ul);
    }

    private sealed class Int16Codec : IKeyCodec<short>
    {
        public int FixedSize => 2;
        public int GetMaxSize(short value) => 2;
        public int Encode(Span<byte> dst, short value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)((ushort)value ^ 0x8000));
            return 2;
        }
        public short Decode(ReadOnlySpan<byte> src) => (short)(BinaryPrimitives.ReadUInt16BigEndian(src) ^ 0x8000);
    }

    private sealed class SByteCodec : IKeyCodec<sbyte>
    {
        public int FixedSize => 1;
        public int GetMaxSize(sbyte value) => 1;
        public int Encode(Span<byte> dst, sbyte value)
        {
            dst[0] = (byte)(value + 128);
            return 1;
        }
        public sbyte Decode(ReadOnlySpan<byte> src) => (sbyte)(src[0] - 128);
    }

    private sealed class UInt32Codec : IKeyCodec<uint>
    {
        public int FixedSize => 4;
        public int GetMaxSize(uint value) => 4;
        public int Encode(Span<byte> dst, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst, value);
            return 4;
        }
        public uint Decode(ReadOnlySpan<byte> src) => BinaryPrimitives.ReadUInt32BigEndian(src);
    }

    private sealed class UInt64Codec : IKeyCodec<ulong>
    {
        public int FixedSize => 8;
        public int GetMaxSize(ulong value) => 8;
        public int Encode(Span<byte> dst, ulong value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, value);
            return 8;
        }
        public ulong Decode(ReadOnlySpan<byte> src) => BinaryPrimitives.ReadUInt64BigEndian(src);
    }

    private sealed class UInt16Codec : IKeyCodec<ushort>
    {
        public int FixedSize => 2;
        public int GetMaxSize(ushort value) => 2;
        public int Encode(Span<byte> dst, ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(dst, value);
            return 2;
        }
        public ushort Decode(ReadOnlySpan<byte> src) => BinaryPrimitives.ReadUInt16BigEndian(src);
    }

    private sealed class ByteCodec : IKeyCodec<byte>
    {
        public int FixedSize => 1;
        public int GetMaxSize(byte value) => 1;
        public int Encode(Span<byte> dst, byte value)
        {
            dst[0] = value;
            return 1;
        }
        public byte Decode(ReadOnlySpan<byte> src) => src[0];
    }

    private sealed class BoolCodec : IKeyCodec<bool>
    {
        public int FixedSize => 1;
        public int GetMaxSize(bool value) => 1;
        public int Encode(Span<byte> dst, bool value)
        {
            dst[0] = value ? (byte)1 : (byte)0;
            return 1;
        }
        public bool Decode(ReadOnlySpan<byte> src) => src[0] != 0;
    }

    private sealed class CharCodec : IKeyCodec<char>
    {
        public int FixedSize => 2;
        public int GetMaxSize(char value) => 2;
        public int Encode(Span<byte> dst, char value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(dst, value);
            return 2;
        }
        public char Decode(ReadOnlySpan<byte> src) => (char)BinaryPrimitives.ReadUInt16BigEndian(src);
    }

    private sealed class SingleCodec : IKeyCodec<float>
    {
        public int FixedSize => 4;
        public int GetMaxSize(float value) => 4;
        public int Encode(Span<byte> dst, float value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            // IEEE-754 total-order trick: negative -> flip all bits, positive -> flip sign bit.
            bits = (bits & 0x8000_0000u) != 0 ? ~bits : bits ^ 0x8000_0000u;
            BinaryPrimitives.WriteUInt32BigEndian(dst, bits);
            return 4;
        }
        public float Decode(ReadOnlySpan<byte> src)
        {
            uint bits = BinaryPrimitives.ReadUInt32BigEndian(src);
            bits = (bits & 0x8000_0000u) != 0 ? bits ^ 0x8000_0000u : ~bits;
            return BitConverter.UInt32BitsToSingle(bits);
        }
    }

    private sealed class DoubleCodec : IKeyCodec<double>
    {
        public int FixedSize => 8;
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

    private sealed class DateTimeCodec : IKeyCodec<DateTime>
    {
        // Ordered by Ticks; Kind is carried in a trailing byte so it round-trips
        // without affecting the ordering of distinct instants.
        public int FixedSize => 9;
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

    private sealed class DateTimeOffsetCodec : IKeyCodec<DateTimeOffset>
    {
        // Ordered by the UTC instant; the offset is carried for round-tripping.
        public int FixedSize => 10;
        public int GetMaxSize(DateTimeOffset value) => 10;
        public int Encode(Span<byte> dst, DateTimeOffset value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value.UtcTicks);
            BinaryPrimitives.WriteUInt16BigEndian(dst[8..], (ushort)((short)value.Offset.TotalMinutes ^ 0x8000));
            return 10;
        }
        public DateTimeOffset Decode(ReadOnlySpan<byte> src)
        {
            long utcTicks = (long)BinaryPrimitives.ReadUInt64BigEndian(src);
            short offsetMinutes = (short)(BinaryPrimitives.ReadUInt16BigEndian(src[8..]) ^ 0x8000);
            var offset = TimeSpan.FromMinutes(offsetMinutes);
            return new DateTimeOffset(utcTicks + offset.Ticks, offset);
        }
    }

    private sealed class TimeSpanCodec : IKeyCodec<TimeSpan>
    {
        public int FixedSize => 8;
        public int GetMaxSize(TimeSpan value) => 8;
        public int Encode(Span<byte> dst, TimeSpan value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value.Ticks ^ 0x8000_0000_0000_0000ul);
            return 8;
        }
        public TimeSpan Decode(ReadOnlySpan<byte> src)
            => new((long)(BinaryPrimitives.ReadUInt64BigEndian(src) ^ 0x8000_0000_0000_0000ul));
    }

    private sealed class GuidCodec : IKeyCodec<Guid>
    {
        // RFC 4122 big-endian byte order: a stable, memcmp-consistent total order.
        public int FixedSize => 16;
        public int GetMaxSize(Guid value) => 16;
        public int Encode(Span<byte> dst, Guid value)
        {
            value.TryWriteBytes(dst, bigEndian: true, out _);
            return 16;
        }
        public Guid Decode(ReadOnlySpan<byte> src) => new(src, bigEndian: true);
    }

    /// <summary>
    /// UTF-8 with 0x00 escaped as (0x00, 0xFF) and terminated by (0x00, 0x00).
    /// This is order-preserving (byte-wise order equals ordinal UTF-8 order) and
    /// prefix-free: the two-byte terminator sequence cannot occur inside a body,
    /// so no encoding is a byte-prefix of a different encoding.
    /// </summary>
    private sealed class StringCodec : IKeyCodec<string>
    {
        public int FixedSize => -1;
        public int GetMaxSize(string value) => Encoding.UTF8.GetMaxByteCount(value.Length) * 2 + 2;

        public int Encode(Span<byte> dst, string value)
        {
            int utf8Len = Encoding.UTF8.GetBytes(value, dst);
            // Escape embedded zero bytes in place, expanding from the end.
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

            byte[]? rented = null;
            Span<byte> buf = src.Length <= 512 ? stackalloc byte[512] : rented = new byte[src.Length];
            int w = 0;
            for (int r = 0; r < src.Length; r++)
            {
                byte b = src[r];
                buf[w++] = b;
                if (b == 0) r++; // skip 0xFF escape marker
            }
            string result = Encoding.UTF8.GetString(buf[..w]);
            GC.KeepAlive(rented);
            return result;
        }
    }
}
