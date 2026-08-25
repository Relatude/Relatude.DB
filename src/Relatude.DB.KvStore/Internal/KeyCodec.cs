using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Relatude.DB.Common;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Order-preserving, prefix-free binary encoding for index FileKeyUtility.
/// Encodings compare correctly with an unsigned byte-wise comparison
/// (<see cref="MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>),
/// which lets the B+Tree work on raw bytes for every supported type.
/// Prefix-freedom guarantees that no encoded value is a byte-prefix of another,
/// which is required for composite (value, id) FileKeyUtility.
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

    /// <summary>
    /// The codec for a store that only ever compares encodings for equality — the hash layout,
    /// which has no value tree, no ordering and no composite FileKeyUtility. Order-preservation and
    /// prefix-freedom are what force the variable-length types to escape their content
    /// (<see cref="ByteArrayCodec"/>, <see cref="StringCodec"/>, <see cref="FloatArrayCodec"/>),
    /// and neither buys the hash layout anything: it stores the payload with an explicit length and
    /// only asks whether two encodings are byte-identical, which raw bytes answer exactly as well.
    /// So byte arrays, strings and float arrays are stored verbatim here — no escape scan, no
    /// terminator, and no size inflation on content full of zero bytes (a float vector is roughly a
    /// quarter zeros, which the escaping codec grows by a quarter and a worst case doubles). Every
    /// other type is fixed-size and unescaped already, and its encoding is a bijection, so it keeps
    /// the one codec both layouts share.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IKeyCodec<T> GetUnordered<T>() where T : notnull => UnorderedCache<T>.Instance;

    /// <summary>Stable per-type id persisted in the catalog to detect type mismatches on reopen. Independent of which codec flavour above stores the type.</summary>
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

    private static class UnorderedCache<T> where T : notnull
    {
        public static readonly IKeyCodec<T> Instance =
            typeof(T) == typeof(byte[]) ? (IKeyCodec<T>)(object)new RawByteArrayCodec() :
            typeof(T) == typeof(string) ? (IKeyCodec<T>)(object)new RawStringCodec() :
            typeof(T) == typeof(float[]) ? (IKeyCodec<T>)(object)new RawFloatArrayCodec() :
            Cache<T>.Instance;
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
        if (t == typeof(byte[])) return (new ByteArrayCodec(), 18);
        if (t == typeof(GeoCoordinate)) return (new GeoCoordinateCodec(), 19);
        if(t == typeof(float[])) return (new FloatArrayCodec(), 20);
        if (t == typeof(decimal)) return (new DecimalCodec(), 21);
        throw new NotSupportedException(
            $"Type '{t}' is not supported as an index value. Supported: integral types, bool, char, " +
            "float, double, decimal, DateTime, DateTimeOffset, TimeSpan, Guid, string, byte[], float[], GeoCoordinate.");
    }

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
        // The key is the ticks alone. DateTime equality and ordering (==, CompareTo) ignore Kind,
        // so carrying Kind in the key would make two equal values (same ticks, different kinds)
        // distinct keys - a query constant with Kind Unspecified would then miss a stored Utc
        // value. Decoded values come back as Utc, matching the engine's normalization on write.
        public int FixedSize => 8;
        public int GetMaxSize(DateTime value) => 8;
        public int Encode(Span<byte> dst, DateTime value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value.Ticks);
            return 8;
        }
        public DateTime Decode(ReadOnlySpan<byte> src)
            => new((long)BinaryPrimitives.ReadUInt64BigEndian(src), DateTimeKind.Utc);
    }

    private sealed class DateTimeOffsetCodec : IKeyCodec<DateTimeOffset>
    {
        // The key is the UTC instant alone. DateTimeOffset equality and ordering (==, CompareTo)
        // follow the instant and ignore the offset, so including the offset in the key would make
        // two equal values (same instant, different offsets) distinct keys - equality lookups and
        // duplicate grouping would then disagree with the in-memory value index. Decoded values
        // come back with a zero offset; the original offset lives in the node payload, not here.
        public int FixedSize => 8;
        public int GetMaxSize(DateTimeOffset value) => 8;
        public int Encode(Span<byte> dst, DateTimeOffset value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)value.UtcTicks);
            return 8;
        }
        public DateTimeOffset Decode(ReadOnlySpan<byte> src)
            => new((long)BinaryPrimitives.ReadUInt64BigEndian(src), TimeSpan.Zero);
    }

    private sealed class GeoCoordinateCodec : IKeyCodec<GeoCoordinate>
    {
        // The storage value is the 62-bit Morton code + 1 (0 = Empty); big-endian byte order
        // equals its numeric order, which is the type's CompareTo order.
        public int FixedSize => 8;
        public int GetMaxSize(GeoCoordinate value) => 8;
        public int Encode(Span<byte> dst, GeoCoordinate value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, value.StorageValue);
            return 8;
        }
        public GeoCoordinate Decode(ReadOnlySpan<byte> src)
            => GeoCoordinate.FromStorageValue(BinaryPrimitives.ReadUInt64BigEndian(src));
    }
    /// <summary>
    /// Element-wise lexicographic order over floats: each element is written as the four-byte
    /// order-preserving form <see cref="SingleCodec"/> uses (IEEE-754 total order, so -0 &lt; +0
    /// and NaNs sort at the ends), so byte-wise order of the concatenation equals element-wise
    /// order of the arrays. The stream is then escaped exactly like <see cref="ByteArrayCodec"/>
    /// (0x00 as (0x00, 0xFF), terminated by (0x00, 0x00)) to make it prefix-free, which is what
    /// makes a shorter array sort before any array that extends it.
    /// </summary>
    private sealed class FloatArrayCodec : IKeyCodec<float[]>
    {
        public int FixedSize => -1;
        public int GetMaxSize(float[] value) => value.Length * 8 + 2; // 4 bytes per element, each possibly escaped

        public int Encode(Span<byte> dst, float[] value)
        {
            int w = 0;
            foreach (float f in value)
            {
                uint bits = ToOrderedBits(f);
                for (int shift = 24; shift >= 0; shift -= 8)
                {
                    byte b = (byte)(bits >> shift);
                    dst[w++] = b;
                    if (b == 0) dst[w++] = 0xFF;
                }
            }
            dst[w++] = 0;
            dst[w++] = 0;
            return w;
        }

        public float[] Decode(ReadOnlySpan<byte> src)
        {
            src = src[..^2]; // strip terminator
            int zeros = src.Count((byte)0);
            var result = new float[(src.Length - zeros) >> 2];

            if (zeros == 0)
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] = FromOrderedBits(BinaryPrimitives.ReadUInt32BigEndian(src[(i * 4)..]));
                return result;
            }

            int r = 0;
            for (int i = 0; i < result.Length; i++)
            {
                uint bits = 0;
                for (int k = 0; k < 4; k++)
                {
                    byte b = src[r++];
                    bits = (bits << 8) | b;
                    if (b == 0) r++; // skip 0xFF escape marker
                }
                result[i] = FromOrderedBits(bits);
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ToOrderedBits(float value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            return (bits & 0x8000_0000u) != 0 ? ~bits : bits ^ 0x8000_0000u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FromOrderedBits(uint bits)
            => BitConverter.UInt32BitsToSingle((bits & 0x8000_0000u) != 0 ? bits ^ 0x8000_0000u : ~bits);
    }

    private sealed class DecimalCodec : IKeyCodec<decimal>
    {
        // The key is the value scaled to the fixed denominator 10^28 (the maximum decimal scale):
        // a signed 192-bit integer in offset-binary big-endian, so the unsigned byte-wise order
        // equals the numeric order. Scaling to a common denominator canonicalizes the
        // representation - 1.0m and 1.00m carry different scale bits but are the same value, and
        // must produce identical key bytes for equality in both tree and hash layouts (decimal
        // ==, CompareTo and GetHashCode all compare the value, never the representation).
        // Range: |value| <= (2^96-1) * 10^28 < 2^191, so 192 bits always fit.
        // Decoding returns the smallest-scale representation of the value; the original scale
        // representation is not kept, matching how the in-memory index treats equal values as one.
        public int FixedSize => 24;
        public int GetMaxSize(decimal value) => 24;
        public int Encode(Span<byte> dst, decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            var scale = (bits[3] >> 16) & 0xFF;
            var negative = bits[3] < 0;
            ulong m0 = (uint)bits[0] | ((ulong)(uint)bits[1] << 32); // low 64 bits of the magnitude
            ulong m1 = (uint)bits[2];                                // middle 64 bits
            ulong m2 = 0;                                            // high 64 bits
            for (var i = scale; i < 28; i++) // magnitude *= 10 until the denominator is 10^28
            {
                var h0 = Math.BigMul(m0, 10UL, out m0);
                var h1 = Math.BigMul(m1, 10UL, out m1);
                m1 += h0;
                if (m1 < h0) h1++;
                m2 = m2 * 10 + h1; // cannot overflow: the final magnitude stays below 2^191
            }
            if (negative) // two's complement negation (negative zero collapses onto zero)
            {
                m0 = ~m0; m1 = ~m1; m2 = ~m2;
                if (++m0 == 0 && ++m1 == 0) ++m2;
            }
            BinaryPrimitives.WriteUInt64BigEndian(dst, m2 ^ 0x8000_0000_0000_0000ul);
            BinaryPrimitives.WriteUInt64BigEndian(dst[8..], m1);
            BinaryPrimitives.WriteUInt64BigEndian(dst[16..], m0);
            return 24;
        }
        public decimal Decode(ReadOnlySpan<byte> src)
        {
            var m2 = BinaryPrimitives.ReadUInt64BigEndian(src) ^ 0x8000_0000_0000_0000ul;
            var m1 = BinaryPrimitives.ReadUInt64BigEndian(src[8..]);
            var m0 = BinaryPrimitives.ReadUInt64BigEndian(src[16..]);
            var negative = (long)m2 < 0;
            if (negative) // back from two's complement to the magnitude
            {
                m0 = ~m0; m1 = ~m1; m2 = ~m2;
                if (++m0 == 0 && ++m1 == 0) ++m2;
            }
            // shrink the magnitude until it fits the 96-bit decimal mantissa; every division is
            // exact because the magnitude is a valid mantissa times a power of ten
            var scale = 28;
            while (m2 != 0 || m1 > uint.MaxValue)
            {
                var r2 = m2 % 10; m2 /= 10;
                var n1 = ((UInt128)r2 << 64) | m1;
                m1 = (ulong)(n1 / 10);
                var n0 = ((UInt128)(n1 % 10) << 64) | m0;
                m0 = (ulong)(n0 / 10);
                scale--;
            }
            var m = ((UInt128)m1 << 64) | m0;
            while (scale > 0) // then strip trailing zeros for the smallest-scale representation
            {
                var (q, r) = UInt128.DivRem(m, 10);
                if (r != 0) break;
                m = q;
                scale--;
            }
            return new decimal((int)(uint)(ulong)m, (int)(uint)((ulong)m >> 32), (int)(uint)(ulong)(m >> 64), negative, (byte)scale);
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
    /// Raw bytes with 0x00 escaped as (0x00, 0xFF) and terminated by (0x00, 0x00) — the same
    /// scheme as <see cref="StringCodec"/> applied to arbitrary bytes: order-preserving (byte-wise
    /// order of the encodings equals byte-wise order of the raw arrays) and prefix-free.
    /// </summary>
    private sealed class ByteArrayCodec : IKeyCodec<byte[]>
    {
        public int FixedSize => -1;
        public int GetMaxSize(byte[] value) => value.Length * 2 + 2;

        public int Encode(Span<byte> dst, byte[] value)
        {
            int w = 0;
            foreach (byte b in value)
            {
                dst[w++] = b;
                if (b == 0) dst[w++] = 0xFF;
            }
            dst[w++] = 0;
            dst[w++] = 0;
            return w;
        }

        public byte[] Decode(ReadOnlySpan<byte> src)
        {
            src = src[..^2]; // strip terminator
            if (src.IndexOf((byte)0) < 0) return src.ToArray();

            var buf = new byte[src.Length];
            int w = 0;
            for (int r = 0; r < src.Length; r++)
            {
                byte b = src[r];
                buf[w++] = b;
                if (b == 0) r++; // skip 0xFF escape marker
            }
            return buf.AsSpan(..w).ToArray();
        }
    }

    /// <summary>
    /// The bytes themselves, for stores that never order or prefix-scan their values (see
    /// <see cref="GetUnordered{T}"/>): equal arrays encode to equal bytes and distinct arrays to
    /// distinct bytes, which is the whole contract there. Neither order-preserving nor prefix-free,
    /// so it must never reach a B+Tree key.
    /// </summary>
    private sealed class RawByteArrayCodec : IKeyCodec<byte[]>
    {
        public int FixedSize => -1;
        public int GetMaxSize(byte[] value) => value.Length;
        public int Encode(Span<byte> dst, byte[] value)
        {
            value.CopyTo(dst);
            return value.Length;
        }
        public byte[] Decode(ReadOnlySpan<byte> src) => src.ToArray();
    }

    /// <summary>
    /// The <see cref="RawByteArrayCodec"/> of float arrays: the raw little-endian element bits, no
    /// order-preserving transform, no escape pass and no terminator, so an <c>n</c>-element vector
    /// is exactly <c>4n</c> bytes however many zero bytes it contains. Equal arrays encode to equal
    /// bytes and bitwise-distinct arrays to distinct bytes, which is the whole contract for the
    /// stores that use it (see <see cref="GetUnordered{T}"/>). Neither order-preserving nor
    /// prefix-free, so it must never reach a B+Tree key.
    /// <para>
    /// Little-endian is written explicitly rather than blitting native memory: everything else in
    /// the file format fixes its byte order, and this is persisted data. On a little-endian host —
    /// every platform .NET currently ships on — both directions are a straight copy, because
    /// <see cref="BitConverter.IsLittleEndian"/> folds at JIT time and the swapping branch is
    /// dropped entirely.
    /// </para>
    /// </summary>
    private sealed class RawFloatArrayCodec : IKeyCodec<float[]>
    {
        public int FixedSize => -1;
        public int GetMaxSize(float[] value) => value.Length * 4;

        public int Encode(Span<byte> dst, float[] value)
        {
            int size = value.Length * 4;
            if (BitConverter.IsLittleEndian)
            {
                MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(dst);
            }
            else
            {
                for (int i = 0; i < value.Length; i++)
                    BinaryPrimitives.WriteSingleLittleEndian(dst[(i * 4)..], value[i]);
            }
            return size;
        }

        public float[] Decode(ReadOnlySpan<byte> src)
        {
            if (BitConverter.IsLittleEndian) return MemoryMarshal.Cast<byte, float>(src).ToArray();

            var result = new float[src.Length / 4];
            for (int i = 0; i < result.Length; i++)
                result[i] = BinaryPrimitives.ReadSingleLittleEndian(src[(i * 4)..]);
            return result;
        }
    }

    /// <summary>Plain UTF-8, the <see cref="RawByteArrayCodec"/> of strings: no escape pass and no terminator.</summary>
    private sealed class RawStringCodec : IKeyCodec<string>
    {
        public int FixedSize => -1;
        public int GetMaxSize(string value) => Encoding.UTF8.GetMaxByteCount(value.Length);
        public int Encode(Span<byte> dst, string value) => Encoding.UTF8.GetBytes(value, dst);
        public string Decode(ReadOnlySpan<byte> src) => Encoding.UTF8.GetString(src);
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

/// <summary>
/// Value comparers for the dictionary/sorted-set based engines, matching the byte-wise ordering
/// the B+Tree gets for free from the codec encodings: ordinal for strings, content (not reference)
/// order and equality for byte arrays, <c>Default</c> for everything else.
/// </summary>
internal static class ValueComparers
{
    public static IComparer<T> GetComparer<T>() where T : notnull
    {
        if (typeof(T) == typeof(string)) return (IComparer<T>)(object)StringComparer.Ordinal;
        if (typeof(T) == typeof(byte[])) return (IComparer<T>)(object)ByteArrayComparer.Instance;
        return Comparer<T>.Default;
    }

    public static IEqualityComparer<T> GetEqualityComparer<T>() where T : notnull
    {
        if (typeof(T) == typeof(byte[])) return (IEqualityComparer<T>)(object)ByteArrayComparer.Instance;
        return EqualityComparer<T>.Default;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
        public bool Equals(byte[]? x, byte[]? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));
        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
