using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// WebP: reads lossy and lossless images, alpha, the extended format and the first frame of an animation;
/// writes lossy, with any alpha kept losslessly.
/// </summary>
internal sealed class WebpCodec : IImageCodec {
    const uint Lossy = 0x20385056, Lossless = 0x4c385056, Extended = 0x58385056, Alpha = 0x48504c41, Frame = 0x464d4e41;

    public ImageFormat Format => ImageFormat.Webp;

    public bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);

    static int U24(byte[] d, int at) => d[at] | d[at + 1] << 8 | d[at + 2] << 16;

    public bool TryReadSize(byte[] data, out int width, out int height) {
        width = height = 0;
        if (data.Length < 30 || !CanDecode(data)) return false;
        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        if (kind == Extended) {
            (width, height) = (U24(data, 24) + 1, U24(data, 27) + 1);
        } else if (kind == Lossy) {
            (width, height) = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26)) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28)) & 0x3fff);
        } else if (kind == Lossless && data[20] == 0x2f) {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(21));
            (width, height) = ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }
        return width > 0 && height > 0;
    }

    static List<(uint Kind, int Offset, int Length)> Chunks(byte[] d, int start, int end) {
        var chunks = new List<(uint, int, int)>();
        for (int at = start; at + 8 <= end;) {
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
            int length = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
            if (length < 0 || at + 8 + length > end) throw new ImageFormatException("WEBP chunk is truncated.");
            chunks.Add((kind, at + 8, length));
            at += 8 + length + (length & 1);
        }
        return chunks;
    }

    public InternalImage Decode(byte[] data, int downscale) {
        if (!CanDecode(data)) throw new ImageFormatException("Invalid WEBP RIFF header.");
        int end = (int)Math.Min(data.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        var chunks = Chunks(data, 12, end);
        int canvasWidth = 0, canvasHeight = 0;
        foreach (var (kind, offset, length) in chunks) {
            if (kind == Extended && length >= 10) (canvasWidth, canvasHeight) = (U24(data, offset + 4) + 1, U24(data, offset + 7) + 1);
            if (kind != Frame || length < 16) continue;
            // the first frame of an animation, on its canvas
            var frame = Image(data, Chunks(data, offset + 16, offset + length));
            int x = 2 * U24(data, offset), y = 2 * U24(data, offset + 3);
            if (x == 0 && y == 0 && frame.Width == canvasWidth && frame.Height == canvasHeight) return frame;
            if (x + frame.Width > canvasWidth || y + frame.Height > canvasHeight) throw new ImageFormatException("WEBP frame lies outside its canvas.");
            return frame.Pad(canvasWidth, canvasHeight, x, y, default);
        }
        return Image(data, chunks);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static InternalImage Image(byte[] d, List<(uint Kind, int Offset, int Length)> chunks) {
        (int Offset, int Length)? alpha = null;
        foreach (var (kind, offset, length) in chunks) {
            if (kind == Alpha) alpha = (offset, length);
            if (kind == Lossless) {
                var argb = Vp8L.Decode(d, offset, length, out int w, out int h);
                var rgba = GC.AllocateUninitializedArray<byte>(argb.Length * 4);
                var pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
                for (int i = 0; i < argb.Length; i++) {
                    uint p = argb[i];
                    pixels[i] = (p & 0xff00ff00) | ((p >> 16) & 0xff) | ((p & 0xff) << 16);
                }
                return new InternalImage(w, h, rgba);
            }
            if (kind == Lossy) {
                var vp8 = Vp8Decoder.Decode(d, offset, length);
                var rgba = ToRgba(vp8);
                if (alpha is { } a) {
                    var values = DecodeAlpha(d, a.Offset, a.Length, vp8.Width, vp8.Height);
                    for (int i = 0; i < values.Length; i++) rgba[i * 4 + 3] = values[i];
                }
                return new InternalImage(vp8.Width, vp8.Height, rgba);
            }
        }
        throw new ImageFormatException("WEBP file has no image data.");
    }

    // ── Alpha ────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static byte[] DecodeAlpha(byte[] d, int offset, int length, int width, int height) {
        if (length < 1) throw new ImageFormatException("WEBP alpha chunk is empty.");
        int method = d[offset] & 3, filter = (d[offset] >> 2) & 3, count = width * height;
        byte[] a;
        if (method == 0) {
            if (length - 1 < count) throw new ImageFormatException("WEBP alpha is truncated.");
            a = d.AsSpan(offset + 1, count).ToArray();
        } else if (method == 1) {
            var argb = Vp8L.DecodeHeaderless(d, offset + 1, length - 1, width, height);
            a = new byte[count];
            for (int i = 0; i < count; i++) a[i] = (byte)(argb[i] >> 8);
        } else {
            throw new ImageFormatException("Unsupported WEBP alpha compression.");
        }
        if (filter != 0)
            for (int i = 0; i < count; i++) a[i] = (byte)(a[i] + AlphaPrediction(a, i, width, filter));
        return a;
    }

    // what an alpha value is predicted from: left, above, or the gradient of both (nothing for the very first)
    static int AlphaPrediction(byte[] a, int i, int width, int filter) {
        int x = i % width;
        if (i < width) return x == 0 ? 0 : a[i - 1];
        if (x == 0) return a[i - width];
        return filter switch {
            1 => a[i - 1],
            2 => a[i - width],
            _ => Math.Clamp(a[i - 1] + a[i - width] - a[i - width - 1], 0, 255),
        };
    }

    // the filter whose residuals look cheapest, applied, then coded as a lossless image of greens
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static byte[] EncodeAlpha(InternalImage image) {
        int w = image.Width, n = w * image.Height;
        ReadOnlySpan<byte> pixels = image.Pixels;
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = pixels[i * 4 + 3];
        byte[] best = a;
        int bestFilter = 0;
        double bestCost = double.MaxValue;
        for (int filter = 0; filter < 4; filter++) {
            var r = new byte[n];
            var histogram = new int[256];
            for (int i = 0; i < n; i++) histogram[r[i] = (byte)(a[i] - (filter == 0 ? 0 : AlphaPrediction(a, i, w, filter)))]++;
            double cost = 0;
            foreach (int c in histogram) if (c > 0) cost -= c * Math.Log2((double)c / n);
            if (cost < bestCost) (bestCost, best, bestFilter) = (cost, r, filter);
        }
        var greens = new uint[n];
        for (int i = 0; i < n; i++) greens[i] = (uint)best[i] << 8;
        return [(byte)(1 | bestFilter << 2), .. Vp8L.EncodeHeaderless(greens, w)];
    }

    // ── Colour conversion (libwebp's BT.601 limited range) ───────────────────

    static int MultHi(int v, int c) => (v * c) >> 8;
    static byte Clip8(int v) => (v & ~16383) == 0 ? (byte)(v >> 6) : v < 0 ? (byte)0 : (byte)255;

    // chroma upsampled with the 9-3-3-1 filter libwebp uses by default
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static byte[] ToRgba(Vp8Decoder dec) {
        int w = dec.Width, h = dec.Height, ys = dec.YStride, uvs = dec.UvStride, cw = (w + 1) >> 1, ch = (h + 1) >> 1;
        var rgba = GC.AllocateUninitializedArray<byte>(checked(w * h * 4));
        byte[] Y = dec.Y, U = dec.U, V = dec.V;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Band(int band) {
            int[] su = new int[cw], sv = new int[cw];
            for (int y = band * 16, y1 = Math.Min(h, y + 16); y < y1; y++) {
                int near = (y >> 1) * uvs, far = Math.Clamp((y & 1) == 0 ? (y >> 1) - 1 : (y >> 1) + 1, 0, ch - 1) * uvs;
                for (int i = 0; i < cw; i++) {
                    su[i] = 3 * U[near + i] + U[far + i];
                    sv[i] = 3 * V[near + i] + V[far + i];
                }
                int row = y * w * 4;
                for (int x = 0; x < w; x++) {
                    int i = x >> 1, j = Math.Clamp((x & 1) == 0 ? i - 1 : i + 1, 0, cw - 1);
                    int u = (3 * su[i] + su[j] + 8) >> 4, v = (3 * sv[i] + sv[j] + 8) >> 4, luma = MultHi(Y[y * ys + x], 19077);
                    rgba[row + x * 4] = Clip8(luma + MultHi(v, 26149) - 14234);
                    rgba[row + x * 4 + 1] = Clip8(luma - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
                    rgba[row + x * 4 + 2] = Clip8(luma + MultHi(u, 33050) - 17685);
                    rgba[row + x * 4 + 3] = 255;
                }
            }
        }
        int bands = (h + 15) / 16;
        if (InternalImage.ShouldParallelize(w, h)) Parallel.For(0, bands, Band);
        else for (int b = 0; b < bands; b++) Band(b);
        return rgba;
    }

    // planes padded to whole macroblocks by repeating the last row and column
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static (byte[] Y, byte[] U, byte[] V) ToYuv(InternalImage image) {
        int w = image.Width, h = image.Height, mbw = (w + 15) >> 4, mbh = (h + 15) >> 4, ys = mbw * 16, uvs = mbw * 8;
        var Y = new byte[ys * mbh * 16];
        var U = new byte[uvs * mbh * 8];
        var V = new byte[uvs * mbh * 8];
        ReadOnlySpan<byte> p = image.Pixels;
        for (int y = 0; y < mbh * 16; y++)
            for (int x = 0, row = Math.Min(y, h - 1) * w; x < ys; x++) {
                int o = (row + Math.Min(x, w - 1)) * 4;
                Y[y * ys + x] = (byte)((16839 * p[o] + 33059 * p[o + 1] + 6420 * p[o + 2] + (16 << 16) + (1 << 15)) >> 16);
            }
        for (int y = 0; y < mbh * 8; y++)
            for (int x = 0; x < uvs; x++) {
                int r = 0, g = 0, b = 0;
                for (int k = 0; k < 4; k++) {
                    int o = (Math.Min(2 * y + (k >> 1), h - 1) * w + Math.Min(2 * x + (k & 1), w - 1)) * 4;
                    r += p[o];
                    g += p[o + 1];
                    b += p[o + 2];
                }
                U[y * uvs + x] = (byte)Math.Clamp((-9719 * r - 19081 * g + 28800 * b + (128 << 18) + (1 << 17)) >> 18, 0, 255);
                V[y * uvs + x] = (byte)Math.Clamp((28800 * r - 24116 * g - 4684 * b + (128 << 18) + (1 << 17)) >> 18, 0, 255);
            }
        return (Y, U, V);
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public void Encode(InternalImage image, Stream stream, ImageSaveOptions options) {
        if (image.Width > 16383 || image.Height > 16383) throw new ArgumentOutOfRangeException(nameof(image), "WEBP images are limited to 16383 x 16383 pixels.");
        var (y, u, v) = ToYuv(image);
        byte[] frame = Vp8Encoder.Encode(y, u, v, image.Width, image.Height, options.Quality);
        bool translucent = false;
        ReadOnlySpan<byte> pixels = image.Pixels;
        for (int i = 3; i < pixels.Length && !translucent; i += 4) translucent = pixels[i] != 255;
        byte[]? alpha = translucent ? EncodeAlpha(image) : null;
        static int Size(int length) => 8 + length + (length & 1);
        int riff = 4 + Size(frame.Length) + (alpha == null ? 0 : Size(10) + Size(alpha.Length));
        stream.Write("RIFF"u8);
        stream.Write(BitConverter.GetBytes(riff));
        stream.Write("WEBP"u8);
        if (alpha != null) {
            int w = image.Width - 1, h = image.Height - 1;
            Chunk(stream, "VP8X"u8, [0x10, 0, 0, 0, (byte)w, (byte)(w >> 8), (byte)(w >> 16), (byte)h, (byte)(h >> 8), (byte)(h >> 16)]);
            Chunk(stream, "ALPH"u8, alpha);
        }
        Chunk(stream, "VP8 "u8, frame);
    }

    static void Chunk(Stream stream, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> payload) {
        stream.Write(kind);
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        if ((payload.Length & 1) != 0) stream.WriteByte(0);
    }
}
