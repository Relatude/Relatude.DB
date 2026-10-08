using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Relatude.DB.FileConversion.ImageEncoders;

internal sealed unsafe class PngCodec : IImageCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public ImageFormat Format => ImageFormat.Png;

    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        return header.Length >= Signature.Length && header[..Signature.Length].SequenceEqual(Signature);
    }

    public bool TryReadSize(byte[] data, out int width, out int height)
    {
        bool ok = data.Length >= 24 && CanDecode(data) && data.AsSpan(12, 4).SequenceEqual("IHDR"u8);
        width = ok ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)) : 0;
        height = ok ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)) : 0;
        return ok && width > 0 && height > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public InternalImage Decode(byte[] data, int downscale)
    {
        if (!CanDecode(data))
        {
            throw new ImageFormatException("Invalid PNG signature.");
        }

        int width = 0, height = 0, depth = 0, type = 0, total = 0;
        byte[]? palette = null, transparency = null;
        List<(int Offset, int Length)> idat = [];
        for (int at = 8; at + 12 <= data.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            if (length < 0 || at + 12 + length > data.Length)
            {
                throw new ImageFormatException("PNG chunk is truncated.");
            }

            var kind = data.AsSpan(at + 4, 4);
            var payload = data.AsSpan(at + 8, length);
            bool header = kind.SequenceEqual("IHDR"u8), plte = kind.SequenceEqual("PLTE"u8), trns = kind.SequenceEqual("tRNS"u8);
            // image data is left to zlib's own checksum
            if ((header || plte || trns) && Crc32.Compute(data.AsSpan(at + 4, 4 + length)) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 8 + length)))
            {
                throw new ImageFormatException("PNG CRC check failed.");
            }

            if (header)
            {
                if (length != 13) throw new ImageFormatException("Invalid PNG IHDR chunk.");
                width = BinaryPrimitives.ReadInt32BigEndian(payload);
                height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                depth = payload[8];
                type = payload[9];
                ValidateHeader(width, height, depth, type, payload[12]);
            }
            else if (plte) palette = payload.ToArray();
            else if (trns) transparency = payload.ToArray();
            else if (kind.SequenceEqual("IDAT"u8)) { idat.Add((at + 8, length)); total += length; }
            else if (kind.SequenceEqual("IEND"u8)) break;
            at += 12 + length;
        }

        if (width == 0) throw new ImageFormatException("PNG image is missing IHDR.");
        if (idat.Count == 0) throw new ImageFormatException("PNG image has no IDAT data.");
        ImageLimits.ThrowIfTooLarge(width, height);
        var compressed = new byte[total];
        int copied = 0;
        foreach (var (offset, length) in idat)
        {
            Buffer.BlockCopy(data, offset, compressed, copied, length);
            copied += length;
        }

        int channels = ChannelsForColorType(type), bits = channels * depth;
        int rowBytes = checked((width * bits + 7) / 8), stride = rowBytes + 1, bpp = Math.Max(1, bits / 8);
        // deflate expands at most 1032 times: data too small to hold the picture is refused before its buffer exists
        if ((long)stride * height > total * 1032L + 4096)
        {
            throw new ImageFormatException("PNG image data is truncated.");
        }

        var raw = GC.AllocateUninitializedArray<byte>(checked(stride * height + 4));
        using (var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            if (zlib.ReadAtLeast(raw.AsSpan(0, stride * height), stride * height, throwOnEndOfStream: false) < stride * height)
            {
                throw new ImageFormatException("PNG image data is truncated.");
            }
        }

        var zero = new byte[rowBytes + 4];
        fixed (byte* r = raw, z = zero)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = r + (long)y * stride + 1;
                Unfilter(row[-1], row, y == 0 ? z : row - stride, rowBytes, bpp);
            }
        }

        uint[]? lut = type == 3 ? PaletteLut(palette, transparency) : type == 0 && depth < 16 ? GrayLut(depth, transparency) : null;
        int mask = (1 << depth) - 1;
        var rgba = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            fixed (byte* r = raw, o = rgba)
            {
                byte* s = r + (long)y * stride + 1;
                uint* d = (uint*)(o + (long)y * width * 4);
                if (lut != null && depth == 8)
                    for (int x = 0; x < width; x++) d[x] = lut[s[x]];
                else if (lut != null)
                    for (int x = 0, bit = 0; x < width; x++, bit += depth) d[x] = lut[(s[bit >> 3] >> (8 - depth - (bit & 7))) & mask];
                else if (depth == 8 && type == 6)
                    Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                else if (depth == 8 && type == 4)
                    for (int x = 0; x < width; x++, s += 2) d[x] = s[0] * 0x010101u | (uint)s[1] << 24;
                else if (depth == 8)
                {
                    uint key = transparency is { Length: >= 6 } t ? (uint)(t[1] | t[3] << 8 | t[5] << 16) : uint.MaxValue;
                    for (int x = 0; x < width; x++, s += 3)
                    {
                        uint c = (uint)(s[0] | s[1] << 8 | s[2] << 16);
                        d[x] = c == key ? c : c | 0xFF000000u;
                    }
                }
                else
                    for (int x = 0; x < width; x++, s += channels * 2) d[x] = Pixel16(s, type, transparency);
            }
        }

        if (InternalImage.ShouldParallelize(width, height)) Parallel.For(0, height, Row);
        else for (int y = 0; y < height; y++) Row(y);
        return new InternalImage(width, height, rgba);
    }

    private static uint[] PaletteLut(byte[]? palette, byte[]? transparency)
    {
        if (palette is null || palette.Length % 3 != 0) throw new ImageFormatException("Indexed PNG palette is missing or invalid.");
        var lut = new uint[256];
        Array.Fill(lut, 0xFF000000u);
        for (int i = 0; i < palette.Length / 3 && i < 256; i++)
        {
            uint alpha = transparency is not null && i < transparency.Length ? transparency[i] : 255u;
            lut[i] = (uint)(palette[i * 3] | palette[i * 3 + 1] << 8 | palette[i * 3 + 2] << 16) | alpha << 24;
        }

        return lut;
    }

    private static uint[] GrayLut(int depth, byte[]? transparency)
    {
        int max = (1 << depth) - 1, key = transparency is { Length: >= 2 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : -1;
        var lut = new uint[256];
        for (int v = 0; v <= max; v++) lut[v] = (uint)((v * 255 + max / 2) / max) * 0x010101u | (v == key ? 0u : 0xFF000000u);
        return lut;
    }

    private static uint Pixel16(byte* s, int type, byte[]? transparency)
    {
        int s0 = s[0] << 8 | s[1], s1 = s[2] << 8 | s[3], s2 = s[4] << 8 | s[5];
        bool keyed = type switch
        {
            0 => transparency is { Length: >= 2 } && s0 == BinaryPrimitives.ReadUInt16BigEndian(transparency),
            2 => transparency is { Length: >= 6 } && s0 == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                && s1 == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && s2 == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)),
            _ => false,
        };
        return type switch
        {
            0 => s[0] * 0x010101u | (keyed ? 0u : 0xFF000000u),
            4 => s[0] * 0x010101u | (uint)s[2] << 24,
            2 => (uint)(s[0] | s[2] << 8 | s[4] << 16) | (keyed ? 0u : 0xFF000000u),
            _ => (uint)(s[0] | s[2] << 8 | s[4] << 16 | s[6] << 24),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Unfilter(int filter, byte* c, byte* p, int n, int bpp)
    {
        if (bpp is 3 or 4 && filter is 1 or 3 or 4)
        {
            UnfilterPixels(filter, c, p, n, bpp);
            return;
        }

        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < n; i++) c[i] += c[i - bpp];
                break;
            case 2:
                int j = 0;
                if (Vector.IsHardwareAccelerated)
                    for (; j <= n - Vector<byte>.Count; j += Vector<byte>.Count)
                        Unsafe.WriteUnaligned(c + j, Unsafe.ReadUnaligned<Vector<byte>>(c + j) + Unsafe.ReadUnaligned<Vector<byte>>(p + j));
                for (; j < n; j++) c[j] += p[j];
                break;
            case 3:
                for (int i = 0; i < bpp; i++) c[i] += (byte)(p[i] >> 1);
                for (int i = bpp; i < n; i++) c[i] += (byte)((c[i - bpp] + p[i]) >> 1);
                break;
            case 4:
                for (int i = 0; i < bpp; i++) c[i] += p[i];
                for (int i = bpp; i < n; i++) c[i] += (byte)Paeth(c[i - bpp], p[i], p[i - bpp]);
                break;
            default:
                throw new ImageFormatException($"Invalid PNG filter type: {filter}.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<short> Pixel4(byte* p) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(uint*)p).AsByte()).AsInt16();

    // a whole pixel at a time, the one to its left kept in a register: Sub, Average and Paeth depend on it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterPixels(int filter, byte* c, byte* p, int n, int bpp)
    {
        var mask = Vector128.Create((short)255);
        Vector128<short> left = default, upLeft = default;
        for (int i = 0; i < n; i += bpp)
        {
            Vector128<short> x = Pixel4(c + i), pred;
            if (filter == 1)
            {
                pred = left;
            }
            else
            {
                var up = Pixel4(p + i);
                if (filter == 3)
                {
                    pred = Vector128.ShiftRightArithmetic(left + up, 1);
                }
                else
                {
                    var pa = Vector128.Abs(up - upLeft);
                    var pb = Vector128.Abs(left - upLeft);
                    var pc = Vector128.Abs(left + up - upLeft - upLeft);
                    pred = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc), left,
                        Vector128.ConditionalSelect(Vector128.LessThanOrEqual(pb, pc), up, upLeft));
                }

                upLeft = up;
            }

            left = (x + pred) & mask;
            uint v = Vector128.Narrow(left.AsUInt16(), left.AsUInt16()).AsUInt32().ToScalar();
            if (bpp == 4)
            {
                *(uint*)(c + i) = v;
            }
            else
            {
                *(ushort*)(c + i) = (ushort)v;
                c[i + 2] = (byte)(v >> 16);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Encode(InternalImage image, Stream stream, ImageSaveOptions options)
    {
        int w = image.Width, h = image.Height;
        var px = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        bool opaque = true, gray = true;
        Dictionary<uint, int>? colors = [];
        uint last = ~px[0];
        foreach (uint p in px)
        {
            if (p == last) continue;
            last = p;
            if (p < 0xFF000000u) opaque = false;
            if (((p ^ (p >> 8)) & 0xFFFF) != 0) gray = false;
            if (colors != null && !colors.ContainsKey(p))
            {
                if (colors.Count == 256) colors = null;
                else colors[p] = colors.Count;
            }

            if (!opaque && !gray && colors == null) break;
        }

        // grey keeps the filters working on photos; a palette wins on graphics
        int type = gray ? (opaque ? 0 : 4) : colors != null ? 3 : opaque ? 2 : 6;
        int depth = type == 3 ? colors!.Count switch { <= 2 => 1, <= 4 => 2, <= 16 => 4, _ => 8 } : 8;
        int channels = ChannelsForColorType(type), rowBytes = (w * channels * depth + 7) / 8, bpp = Math.Max(1, channels * depth / 8);

        stream.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)type;
        ihdr[10] = ihdr[11] = ihdr[12] = 0;
        WriteChunk(stream, "IHDR"u8, ihdr);
        if (type == 3)
        {
            var plte = new byte[colors!.Count * 3];
            var trns = new byte[colors.Count];
            int translucent = 0;
            foreach (var (c, i) in colors)
            {
                plte[i * 3] = (byte)c;
                plte[i * 3 + 1] = (byte)(c >> 8);
                plte[i * 3 + 2] = (byte)(c >> 16);
                trns[i] = (byte)(c >> 24);
                if (trns[i] != 255) translucent = Math.Max(translucent, i + 1);
            }

            WriteChunk(stream, "PLTE"u8, plte);
            if (translucent > 0) WriteChunk(stream, "tRNS"u8, trns.AsSpan(0, translucent));
        }

        CompressionLevel level = options.PngCompressionLevel <= 0 ? CompressionLevel.NoCompression
            : options.PngCompressionLevel <= 3 ? CompressionLevel.Fastest
            : options.PngCompressionLevel <= 6 ? CompressionLevel.Optimal
            : CompressionLevel.SmallestSize;
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, level, leaveOpen: true))
        {
            byte[] row = new byte[rowBytes], previous = new byte[rowBytes], best = new byte[rowBytes + 1], candidate = new byte[rowBytes + 1];
            for (int y = 0; y < h; y++)
            {
                var source = px.Slice(y * w, w);
                if (type == 6) MemoryMarshal.AsBytes(source).CopyTo(row);
                else Array.Clear(row);
                for (int x = 0, bit = 0; type != 6 && x < w; x++, bit += depth * channels)
                {
                    uint p = source[x];
                    switch (type)
                    {
                        case 0: row[x] = (byte)p; break;
                        case 4: row[2 * x] = (byte)p; row[2 * x + 1] = (byte)(p >> 24); break;
                        case 2: row[3 * x] = (byte)p; row[3 * x + 1] = (byte)(p >> 8); row[3 * x + 2] = (byte)(p >> 16); break;
                        default: row[bit >> 3] |= (byte)(colors![p] << (8 - depth - (bit & 7))); break;
                    }
                }

                if (type == 3)
                {
                    zlib.WriteByte(0);
                    zlib.Write(row);
                }
                else
                {
                    int bestScore = int.MaxValue;
                    for (int filter = 0; filter <= 4; filter++)
                    {
                        int score = FilterAndScore(row, previous, candidate.AsSpan(1), filter, bpp);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            candidate[0] = (byte)filter;
                            (best, candidate) = (candidate, best);
                        }
                    }

                    zlib.Write(best);
                }

                (previous, row) = (row, previous);
            }
        }

        WriteChunk(stream, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int FilterAndScore(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, Span<byte> output, int filter, int bpp)
    {
        int n = row.Length, first = Math.Min(bpp, n);
        switch (filter)
        {
            case 0:
                row.CopyTo(output);
                break;
            case 1:
                row[..first].CopyTo(output);
                for (int i = bpp; i < n; i++) output[i] = (byte)(row[i] - row[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < n; i++) output[i] = (byte)(row[i] - previous[i]);
                break;
            case 3:
                for (int i = 0; i < first; i++) output[i] = (byte)(row[i] - (previous[i] >> 1));
                for (int i = bpp; i < n; i++) output[i] = (byte)(row[i] - ((row[i - bpp] + previous[i]) >> 1));
                break;
            default:
                for (int i = 0; i < first; i++) output[i] = (byte)(row[i] - previous[i]);
                for (int i = bpp; i < n; i++) output[i] = (byte)(row[i] - Paeth(row[i - bpp], previous[i], previous[i - bpp]));
                break;
        }

        int score = 0;
        foreach (sbyte v in MemoryMarshal.Cast<byte, sbyte>(output[..n])) score += v < 0 ? -v : v;
        return score;
    }

    private static int Paeth(int a, int b, int c)
    {
        int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void ValidateHeader(int width, int height, int bitDepth, int colorType, int interlace)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageFormatException("Invalid PNG dimensions.");
        }

        if (interlace != 0)
        {
            throw new ImageFormatException("Interlaced PNG files are not supported.");
        }

        bool valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };

        if (!valid)
        {
            throw new ImageFormatException($"Unsupported PNG color type and bit depth: {colorType}/{bitDepth}.");
        }
    }

    private static int ChannelsForColorType(int colorType)
    {
        return colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new ImageFormatException($"Unsupported PNG color type: {colorType}.")
        };
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(payload);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(type, payload));
        stream.Write(crc);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data) => Compute(data, []);

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in first)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            foreach (byte value in second)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return crc ^ 0xffffffffu;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
