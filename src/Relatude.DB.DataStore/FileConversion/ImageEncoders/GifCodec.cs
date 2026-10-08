using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// GIF: reads every frame of an animation composed as browsers show it (disposal, transparency, interlacing);
/// writes stills and animations with one palette of at most 255 colours plus transparency.
/// </summary>
internal sealed class GifCodec : IImageCodec {
    public ImageFormat Format => ImageFormat.Gif;

    public bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8));

    static int U16(byte[] d, int at) => d[at] | d[at + 1] << 8;

    public bool TryReadSize(byte[] data, out int width, out int height) {
        bool ok = data.Length >= 10 && CanDecode(data);
        width = ok ? U16(data, 6) : 0;
        height = ok ? U16(data, 8) : 0;
        return ok && width > 0 && height > 0;
    }

    public InternalImage Decode(byte[] data, int downscale) => Read(data, 1).Frames[0];

    public void Encode(InternalImage image, Stream stream, ImageSaveOptions options) => Write(new Animation([image], [0], 1), stream);

    // ── Reading ──────────────────────────────────────────────────────────────

    static readonly uint[] Greys = Enumerable.Range(0, 256).Select(i => (uint)(i * 0x010101) | 0xFF000000).ToArray();

    /// <summary>True when the data holds more than one frame.</summary>
    public static bool IsAnimated(byte[] d) {
        if (d.Length < 13) return false;
        int at = 13, frames = 0;
        if ((d[10] & 0x80) != 0) at += 3 * (2 << (d[10] & 7));
        while (at < d.Length) {
            int block = d[at++];
            if (block == 0x21 && at < d.Length) {
                at = Skip(d, at + 1);
            } else if (block == 0x2C && at + 9 < d.Length) {
                if (++frames > 1) return true;
                int flags = d[at + 8];
                at += 9 + ((flags & 0x80) != 0 ? 3 * (2 << (flags & 7)) : 0) + 1;
                at = Skip(d, at);
            } else {
                break;
            }
        }
        return false;
    }

    static int Skip(byte[] d, int at) {
        while (at < d.Length) {
            int size = d[at++];
            if (size == 0) break;
            at += size;
        }
        return Math.Min(at, d.Length);
    }

    static uint[] Palette(byte[] d, ref int at, int flags) {
        var palette = new uint[2 << (flags & 7)];
        int end = at + 3 * palette.Length;
        for (int i = 0; i < palette.Length && at + 3 <= Math.Min(end, d.Length); i++, at += 3)
            palette[i] = d[at] | (uint)d[at + 1] << 8 | (uint)d[at + 2] << 16 | 0xFF000000;
        at = end;
        return palette;
    }

    /// <summary>
    /// The first frames of a gif, at most max of them, each composed onto the canvas as it is shown. What cannot be
    /// read ends the animation there; a screen too small for the first frame grows to hold it, as in browsers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Animation Read(byte[] d, int max) {
        if (d.Length < 13 || !d.AsSpan(0, 6).SequenceEqual("GIF87a"u8) && !d.AsSpan(0, 6).SequenceEqual("GIF89a"u8)) throw new ImageFormatException("Invalid GIF header.");
        int width = U16(d, 6), height = U16(d, 8), at = 13;
        uint[]? global = (d[10] & 0x80) != 0 ? Palette(d, ref at, d[10]) : null;
        var frames = new List<InternalImage>();
        var durations = new List<int>();
        int loops = 1, disposal = 0, transparent = -1, delay = 0;
        uint[]? canvas = null;
        while (at < d.Length && frames.Count < max) {
            int block = d[at++];
            if (block == 0x21 && at < d.Length) {
                int label = d[at++];
                if (label == 0xF9 && at + 4 < d.Length && d[at] >= 4) {
                    disposal = (d[at + 1] >> 2) & 7;
                    transparent = (d[at + 1] & 1) != 0 ? d[at + 4] : -1;
                    delay = U16(d, at + 2) * 10;
                } else if (label == 0xFF && at + 15 < d.Length && d[at] == 11 && d[at + 12] >= 3 && d[at + 13] == 1
                    && (d.AsSpan(at + 1, 11).SequenceEqual("NETSCAPE2.0"u8) || d.AsSpan(at + 1, 11).SequenceEqual("ANIMEXTS1.0"u8))) {
                    loops = U16(d, at + 14);
                }
                at = Skip(d, at);
                continue;
            }
            if (block != 0x2C || at + 10 > d.Length) break; // the trailer, or what cannot be read
            int fx = U16(d, at), fy = U16(d, at + 2), fw = U16(d, at + 4), fh = U16(d, at + 6), flags = d[at + 8];
            at += 9;
            var palette = (flags & 0x80) != 0 ? Palette(d, ref at, flags) : global ?? Greys;
            if (canvas == null) {
                width = Math.Max(width, fx + fw);
                height = Math.Max(height, fy + fh);
                if (width == 0 || height == 0) break;
                if ((long)width * height > Animation.MaxPixels) throw new ImageFormatException("GIF canvas is too large.");
                canvas = new uint[width * height];
            }
            if (at >= d.Length) break;
            int minCode = d[at++];
            var indices = Lzw(d, ref at, minCode, fw * fh, out int decoded);
            uint[]? previous = disposal == 3 ? (uint[])canvas.Clone() : null;
            bool interlaced = (flags & 0x40) != 0;
            for (int r = 0; r < fh; r++) {
                int y = fy + (interlaced ? InterlacedRow(r, fh) : r);
                if (y >= height) continue;
                int from = r * fw, to = Math.Min(decoded, from + Math.Max(0, Math.Min(fw, width - fx)));
                for (int i = from, o = y * width + fx; i < to; i++, o++) {
                    int c = indices[i];
                    if (c != transparent) canvas[o] = c < palette.Length ? palette[c] : 0xFF000000;
                }
            }
            frames.Add(new InternalImage(width, height, MemoryMarshal.AsBytes(canvas.AsSpan()).ToArray()));
            durations.Add(delay);
            if (disposal == 2) {
                for (int y = fy; y < Math.Min(height, fy + fh); y++)
                    if (fx < width) canvas.AsSpan(y * width + fx, Math.Min(fw, width - fx)).Clear();
            } else if (previous != null) {
                canvas = previous;
            }
            (disposal, transparent, delay) = (0, -1, 0);
            if ((frames.Count + 1L) * width * height > Animation.MaxPixels) break;
        }
        if (frames.Count == 0) throw new ImageFormatException("GIF has no readable frame.");
        return new Animation([.. frames], [.. durations], loops);
    }

    // where the r-th row an interlaced frame stores goes: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1
    static int InterlacedRow(int r, int height) {
        int pass1 = (height + 7) / 8, pass2 = (height + 3) / 8, pass3 = (height + 1) / 4;
        if (r < pass1) return r * 8;
        if ((r -= pass1) < pass2) return r * 8 + 4;
        if ((r -= pass2) < pass3) return r * 4 + 2;
        return (r - pass3) * 2 + 1;
    }

    /// <summary>A frame's colour indices from the LZW data in the sub-blocks at at, which is left after them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static byte[] Lzw(byte[] d, ref int at, int minCode, int count, out int decoded) {
        int length = 0;
        for (int p = at; p < d.Length;) {
            int size = d[p++];
            if (size == 0) break;
            length += Math.Min(size, d.Length - p);
            p += size;
        }
        var data = new byte[length];
        for (int n = 0; at < d.Length;) {
            int size = d[at++];
            if (size == 0) break;
            int take = Math.Min(size, d.Length - at);
            Buffer.BlockCopy(d, at, data, n, take);
            n += take;
            at += size;
        }
        at = Math.Min(at, d.Length);
        var output = new byte[count];
        decoded = 0;
        if (minCode < 1 || minCode > 11) return output;
        int clear = 1 << minCode, codeSize = minCode + 1, next = clear + 2, prev = -1, written = 0, pos = 0, bits = 0;
        uint buffer = 0;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var first = new byte[4096];
        var lengths = new short[4096];
        for (int i = 0; i < clear; i++) {
            suffix[i] = first[i] = (byte)i;
            lengths[i] = 1;
        }
        while (written < count) {
            while (bits < codeSize && pos < data.Length) {
                buffer |= (uint)data[pos++] << bits;
                bits += 8;
            }
            if (bits < codeSize) break;
            int code = (int)(buffer & ((1u << codeSize) - 1));
            buffer >>= codeSize;
            bits -= codeSize;
            if (code == clear) {
                (codeSize, next, prev) = (minCode + 1, clear + 2, -1);
                continue;
            }
            if (code == clear + 1) break;
            if (prev < 0) {
                if (code > clear) break;
                output[written++] = (byte)code;
                prev = code;
                continue;
            }
            if (code > next) break;
            if (next < 4096) {
                prefix[next] = (short)prev;
                suffix[next] = code == next ? first[prev] : first[code];
                first[next] = first[prev];
                lengths[next] = (short)(lengths[prev] + 1);
                if (++next == 1 << codeSize && codeSize < 12) codeSize++;
            }
            int end = written + lengths[code];
            for (int c = code, p = end - 1; p >= written; p--, c = prefix[c])
                if (p < count) output[p] = suffix[c];
            written = Math.Min(end, count);
            prev = code;
        }
        decoded = written;
        return output;
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An animation with one palette for all its frames, which are transparent below half opacity. Without any
    /// transparency, each frame after the first holds only the rectangle that changed, unchanged pixels in it
    /// transparent; with it, every frame is whole and cleared before the next.
    /// </summary>
    public static void Write(Animation animation, Stream stream) {
        var frames = animation.Frames;
        int w = frames[0].Width, h = frames[0].Height;
        if (w > 65535 || h > 65535) throw new ArgumentOutOfRangeException(nameof(animation), "GIF images are limited to 65535 x 65535 pixels.");
        var (palette, indexed) = Quantize(frames);
        int transparent = palette.Length, bits = 1;
        while (1 << bits < palette.Length + 1) bits++;
        bool opaque = true;
        foreach (var f in indexed) opaque &= !f.AsSpan().Contains((byte)transparent);
        stream.Write("GIF89a"u8);
        stream.Write([(byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), (byte)(0xF0 | (bits - 1)), 0, 0]);
        var table = new byte[3 << bits];
        for (int i = 0; i < palette.Length; i++) (table[3 * i], table[3 * i + 1], table[3 * i + 2]) = ((byte)palette[i], (byte)(palette[i] >> 8), (byte)(palette[i] >> 16));
        stream.Write(table);
        // frames the palette makes identical to the one before are merged into it
        var shown = new List<(int Frame, int Duration)>();
        for (int i = 0; i < frames.Length; i++) {
            if (shown.Count > 0 && indexed[i].AsSpan().SequenceEqual(indexed[shown[^1].Frame])) shown[^1] = (shown[^1].Frame, shown[^1].Duration + animation.Durations[i]);
            else shown.Add((i, animation.Durations[i]));
        }
        if (shown.Count > 1 && animation.Loops != 1) {
            stream.Write([0x21, 0xFF, 11]);
            stream.Write("NETSCAPE2.0"u8);
            stream.Write([3, 1, (byte)animation.Loops, (byte)(animation.Loops >> 8), 0]);
        }
        for (int s = 0; s < shown.Count; s++) {
            var pixels = indexed[shown[s].Frame];
            var rect = new RectangleI(0, 0, w, h);
            byte[] data = pixels;
            if (opaque && s > 0) {
                var before = indexed[shown[s - 1].Frame];
                Animation.Changed<byte>(pixels, before, w, h, out rect);
                data = new byte[rect.Width * rect.Height];
                for (int y = 0, o = 0; y < rect.Height; y++)
                    for (int x = 0, i = (rect.Y + y) * w + rect.X; x < rect.Width; x++, i++, o++)
                        data[o] = pixels[i] == before[i] ? (byte)transparent : pixels[i];
            }
            bool holes = !opaque || s > 0;
            if (shown.Count > 1 || holes) {
                int delay = Math.Min(65535, (shown[s].Duration + 5) / 10);
                stream.Write([0x21, 0xF9, 4, (byte)((opaque ? 1 : 2) << 2 | (holes ? 1 : 0)), (byte)delay, (byte)(delay >> 8), (byte)transparent, 0]);
            }
            stream.Write([0x2C, (byte)rect.X, (byte)(rect.X >> 8), (byte)rect.Y, (byte)(rect.Y >> 8),
                (byte)rect.Width, (byte)(rect.Width >> 8), (byte)rect.Height, (byte)(rect.Height >> 8), 0]);
            WriteLzw(stream, data, Math.Max(2, bits));
        }
        stream.WriteByte(0x3B);
    }

    /// <summary>
    /// One palette for every frame, the transparent index just after it, and the frames as indices into it: their
    /// own colours when they number 255 or fewer, else a median cut of them, each colour mapped to its nearest.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static (uint[] Palette, byte[][] Indexed) Quantize(InternalImage[] frames) {
        var exact = new Dictionary<uint, byte>();
        foreach (var f in frames) {
            uint last = 0;
            foreach (uint p in MemoryMarshal.Cast<byte, uint>(f.Pixels)) {
                if (p < 0x80000000 || p == last) continue;
                last = p;
                if (exact.TryAdd(p & 0xFFFFFF, (byte)exact.Count) && exact.Count > 255) break;
            }
            if (exact.Count > 255) break;
        }
        var indexed = new byte[frames.Length][];
        if (exact.Count <= 255) {
            var colours = exact.Keys.ToArray();
            Parallel.For(0, frames.Length, i => indexed[i] = Index(frames[i], (byte)colours.Length, rgb => exact[rgb]));
            return (colours, indexed);
        }
        var palette = MedianCut(frames, out var nearest);
        Parallel.For(0, frames.Length, i => indexed[i] = Index(frames[i], (byte)palette.Length, rgb => nearest[(rgb >> 3 & 31) << 10 | (rgb >> 11 & 31) << 5 | (rgb >> 19 & 31)]));
        return (palette, indexed);
    }

    static byte[] Index(InternalImage frame, byte transparent, Func<uint, byte> colour) {
        var pixels = MemoryMarshal.Cast<byte, uint>(frame.Pixels);
        var indices = new byte[pixels.Length];
        uint last = 0;
        byte index = transparent;
        for (int i = 0; i < pixels.Length; i++) {
            uint p = pixels[i];
            if (p < 0x80000000) {
                indices[i] = transparent;
                continue;
            }
            if (p != last) (last, index) = (p, colour(p & 0xFFFFFF));
            indices[i] = index;
        }
        return indices;
    }

    // 255 colours from a histogram of 15-bit colours, splitting the fullest box at the median of its longest side; nearest maps each 15-bit colour to one of them
    static uint[] MedianCut(InternalImage[] frames, out byte[] nearest) {
        var counts = new long[32768];
        var sums = new long[32768 * 3];
        long total = 0;
        foreach (var f in frames) total += (long)f.Width * f.Height;
        int step = (int)Math.Max(1, total / (1 << 22)); // a sample of about four million pixels is plenty
        foreach (var f in frames) {
            var pixels = MemoryMarshal.Cast<byte, uint>(f.Pixels);
            for (int i = 0; i < pixels.Length; i += step) {
                uint p = pixels[i];
                if (p < 0x80000000) continue;
                int r = (int)(p & 255), g = (int)(p >> 8 & 255), b = (int)(p >> 16 & 255), bin = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
                counts[bin]++;
                sums[3 * bin] += r;
                sums[3 * bin + 1] += g;
                sums[3 * bin + 2] += b;
            }
        }
        var bins = Enumerable.Range(0, 32768).Where(b => counts[b] > 0).ToArray();
        var boxes = new List<(int Start, int End, long Count)> { (0, bins.Length, bins.Sum(b => counts[b])) };
        while (boxes.Count < 255) {
            int pick = -1;
            for (int i = 0; i < boxes.Count; i++)
                if (boxes[i].End - boxes[i].Start > 1 && (pick < 0 || boxes[i].Count > boxes[pick].Count)) pick = i;
            if (pick < 0) break;
            var (start, end, count) = boxes[pick];
            int shift = 0, longest = -1;
            for (int s = 0; s <= 10; s += 5) {
                int lo = 31, hi = 0;
                for (int i = start; i < end; i++) {
                    int v = bins[i] >> s & 31;
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
                if (hi - lo > longest) (longest, shift) = (hi - lo, s);
            }
            bins.AsSpan(start, end - start).Sort((a, b) => (a >> shift & 31) - (b >> shift & 31));
            long run = 0;
            int cut = start + 1;
            while (cut < end - 1 && (run += counts[bins[cut - 1]]) < count / 2) cut++;
            long low = 0;
            for (int i = start; i < cut; i++) low += counts[bins[i]];
            boxes[pick] = (start, cut, low);
            boxes.Add((cut, end, count - low));
        }
        var palette = new uint[boxes.Count];
        for (int i = 0; i < boxes.Count; i++) {
            long r = 0, g = 0, b = 0, n = 0;
            for (int j = boxes[i].Start; j < boxes[i].End; j++) {
                int bin = bins[j];
                (r, g, b, n) = (r + sums[3 * bin], g + sums[3 * bin + 1], b + sums[3 * bin + 2], n + counts[bin]);
            }
            palette[i] = (uint)((r + n / 2) / n) | (uint)((g + n / 2) / n) << 8 | (uint)((b + n / 2) / n) << 16;
        }
        var map = new byte[32768];
        Parallel.For(0, 32768, bin => {
            int r = counts[bin] > 0 ? (int)(sums[3 * bin] / counts[bin]) : (bin >> 10) << 3 | 4;
            int g = counts[bin] > 0 ? (int)(sums[3 * bin + 1] / counts[bin]) : (bin >> 5 & 31) << 3 | 4;
            int b = counts[bin] > 0 ? (int)(sums[3 * bin + 2] / counts[bin]) : (bin & 31) << 3 | 4;
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < palette.Length; i++) {
                int dr = r - (int)(palette[i] & 255), dg = g - (int)(palette[i] >> 8 & 255), db = b - (int)(palette[i] >> 16 & 255);
                int distance = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
                if (distance < bestDistance) (bestDistance, best) = (distance, i);
            }
            map[bin] = (byte)best;
        });
        nearest = map;
        return palette;
    }

    /// <summary>Indices compressed with LZW into sub-blocks, after the minimum code size, then the terminating block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void WriteLzw(Stream stream, byte[] indices, int minCode) {
        stream.WriteByte((byte)minCode);
        var block = new byte[256];
        int filled = 0, bits = 0;
        uint buffer = 0;
        void Emit(int code, int size) {
            buffer |= (uint)code << bits;
            bits += size;
            while (bits >= 8) {
                block[++filled] = (byte)buffer;
                buffer >>= 8;
                bits -= 8;
                if (filled == 255) {
                    block[0] = 255;
                    stream.Write(block, 0, 256);
                    filled = 0;
                }
            }
        }
        int clear = 1 << minCode, codeSize = minCode + 1, next = clear + 2;
        var keys = new int[8192];
        var codes = new short[8192];
        Array.Fill(keys, -1);
        Emit(clear, codeSize);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++) {
            int key = prefix << 8 | indices[i], slot = (int)((uint)key * 0x9E3779B1u >> 19) & 8191;
            while (keys[slot] >= 0 && keys[slot] != key) slot = (slot + 1) & 8191;
            if (keys[slot] == key) {
                prefix = codes[slot];
                continue;
            }
            Emit(prefix, codeSize);
            if (next < 4096) {
                (keys[slot], codes[slot]) = (key, (short)next);
                if (++next > 1 << codeSize && codeSize < 12) codeSize++;
            } else {
                Emit(clear, codeSize);
                Array.Fill(keys, -1);
                (codeSize, next) = (minCode + 1, clear + 2);
            }
            prefix = indices[i];
        }
        Emit(prefix, codeSize);
        if (next < 4096 && next == 1 << codeSize && codeSize < 12) codeSize++; // as the reader grows it after this last code
        Emit(clear + 1, codeSize);
        if (bits > 0) Emit(0, 8 - bits);
        if (filled > 0) {
            block[0] = (byte)filled;
            stream.Write(block, 0, filled + 1);
        }
        stream.WriteByte(0);
    }
}
