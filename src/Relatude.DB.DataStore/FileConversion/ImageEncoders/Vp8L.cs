using System.Numerics;
using System.Runtime.CompilerServices;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// Lossless WebP (VP8L): the complete decoder, and a compact encoder (LZ77 and Huffman codes, no transforms)
/// that the lossy encoder uses for alpha.
/// </summary>
internal static class Vp8L {
    static readonly int[] CodeLengthOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // short distances as (x, y) offsets, the first 120 distance codes
    static readonly sbyte[] DistanceMap = [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3, 3, 1, -3, 1,
        2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4, 4, 2, -4, 2, 0, 5,
        3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2, 4, 4, -4, 4, 3, 5, -3, 5,
        5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2, 4, 5, -4, 5, 5, 4, -5, 4, 3, 6,
        -3, 6, 6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1, 4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7,
        7, 2, -7, 2, 3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5, 8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2,
        6, 6, -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7, -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7];

    static int Div(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    static int Reverse(int code, int length) => (int)(BitOperations.RotateLeft(ReverseBits((uint)code), length) & ((1u << length) - 1));

    static uint ReverseBits(uint v) {
        v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
        v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
        v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
        v = ((v >> 8) & 0x00FF00FF) | ((v & 0x00FF00FF) << 8);
        return (v >> 16) | (v << 16);
    }

    // ── Decoding ─────────────────────────────────────────────────────────────

    sealed class BitReader(byte[] d, int pos, int end) {
        ulong _bits;
        int _count, _padding;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Fill() {
            for (; _count <= 56; _count += 8) {
                if (pos < end) _bits |= (ulong)d[pos++] << _count;
                else _padding++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Peek(int n) {
            if (_count < n) Fill();
            return (int)(_bits & ((1UL << n) - 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int n) {
            _bits >>= n;
            _count -= n;
        }

        public int Read(int n) {
            int v = Peek(n);
            Skip(n);
            return v;
        }

        public bool Overrun => _padding * 8 > _count;
    }

    sealed class Prefix {
        readonly int _single = -1, _bits;
        readonly int[] _table = [], _sorted = [];
        readonly int[] _first = new int[16], _count = new int[16], _offset = new int[16];

        public Prefix(int[] lengths) {
            int used = 0, last = 0, longest = 0;
            for (int s = 0; s < lengths.Length; s++) {
                if (lengths[s] == 0) continue;
                used++;
                last = s;
                longest = Math.Max(longest, lengths[s]);
                _count[lengths[s]]++;
            }
            if (used <= 1) {
                _single = last;
                return;
            }
            for (int len = 1, code = 0, index = 0; len < 16; len++) {
                _first[len] = code;
                _offset[len] = index;
                if (code + _count[len] > 1 << len) throw new ImageFormatException("Invalid WEBP lossless code lengths.");
                code = (code + _count[len]) << 1;
                index += _count[len];
            }
            _sorted = new int[used];
            var fill = (int[])_offset.Clone();
            for (int s = 0; s < lengths.Length; s++) if (lengths[s] > 0) _sorted[fill[lengths[s]]++] = s;
            _bits = Math.Min(longest, 9);
            _table = new int[1 << _bits];
            for (int len = 1; len <= _bits; len++)
                for (int i = 0; i < _count[len]; i++)
                    for (int r = Reverse(_first[len] + i, len); r < _table.Length; r += 1 << len) _table[r] = len << 16 | _sorted[_offset[len] + i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(BitReader br) {
            if (_single >= 0) return _single;
            int e = _table[br.Peek(_bits)];
            if (e != 0) {
                br.Skip(e >> 16);
                return e & 0xffff;
            }
            return ReadLong(br);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int ReadLong(BitReader br) {
            for (int len = 1, code = 0; len < 16; len++) {
                code = code << 1 | br.Read(1);
                int k = code - _first[len];
                if (k >= 0 && k < _count[len]) return _sorted[_offset[len] + k];
            }
            throw new ImageFormatException("Invalid WEBP lossless code.");
        }
    }

    sealed record Transform(int Type, int Bits, int XSize, uint[] Data);

    public static uint[] Decode(byte[] d, int start, int length, out int width, out int height) {
        if (length < 5 || d[start] != 0x2f) throw new ImageFormatException("Invalid WEBP lossless header.");
        var br = new BitReader(d, start + 1, start + length);
        width = br.Read(14) + 1;
        height = br.Read(14) + 1;
        ImageLimits.ThrowIfTooLarge(width, height);
        br.Read(1);
        if (br.Read(3) != 0) throw new ImageFormatException("Unsupported WEBP lossless version.");
        return Finish(br, Stream(br, width, height, true));
    }

    /// <summary>An ALPH chunk's lossless stream: an image without the VP8L header, alpha in green.</summary>
    public static uint[] DecodeHeaderless(byte[] d, int start, int length, int width, int height) {
        var br = new BitReader(d, start, start + length);
        return Finish(br, Stream(br, width, height, true));
    }

    static uint[] Finish(BitReader br, uint[] pixels) => br.Overrun ? throw Truncated() : pixels;

    // checked as rows and codes are read, not only at the end: missing data reads as zeros, which can fill a
    // picture of any size
    static ImageFormatException Truncated() => new("WEBP lossless data is truncated.");

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static uint[] Stream(BitReader br, int width, int height, bool main) {
        var transforms = new List<Transform>();
        int xs = width;
        for (int seen = 0; main && br.Read(1) == 1;) {
            int type = br.Read(2);
            if ((seen & (1 << type)) != 0) throw new ImageFormatException("WEBP lossless transform repeated.");
            seen |= 1 << type;
            if (type is 0 or 1) {
                int bits = br.Read(3) + 2;
                transforms.Add(new(type, bits, xs, Stream(br, Div(xs, bits), Div(height, bits), false)));
            } else if (type == 2) {
                transforms.Add(new(type, 0, xs, []));
            } else {
                int colors = br.Read(8) + 1, bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                var stored = Stream(br, colors, 1, false);
                var palette = new uint[256];
                palette[0] = stored[0];
                for (int i = 1; i < colors; i++) palette[i] = Add(stored[i], palette[i - 1]);
                transforms.Add(new(type, bits, xs, palette));
                xs = Div(xs, bits);
            }
        }
        int cacheBits = br.Read(1) == 1 ? br.Read(4) : 0;
        if (cacheBits > 11) throw new ImageFormatException("Invalid WEBP colour cache size.");
        uint[]? meta = null;
        int metaBits = 0, metaWidth = 0, groups = 1;
        if (main && br.Read(1) == 1) {
            metaBits = br.Read(3) + 2;
            metaWidth = Div(xs, metaBits);
            meta = Stream(br, metaWidth, Div(height, metaBits), false);
            foreach (uint m in meta) groups = Math.Max(groups, (int)((m >> 8) & 0xffff) + 1);
        }
        var codes = new Prefix[groups * 5];
        int green = 256 + 24 + (cacheBits > 0 ? 1 << cacheBits : 0);
        for (int g = 0; g < codes.Length; g += 5) {
            if (br.Overrun) throw Truncated();
            codes[g] = ReadCode(br, green);
            codes[g + 1] = ReadCode(br, 256);
            codes[g + 2] = ReadCode(br, 256);
            codes[g + 3] = ReadCode(br, 256);
            codes[g + 4] = ReadCode(br, 40);
        }
        var data = Pixels(br, xs, height, codes, meta, metaBits, metaWidth, cacheBits);
        for (int i = transforms.Count - 1; i >= 0; i--) data = Inverse(transforms[i], data, height);
        return data;
    }

    static Prefix ReadCode(BitReader br, int size) {
        var lengths = new int[size];
        if (br.Read(1) == 1) {
            int count = br.Read(1) + 1;
            int first = br.Read(br.Read(1) == 1 ? 8 : 1);
            if (first >= size) throw new ImageFormatException("WEBP simple code is out of range.");
            lengths[first] = 1;
            if (count == 2) {
                int second = br.Read(8);
                if (second >= size) throw new ImageFormatException("WEBP simple code is out of range.");
                lengths[second] = 1;
            }
            return new Prefix(lengths);
        }
        var codeLengths = new int[19];
        int n = br.Read(4) + 4;
        for (int i = 0; i < n; i++) codeLengths[CodeLengthOrder[i]] = br.Read(3);
        var lengthCode = new Prefix(codeLengths);
        int tokens = size;
        if (br.Read(1) == 1) {
            tokens = 2 + br.Read(2 + 2 * br.Read(3));
            if (tokens > size) throw new ImageFormatException("WEBP code length count exceeds its alphabet.");
        }
        for (int symbol = 0, previous = 8; symbol < size && tokens-- > 0;) {
            int c = lengthCode.Read(br);
            if (c < 16) {
                lengths[symbol++] = c;
                if (c != 0) previous = c;
                continue;
            }
            int repeat = c == 16 ? 3 + br.Read(2) : c == 17 ? 3 + br.Read(3) : 11 + br.Read(7);
            if (symbol + repeat > size) throw new ImageFormatException("WEBP code lengths overflow their alphabet.");
            Array.Fill(lengths, c == 16 ? previous : 0, symbol, repeat);
            symbol += repeat;
        }
        return new Prefix(lengths);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int Value(BitReader br, int prefix) {
        if (prefix < 4) return prefix + 1;
        int extra = (prefix - 2) >> 1;
        return ((2 + (prefix & 1)) << extra) + br.Read(extra) + 1;
    }

    static int PlaneDistance(int code, int width) {
        if (code > 120) return code - 120;
        int d = DistanceMap[2 * code - 2] + DistanceMap[2 * code - 1] * width;
        return d < 1 ? 1 : d;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static uint[] Pixels(BitReader br, int width, int height, Prefix[] codes, uint[]? meta, int metaBits, int metaWidth, int cacheBits) {
        int total = checked(width * height), shift = 32 - cacheBits, mask = (1 << metaBits) - 1;
        var data = new uint[total];
        var cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        int g = 0;
        for (int pos = 0, x = 0, y = 0; pos < total;) {
            if (meta != null && (x & mask) == 0) g = (int)((meta[(y >> metaBits) * metaWidth + (x >> metaBits)] >> 8) & 0xffff) * 5;
            int code = codes[g].Read(br);
            if (code < 256 || code >= 280) {
                uint argb;
                if (code < 256) {
                    int r = codes[g + 1].Read(br), b = codes[g + 2].Read(br), a = codes[g + 3].Read(br);
                    argb = (uint)(a << 24 | r << 16 | code << 8 | b);
                } else {
                    if (cache == null || code - 280 >= cache.Length) throw new ImageFormatException("Invalid WEBP colour cache index.");
                    argb = cache[code - 280];
                }
                data[pos++] = argb;
                if (cache != null) cache[(0x1e35a7bd * argb) >> shift] = argb;
                if (++x == width) {
                    x = 0;
                    y++;
                    if (br.Overrun) throw Truncated();
                }
                continue;
            }
            int length = Value(br, code - 256);
            int distance = PlaneDistance(Value(br, codes[g + 4].Read(br)), width);
            if (distance > pos || length > total - pos) throw new ImageFormatException("Invalid WEBP backward reference.");
            for (int end = pos + length; pos < end; pos++) {
                uint v = data[pos - distance];
                data[pos] = v;
                if (cache != null) cache[(0x1e35a7bd * v) >> shift] = v;
            }
            x += length;
            while (x >= width) { x -= width; y++; }
            if (br.Overrun) throw Truncated();
            if (meta != null && (x & mask) != 0 && pos < total) g = (int)((meta[(y >> metaBits) * metaWidth + (x >> metaBits)] >> 8) & 0xffff) * 5;
        }
        return data;
    }

    static uint Add(uint a, uint b) => (((a & 0xff00ff00) + (b & 0xff00ff00)) & 0xff00ff00) | (((a & 0x00ff00ff) + (b & 0x00ff00ff)) & 0x00ff00ff);
    static uint Average(uint a, uint b) => (((a ^ b) & 0xfefefefe) >> 1) + (a & b);
    static int Channel(uint p, int s) => (int)(p >> s) & 0xff;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static uint Select(uint top, uint left, uint topLeft) {
        int d = 0;
        for (int s = 0; s < 32; s += 8)
            d += Math.Abs(Channel(left, s) - Channel(topLeft, s)) - Math.Abs(Channel(top, s) - Channel(topLeft, s));
        return d <= 0 ? top : left;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static uint Clamped(uint a, uint b, uint c, bool half) {
        uint result = 0;
        for (int s = 0; s < 32; s += 8) {
            int x = Channel(a, s), v = half ? x + (x - Channel(c, s)) / 2 : x + Channel(b, s) - Channel(c, s);
            result |= (uint)Math.Clamp(v, 0, 255) << s;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static uint[] Inverse(Transform t, uint[] d, int height) {
        int w = t.XSize;
        switch (t.Type) {
            case 0: {
                int tiles = Div(w, t.Bits);
                d[0] = Add(d[0], 0xff000000);
                for (int x = 1; x < w; x++) d[x] = Add(d[x], d[x - 1]);
                for (int y = 1; y < height; y++) {
                    int row = y * w, modes = (y >> t.Bits) * tiles;
                    d[row] = Add(d[row], d[row - w]);
                    for (int x = 1; x < w; x++) {
                        int i = row + x;
                        uint L = d[i - 1], T = d[i - w], TL = d[i - w - 1], TR = d[i - w + 1];
                        uint pred = (int)(t.Data[modes + (x >> t.Bits)] >> 8 & 15) switch {
                            1 => L,
                            2 => T,
                            3 => TR,
                            4 => TL,
                            5 => Average(Average(L, TR), T),
                            6 => Average(L, TL),
                            7 => Average(L, T),
                            8 => Average(TL, T),
                            9 => Average(T, TR),
                            10 => Average(Average(L, TL), Average(T, TR)),
                            11 => Select(T, L, TL),
                            12 => Clamped(L, T, TL, false),
                            13 => Clamped(Average(L, T), 0, TL, true),
                            _ => 0xff000000,
                        };
                        d[i] = Add(d[i], pred);
                    }
                }
                return d;
            }
            case 1: {
                int tiles = Div(w, t.Bits);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < w; x++) {
                        uint m = t.Data[(y >> t.Bits) * tiles + (x >> t.Bits)], p = d[y * w + x];
                        int green = (sbyte)(p >> 8);
                        int red = (Channel(p, 16) + (((sbyte)m * green) >> 5)) & 0xff;
                        int blue = (Channel(p, 0) + (((sbyte)(m >> 8) * green) >> 5) + (((sbyte)(m >> 16) * (sbyte)red) >> 5)) & 0xff;
                        d[y * w + x] = (p & 0xff00ff00) | (uint)red << 16 | (uint)blue;
                    }
                return d;
            }
            case 2:
                for (int i = 0; i < d.Length; i++) {
                    uint p = d[i], g = (p >> 8) & 0xff;
                    d[i] = (p & 0xff00ff00) | (((p >> 16) + g) & 0xff) << 16 | ((p + g) & 0xff);
                }
                return d;
            default: {
                var output = new uint[w * height];
                int packed = Div(w, t.Bits), perPixel = 8 >> t.Bits, mask = (1 << perPixel) - 1, per = (1 << t.Bits) - 1;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < w; x++) {
                        int index = (int)(d[y * packed + (x >> t.Bits)] >> 8 >> ((x & per) * perPixel)) & mask;
                        output[y * w + x] = t.Data[index];
                    }
                return output;
            }
        }
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    sealed class BitWriter {
        byte[] _buffer = new byte[4096];
        int _length, _n;
        ulong _acc;

        public void Write(int bits, int count) {
            _acc |= (ulong)(uint)bits << _n;
            for (_n += count; _n >= 8; _n -= 8, _acc >>= 8) {
                if (_length == _buffer.Length) Array.Resize(ref _buffer, _length * 2);
                _buffer[_length++] = (byte)_acc;
            }
        }

        public byte[] ToArray() {
            if (_n > 0) Write(0, 8 - _n);
            return _buffer[.._length];
        }
    }

    readonly record struct Token(int Length, int Distance, uint Pixel);

    /// <summary>An image as a VP8L stream without the header: no transforms, no colour cache, one set of codes.</summary>
    public static byte[] EncodeHeaderless(uint[] argb, int width) {
        var bw = new BitWriter();
        bw.Write(0, 3); // no transform, no colour cache, no meta codes
        var tokens = Lz77(argb, width);
        int[][] histograms = [new int[280], new int[256], new int[256], new int[256], new int[40]];
        foreach (var t in tokens) {
            if (t.Length == 0) {
                histograms[0][(t.Pixel >> 8) & 0xff]++;
                histograms[1][(t.Pixel >> 16) & 0xff]++;
                histograms[2][t.Pixel & 0xff]++;
                histograms[3][t.Pixel >> 24]++;
            } else {
                histograms[0][256 + PrefixOf(t.Length).Code]++;
                histograms[4][PrefixOf(t.Distance).Code]++;
            }
        }
        var lengths = histograms.Select(h => BuildLengths(h, 15)).ToArray();
        foreach (var l in lengths) WriteCode(bw, l);
        var codes = lengths.Select(Codes).ToArray();
        var bits = lengths.Select(Bits).ToArray();
        void Put(int code, int symbol) => bw.Write(codes[code][symbol], bits[code][symbol]);
        foreach (var t in tokens) {
            if (t.Length == 0) {
                Put(0, (int)(t.Pixel >> 8) & 0xff);
                Put(1, (int)(t.Pixel >> 16) & 0xff);
                Put(2, (int)t.Pixel & 0xff);
                Put(3, (int)(t.Pixel >> 24));
                continue;
            }
            var length = PrefixOf(t.Length);
            Put(0, 256 + length.Code);
            bw.Write(length.Extra, length.Bits);
            var distance = PrefixOf(t.Distance);
            Put(4, distance.Code);
            bw.Write(distance.Extra, distance.Bits);
        }
        return bw.ToArray();
    }

    // the bits each symbol takes: none when it is the code's only symbol
    static int[] Bits(int[] lengths) => lengths.Count(l => l > 0) > 1 ? lengths : new int[lengths.Length];

    static (int Code, int Bits, int Extra) PrefixOf(int value) {
        int x = value - 1;
        if (x < 4) return (x, 0, 0);
        int h = 31 - BitOperations.LeadingZeroCount((uint)x);
        return (2 * h + ((x >> (h - 1)) & 1), h - 1, x & ((1 << (h - 1)) - 1));
    }

    // greedy matching against the pixel to the left, the one above and the last place two pixels were seen
    static List<Token> Lz77(uint[] p, int width) {
        var codes = new Dictionary<int, int>();
        for (int i = 0; i < 120; i++) {
            int d = DistanceMap[2 * i] + DistanceMap[2 * i + 1] * width;
            if (d >= 1) codes.TryAdd(d, i + 1);
        }
        var head = new int[1 << 16];
        Array.Fill(head, -1);
        static int Hash(uint a, uint b) => (int)((a * 0x9E3779B1u ^ b * 0x85EBCA6Bu) >> 16);
        var tokens = new List<Token>();
        for (int i = 0; i < p.Length;) {
            int bestLength = 0, bestDistance = 0;
            void Try(int distance) {
                if (distance < 1 || distance > i || distance > (1 << 20) - 120) return;
                int length = 0, max = Math.Min(4096, p.Length - i);
                while (length < max && p[i + length] == p[i + length - distance]) length++;
                if (length > bestLength) { bestLength = length; bestDistance = distance; }
            }
            Try(1);
            Try(width);
            if (i + 1 < p.Length) {
                int h = Hash(p[i], p[i + 1]);
                if (head[h] >= 0) Try(i - head[h]);
                head[h] = i;
            }
            if (bestLength < 3) {
                tokens.Add(new(0, 0, p[i++]));
                continue;
            }
            tokens.Add(new(bestLength, codes.TryGetValue(bestDistance, out int c) ? c : bestDistance + 120, 0));
            for (int k = 1; k < bestLength && i + k + 1 < p.Length; k++) head[Hash(p[i + k], p[i + k + 1])] = i + k;
            i += bestLength;
        }
        return tokens;
    }

    // Huffman code lengths no longer than limit: small counts are raised until the tree is shallow enough
    static int[] BuildLengths(int[] freq, int limit) {
        var lengths = new int[freq.Length];
        int used = 0, only = 0;
        for (int i = 0; i < freq.Length; i++) if (freq[i] > 0) { used++; only = i; }
        if (used <= 1) {
            if (used == 1) lengths[only] = 1;
            return lengths;
        }
        for (long floor = 1; ; floor *= 2) {
            var parent = new int[2 * freq.Length];
            var queue = new PriorityQueue<int, long>();
            for (int i = 0; i < freq.Length; i++) if (freq[i] > 0) queue.Enqueue(i, Math.Max(freq[i], floor));
            int next = freq.Length;
            while (queue.Count > 1) {
                queue.TryDequeue(out int a, out long fa);
                queue.TryDequeue(out int b, out long fb);
                parent[a] = parent[b] = next;
                queue.Enqueue(next++, fa + fb);
            }
            int root = next - 1, deepest = 0;
            for (int i = 0; i < freq.Length; i++) {
                if (freq[i] == 0) continue;
                int depth = 0;
                for (int k = i; k != root; k = parent[k]) depth++;
                lengths[i] = depth;
                deepest = Math.Max(deepest, depth);
            }
            if (deepest <= limit) return lengths;
        }
    }

    // canonical codes, bit-reversed for the LSB-first stream
    static int[] Codes(int[] lengths) {
        var count = new int[16];
        foreach (int l in lengths) count[l]++;
        var next = new int[16];
        for (int len = 1, code = 0; len < 16; len++) {
            next[len] = code;
            code = (code + count[len]) << 1;
        }
        var codes = new int[lengths.Length];
        for (int s = 0; s < lengths.Length; s++) if (lengths[s] > 0) codes[s] = Reverse(next[lengths[s]]++, lengths[s]);
        return codes;
    }

    static void WriteCode(BitWriter bw, int[] lengths) {
        int used = 0, s0 = 0, s1 = 0;
        for (int i = 0; i < lengths.Length; i++) {
            if (lengths[i] == 0) continue;
            if (used++ == 0) s0 = i; else s1 = i;
        }
        if (used <= 2 && s0 < 256 && s1 < 256) {
            bw.Write(1, 1);
            bw.Write(used == 2 ? 1 : 0, 1);
            if (s0 < 2) { bw.Write(0, 1); bw.Write(s0, 1); } else { bw.Write(1, 1); bw.Write(s0, 8); }
            if (used == 2) bw.Write(s1, 8);
            return;
        }
        var tokens = new List<(int Code, int Bits, int Extra)>();
        for (int i = 0, previous = 8; i < lengths.Length;) {
            int v = lengths[i], run = 1;
            while (i + run < lengths.Length && lengths[i + run] == v) run++;
            i += run;
            if (v == 0) {
                for (; run >= 11; run -= Math.Min(run, 138)) tokens.Add((18, 7, Math.Min(run, 138) - 11));
                if (run >= 3) { tokens.Add((17, 3, run - 3)); run = 0; }
            } else {
                if (v != previous) { tokens.Add((v, 0, 0)); previous = v; run--; }
                for (; run >= 3; run -= Math.Min(run, 6)) tokens.Add((16, 2, Math.Min(run, 6) - 3));
            }
            for (; run > 0; run--) tokens.Add((v, 0, 0));
        }
        var histogram = new int[19];
        foreach (var t in tokens) histogram[t.Code]++;
        var codeLengths = BuildLengths(histogram, 7);
        int n = 19;
        while (n > 4 && codeLengths[CodeLengthOrder[n - 1]] == 0) n--;
        bw.Write(0, 1);
        bw.Write(n - 4, 4);
        for (int i = 0; i < n; i++) bw.Write(codeLengths[CodeLengthOrder[i]], 3);
        bw.Write(0, 1); // every symbol's length follows
        var codes = Codes(codeLengths);
        var bits = Bits(codeLengths);
        foreach (var t in tokens) {
            bw.Write(codes[t.Code], bits[t.Code]);
            bw.Write(t.Extra, t.Bits);
        }
    }
}
