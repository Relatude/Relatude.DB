using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>Baseline JPEG: decodes at 1, 1/2, 1/4 or 1/8 of full size, encodes with optimized Huffman tables.</summary>
internal sealed unsafe class JpegCodec : IImageCodec {
    // natural position of each zigzag index; the tail absorbs a corrupt run past the last coefficient
    static readonly byte[] ZigZag = [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63];

    static readonly byte[] LuminanceQuant = [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99];

    static readonly byte[] ChrominanceQuant = [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99];

    // AAN scale factors: cos(k*pi/16) * sqrt(2), 1 for k = 0
    static readonly float[] Aan = [1f, 1.387039845f, 1.306562965f, 1.175875602f, 1f, 0.785694958f, 0.541196100f, 0.275899379f];
    static readonly float[][] Cos = [[], [], Basis(2), [], Basis(4)];
    static readonly int[] CrR = new int[256], CbB = new int[256], CrG = new int[256], CbG = new int[256];

    static JpegCodec() {
        for (int i = 0; i < 256; i++) {
            int x = i - 128;
            CrR[i] = (91881 * x + 32768) >> 16;
            CbB[i] = (116130 * x + 32768) >> 16;
            CrG[i] = -46802 * x;
            CbG[i] = -22554 * x + 32768;
        }
    }

    public ImageFormat Format => ImageFormat.Jpeg;

    public bool CanDecode(ReadOnlySpan<byte> header) => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

    public InternalImage Decode(byte[] data, int downscale) => new Decoder(data).Decode(downscale);

    public bool TryReadSize(byte[] data, out int width, out int height) => new Decoder(data).ReadSize(out width, out height);

    static int Ceil(int a, int b) => (a + b - 1) / b;

    static float[] Basis(int n) {
        var t = new float[n * n];
        for (int u = 0; u < n; u++)
            for (int x = 0; x < n; x++)
                t[u * n + x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1) * Math.Cos((2 * x + 1) * u * Math.PI / (2 * n)) / 2);
        return t;
    }

    // 0..255 for -512..511, wrapping beyond: what libjpeg's range limiting does
    static readonly byte[] Limit = Enumerable.Range(0, 1024).Select(i => (byte)Math.Clamp(i < 512 ? i : i - 1024, 0, 255)).ToArray();

    static byte Clamp(float v) => Limit[float.ConvertToIntegerNative<int>(v) & 1023];

    static uint Clip(int v) => (uint)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ── Decoding ─────────────────────────────────────────────────────────────

    sealed class Component {
        public int Id, H, V, Tq, Td, Ta, Pred, Stride, Width, Height;
        public byte[] Plane = [];
    }

    sealed class Huffman {
        public readonly ushort[] Fast = new ushort[512]; // code length << 8 | symbol, for codes of 9 bits or less
        public readonly short[] FastAc = new short[512]; // value << 8 | run << 4 | code and value length, for small AC values
        public readonly uint[] MaxCode = new uint[18];
        public readonly int[] Delta = new int[17];
        public readonly byte[] Values;

        public Huffman(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values, bool ac) {
            Values = values.ToArray();
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++, code <<= 1) {
                Delta[len] = k - code;
                for (int i = 0; i < counts[len - 1]; i++, k++, code++)
                    if (len <= 9) Fast.AsSpan(code << (9 - len), 1 << (9 - len)).Fill((ushort)(len << 8 | Values[k]));
                if (code > 1 << len) throw new ImageFormatException("Invalid JPEG Huffman table.");
                MaxCode[len] = (uint)(code << (16 - len));
            }
            MaxCode[17] = uint.MaxValue;
            if (!ac) return;
            for (int i = 0; i < 512; i++) {
                int len = Fast[i] >> 8, run = (Fast[i] >> 4) & 15, mag = Fast[i] & 15;
                if (len == 0 || mag == 0 || len + mag > 9) continue;
                int v = ((i << len) & 511) >> (9 - mag);
                if (v < 1 << (mag - 1)) v -= (1 << mag) - 1;
                if (v is >= -128 and <= 127) FastAc[i] = (short)(v * 256 + run * 16 + len + mag);
            }
        }
    }

    sealed class Decoder(byte[] d) {
        readonly ushort[]?[] _q = new ushort[4][];
        readonly float[]?[] _fq = new float[4][]; // dequantization folded with the AAN scales and the 1/8
        readonly Huffman?[] _dc = new Huffman[4], _ac = new Huffman[4];
        readonly List<Component> _comps = [];
        int _pos = 2, _restart, _w, _h, _maxH = 1, _maxV = 1, _scale = 1, _n = 8, _orientation = 1;
        bool _frame, _ready, _scanned;
        ulong _bits;
        int _nbits, _padding; // zero bytes fed in place of data the scan does not have
        bool _marker;

        public bool ReadSize(out int width, out int height) {
            bool ok = Parse(true, 1);
            (width, height) = _orientation >= 5 ? (_h, _w) : (_w, _h);
            return ok && _w > 0 && _h > 0;
        }

        public InternalImage Decode(int downscale) {
            Parse(false, downscale);
            if (!_scanned) throw new ImageFormatException("JPEG image has no scan.");
            return Orient(Output());
        }

        bool Parse(bool sizeOnly, int downscale) {
            for (int m; (m = NextMarker()) >= 0 && m != 0xD9;) {
                if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;
                if (_pos + 2 > d.Length) throw Truncated();
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(_pos));
                if (len < 2 || _pos + len > d.Length) throw Truncated();
                var s = new ReadOnlySpan<byte>(d, _pos + 2, len - 2);
                _pos += len;
                switch (m) {
                    case 0xC4: ReadHuffman(s); break;
                    case 0xDB: ReadQuant(s); break;
                    case 0xDD: _restart = s.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(s) : 0; break;
                    case 0xE1: ReadExif(s); break;
                    case 0xDA:
                        if (sizeOnly) return _frame;
                        ReadScan(s);
                        break;
                    case >= 0xC0 and <= 0xCF and not 0xC8 and not 0xCC:
                        if (s.Length < 6) throw Truncated();
                        // a second frame would replace the components the planes were allocated for
                        if (_frame) throw new ImageFormatException("JPEG has more than one frame header.");
                        _h = BinaryPrimitives.ReadUInt16BigEndian(s[1..]);
                        _w = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
                        if (sizeOnly) return true;
                        if (m == 0xC2) throw new ImageFormatException("Progressive JPEG files are not supported.");
                        if (m > 0xC1) throw new ImageFormatException("Only baseline JPEG files are supported.");
                        ReadFrame(s);
                        _scale = downscale >= 8 ? 8 : downscale >= 4 ? 4 : downscale >= 2 ? 2 : 1;
                        break;
                }
            }
            return _frame;
        }

        int NextMarker() {
            while (_pos + 1 < d.Length) {
                if (d[_pos] == 0xFF && d[_pos + 1] != 0 && d[_pos + 1] != 0xFF) {
                    _pos += 2;
                    return d[_pos - 1];
                }
                _pos++;
            }
            return -1;
        }

        static ImageFormatException Truncated() => new("JPEG data is truncated or corrupt.");

        void ReadFrame(ReadOnlySpan<byte> s) {
            int n = s[5];
            if (s[0] != 8) throw new ImageFormatException("Only 8-bit JPEG files are supported.");
            if (_w == 0 || _h == 0 || (n != 1 && n != 3) || s.Length < 6 + n * 3) throw new ImageFormatException("Unsupported JPEG frame.");
            _comps.Clear();
            _maxH = _maxV = 1;
            for (int i = 0; i < n; i++) {
                var c = new Component { Id = s[6 + i * 3], H = s[7 + i * 3] >> 4, V = s[7 + i * 3] & 15, Tq = s[8 + i * 3] & 3 };
                if (c.H is < 1 or > 4 || c.V is < 1 or > 4) throw new ImageFormatException("Unsupported JPEG sampling.");
                _maxH = Math.Max(_maxH, c.H);
                _maxV = Math.Max(_maxV, c.V);
                _comps.Add(c);
            }
            foreach (var c in _comps)
                if (_maxH % c.H != 0 || _maxV % c.V != 0) throw new ImageFormatException("Unsupported JPEG sampling.");
            _frame = true;
        }

        void ReadQuant(ReadOnlySpan<byte> s) {
            while (s.Length > 0) {
                int precision = s[0] >> 4, id = s[0] & 15, size = precision == 0 ? 64 : 128;
                if (id > 3 || precision > 1 || s.Length < 1 + size) throw Truncated();
                var q = new ushort[64];
                var f = new float[64];
                for (int k = 0; k < 64; k++) q[ZigZag[k]] = precision == 0 ? s[1 + k] : BinaryPrimitives.ReadUInt16BigEndian(s[(1 + 2 * k)..]);
                for (int i = 0; i < 64; i++) f[i] = q[i] * Aan[i >> 3] * Aan[i & 7] * 0.125f;
                _q[id] = q;
                _fq[id] = f;
                s = s[(1 + size)..];
            }
        }

        void ReadHuffman(ReadOnlySpan<byte> s) {
            while (s.Length > 0) {
                if (s.Length < 17) throw Truncated();
                int tc = s[0] >> 4, id = s[0] & 15, n = 0;
                for (int i = 1; i <= 16; i++) n += s[i];
                if (tc > 1 || id > 3 || s.Length < 17 + n) throw Truncated();
                (tc == 0 ? _dc : _ac)[id] = new Huffman(s.Slice(1, 16), s.Slice(17, n), tc == 1);
                s = s[(17 + n)..];
            }
        }

        void ReadExif(ReadOnlySpan<byte> s) {
            if (s.Length < 14 || !s[..6].SequenceEqual("Exif\0\0"u8)) return;
            var t = s[6..];
            bool le = t[0] == (byte)'I';
            int ifd = (int)Read32(t, 4, le);
            if (ifd < 8 || ifd + 2 > t.Length) return;
            int count = Read16(t, ifd, le);
            for (int i = 0, e = ifd + 2; i < count && e + 12 <= t.Length; i++, e += 12) {
                if (Read16(t, e, le) != 0x0112) continue;
                int o = Read16(t, e + 8, le);
                if (o is >= 1 and <= 8) _orientation = o;
                return;
            }
        }

        static int Read16(ReadOnlySpan<byte> s, int o, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt16BigEndian(s[o..]);
        static uint Read32(ReadOnlySpan<byte> s, int o, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt32BigEndian(s[o..]);

        void ReadScan(ReadOnlySpan<byte> s) {
            int n = s.Length > 0 ? s[0] : 0;
            if (!_frame || n < 1 || n > _comps.Count || s.Length < 4 + 2 * n) throw Truncated();
            var scan = new Component[n];
            for (int i = 0; i < n; i++) {
                int id = s[1 + 2 * i];
                var c = _comps.Find(x => x.Id == id) ?? throw Truncated();
                c.Td = s[2 + 2 * i] >> 4;
                c.Ta = s[2 + 2 * i] & 15;
                if (c.Td > 3 || c.Ta > 3 || _dc[c.Td] == null || _ac[c.Ta] == null || _q[c.Tq] == null)
                    throw new ImageFormatException("JPEG tables are missing.");
                scan[i] = c;
            }
            if (s[1 + 2 * n] != 0 || s[2 + 2 * n] != 63 || s[3 + 2 * n] != 0) throw new ImageFormatException("Progressive JPEG files are not supported.");
            if (!_ready) Allocate();
            DecodeScan(scan);
            _scanned = true;
        }

        void Allocate() {
            ImageLimits.ThrowIfTooLarge(Ceil(_w, _scale), Ceil(_h, _scale));
            _n = 8 / _scale;
            int cols = Ceil(_w, 8 * _maxH), rows = Ceil(_h, 8 * _maxV);
            foreach (var c in _comps) {
                c.Stride = cols * c.H * _n;
                c.Plane = new byte[checked(c.Stride * rows * c.V * _n)];
                c.Width = Ceil(Ceil(_w * c.H, _maxH), _scale);
                c.Height = Ceil(Ceil(_h * c.V, _maxV), _scale);
            }
            _ready = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void DecodeScan(Component[] scan) {
            _bits = 0;
            _nbits = _padding = 0;
            _marker = false;
            foreach (var c in scan) c.Pred = 0;
            int* blk = stackalloc int[64];
            new Span<int>(blk, 64).Clear();
            float* ws = stackalloc float[64];
            int left = _restart;
            if (scan.Length == 1) {
                var c = scan[0];
                int bw = Ceil(Ceil(_w * c.H, _maxH), 8), bh = Ceil(Ceil(_h * c.V, _maxV), 8);
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++) {
                        if (_restart > 0 && left-- == 0) { Restart(scan); left = _restart - 1; }
                        Block(c, blk, ws, bx, by);
                    }
                return;
            }
            int cols = Ceil(_w, 8 * _maxH), rows = Ceil(_h, 8 * _maxV);
            for (int my = 0; my < rows; my++)
                for (int mx = 0; mx < cols; mx++) {
                    if (_restart > 0 && left-- == 0) { Restart(scan); left = _restart - 1; }
                    foreach (var c in scan)
                        for (int v = 0; v < c.V; v++)
                            for (int h = 0; h < c.H; h++) Block(c, blk, ws, mx * c.H + h, my * c.V + v);
                }
        }

        void Restart(Component[] scan) {
            int at = _pos;
            _bits = 0;
            _nbits = _padding = 0;
            _marker = false;
            if (NextMarker() is < 0xD0 or > 0xD7) _pos = at;
            foreach (var c in scan) c.Pred = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Fill() {
            while (_nbits <= 56) {
                uint b = 0;
                if (!_marker && _pos < d.Length) {
                    b = d[_pos];
                    if (b != 0xFF) _pos++;
                    else if (_pos + 1 < d.Length && d[_pos + 1] == 0) _pos += 2;
                    else { _marker = true; b = 0; _padding++; }
                } else {
                    _padding++;
                }
                _bits |= (ulong)b << (56 - _nbits);
                _nbits += 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Bits(int n) {
            int v = (int)(_bits >> (64 - n));
            _bits <<= n;
            _nbits -= n;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Extend(int v, int t) => v < 1 << (t - 1) ? v - (1 << t) + 1 : v;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int Symbol(Huffman h) {
            int f = h.Fast[(int)(_bits >> 55)];
            if (f != 0) {
                _bits <<= f >> 8;
                _nbits -= f >> 8;
                return f & 255;
            }
            uint c = (uint)(_bits >> 48);
            int len = 10;
            while (c >= h.MaxCode[len]) len++;
            if (len > 16) throw new ImageFormatException("Invalid JPEG Huffman code.");
            _bits <<= len;
            _nbits -= len;
            return h.Values[(int)(c >> (16 - len)) + h.Delta[len]];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Block(Component c, int* blk, float* ws, int bx, int by) {
            bool any = false;
            // once a block has read past the data, the rest of the scan (or of its restart interval) stays grey, as
            // libjpeg leaves it: a few bytes cannot make it decode a whole picture of padding
            if (_padding * 8 <= _nbits) {
                var ac = _ac[c.Ta]!;
                if (_nbits < 32) Fill();
                int t = Symbol(_dc[c.Td]!) & 15;
                if (t != 0) c.Pred += Extend(Bits(t), t);
                blk[0] = c.Pred;
                for (int k = 1; k < 64;) {
                    if (_nbits < 32) Fill();
                    int f = ac.FastAc[(int)(_bits >> 55)];
                    if (f != 0) {
                        k += (f >> 4) & 15;
                        _bits <<= f & 15;
                        _nbits -= f & 15;
                        blk[ZigZag[k++]] = f >> 8;
                        any = true;
                        continue;
                    }
                    int rs = Symbol(ac), s = rs & 15;
                    if (s == 0) {
                        if (rs != 0xF0) break;
                        k += 16;
                        continue;
                    }
                    k += rs >> 4;
                    blk[ZigZag[k++]] = Extend(Bits(s), s);
                    any = true;
                }
            }
            int n = _n;
            fixed (byte* plane = c.Plane)
            fixed (float* fq = _fq[c.Tq]) {
                byte* dst = plane + by * n * c.Stride + bx * n;
                if (!any || n == 1) {
                    byte v = Clamp(blk[0] * fq[0] + 128.5f);
                    for (int y = 0; y < n; y++) new Span<byte>(dst + y * c.Stride, n).Fill(v);
                } else if (n == 8) Idct8(blk, fq, dst, c.Stride, ws);
                else IdctN(blk, _q[c.Tq]!, n, dst, c.Stride);
            }
            if (any) new Span<int>(blk, 64).Clear();
            else blk[0] = 0;
        }

        InternalImage Output() {
            int w = Ceil(_w, _scale), h = Ceil(_h, _scale);
            var rgba = GC.AllocateUninitializedArray<byte>(checked(w * h * 4));
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            void Band(int band) {
                int y0 = band * 16, y1 = Math.Min(h, y0 + 16);
                int[] sum = new int[w];
                byte[] b0 = new byte[w], b1 = new byte[w], b2 = new byte[w];
                fixed (byte* o = rgba)
                    for (int y = y0; y < y1; y++) {
                        uint* dst = (uint*)(o + (long)y * w * 4);
                        if (_comps.Count == 1) {
                            fixed (byte* g = Sample(_comps[0], y, w, sum, b0))
                                for (int x = 0; x < w; x++) dst[x] = g[x] * 0x010101u | 0xFF000000u;
                            continue;
                        }
                        fixed (byte* py = Sample(_comps[0], y, w, sum, b0), pb = Sample(_comps[1], y, w, sum, b1), pr = Sample(_comps[2], y, w, sum, b2))
                            YccToRgba(py, pb, pr, dst, w);
                    }
            }
            int bands = Ceil(h, 16);
            if (InternalImage.ShouldParallelize(w, h)) Parallel.For(0, bands, Band);
            else for (int i = 0; i < bands; i++) Band(i);
            return new InternalImage(w, h, rgba);
        }

        // a component's row at full width: chroma is upsampled with libjpeg's triangle filter
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        ReadOnlySpan<byte> Sample(Component c, int y, int w, int[] sum, byte[] buf) {
            int hr = _maxH / c.H, vr = _maxV / c.V, cw = c.Width;
            int cy = Math.Min(y / vr, c.Height - 1);
            var near = c.Plane.AsSpan(cy * c.Stride, c.Stride);
            if (hr == 1 && vr == 1) return near[..w];
            if (vr == 2) {
                var far = c.Plane.AsSpan(Math.Clamp((y & 1) == 0 ? cy - 1 : cy + 1, 0, c.Height - 1) * c.Stride, cw);
                for (int i = 0; i < cw; i++) sum[i] = 3 * near[i] + far[i];
            } else {
                for (int i = 0; i < cw; i++) sum[i] = near[i] << 2;
            }
            if (hr == 2) {
                buf[0] = (byte)((sum[0] * 4 + 8) >> 4);
                for (int i = 0, x = 0; i < cw; i++, x += 2) {
                    if (x > 0 && x < w) buf[x] = (byte)((3 * sum[i] + sum[i - 1] + 8) >> 4);
                    if (x + 1 < w) buf[x + 1] = (byte)((3 * sum[i] + sum[Math.Min(i + 1, cw - 1)] + 7) >> 4);
                }
            } else {
                for (int x = 0; x < w; x++) buf[x] = (byte)((sum[x / hr] + 2) >> 2);
            }
            return buf.AsSpan(0, w);
        }

        InternalImage Orient(InternalImage img) => _orientation switch {
            2 => img.FlipHorizontal(),
            3 => img.Rotate180(),
            4 => img.FlipVertical(),
            5 => img.Rotate90Clockwise().FlipHorizontal(),
            6 => img.Rotate90Clockwise(),
            7 => img.Rotate90CounterClockwise().FlipHorizontal(),
            8 => img.Rotate90CounterClockwise(),
            _ => img,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void YccToRgba(byte* py, byte* pb, byte* pr, uint* dst, int w) {
        int x = 0;
        if (Vector.IsHardwareAccelerated) {
            int lanes = Vector<int>.Count;
            for (; x <= w - Vector<byte>.Count; x += Vector<byte>.Count) {
                Widen(py + x, out var y0, out var y1, out var y2, out var y3);
                Widen(pb + x, out var b0, out var b1, out var b2, out var b3);
                Widen(pr + x, out var r0, out var r1, out var r2, out var r3);
                Ycc8(dst + x, y0, b0, r0);
                Ycc8(dst + x + lanes, y1, b1, r1);
                Ycc8(dst + x + 2 * lanes, y2, b2, r2);
                Ycc8(dst + x + 3 * lanes, y3, b3, r3);
            }
        }
        for (; x < w; x++) {
            int yy = py[x], cb = pb[x], cr = pr[x];
            dst[x] = Clip(yy + CrR[cr]) | Clip(yy + ((CbG[cb] + CrG[cr]) >> 16)) << 8 | Clip(yy + CbB[cb]) << 16 | 0xFF000000u;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Widen(byte* p, out Vector<int> a, out Vector<int> b, out Vector<int> c, out Vector<int> d) {
        Vector.Widen(Unsafe.ReadUnaligned<Vector<byte>>(p), out Vector<ushort> low, out Vector<ushort> high);
        Vector.Widen(low, out Vector<uint> ua, out Vector<uint> ub);
        Vector.Widen(high, out Vector<uint> uc, out Vector<uint> ud);
        (a, b, c, d) = (Vector.AsVectorInt32(ua), Vector.AsVectorInt32(ub), Vector.AsVectorInt32(uc), Vector.AsVectorInt32(ud));
    }

    // the same arithmetic as the tables, a lane per pixel
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Ycc8(uint* dst, Vector<int> y, Vector<int> cb, Vector<int> cr) {
        cb -= new Vector<int>(128);
        cr -= new Vector<int>(128);
        var half = new Vector<int>(32768);
        var r = y + Vector.ShiftRightArithmetic(cr * 91881 + half, 16);
        var g = y + Vector.ShiftRightArithmetic(cb * -22554 - cr * 46802 + half, 16);
        var b = y + Vector.ShiftRightArithmetic(cb * 116130 + half, 16);
        var max = new Vector<int>(255);
        r = Vector.Min(Vector.Max(r, Vector<int>.Zero), max);
        g = Vector.Min(Vector.Max(g, Vector<int>.Zero), max);
        b = Vector.Min(Vector.Max(b, Vector<int>.Zero), max);
        var rgba = Vector.AsVectorUInt32(r) | Vector.ShiftLeft(Vector.AsVectorUInt32(g), 8) | Vector.ShiftLeft(Vector.AsVectorUInt32(b), 16) | new Vector<uint>(0xFF000000u);
        Unsafe.WriteUnaligned(dst, rgba);
    }

    // AAN float IDCT (as libjpeg's jidctflt), coefficients in natural order: the columns four at a time, then the rows
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Idct8(int* c, float* q, byte* dst, int stride, float* ws) {
        var c2 = Vector128.Create(1.414213562f);
        var c5 = Vector128.Create(1.847759065f);
        var c10 = Vector128.Create(1.082392200f);
        var c12 = Vector128.Create(2.613125930f);
        for (int h = 0; h < 8; h += 4) {
            int* p = c + h;
            float* f = q + h;
            var t0 = Vector128.ConvertToSingle(Vector128.Load(p)) * Vector128.Load(f);
            var t1 = Vector128.ConvertToSingle(Vector128.Load(p + 16)) * Vector128.Load(f + 16);
            var t2 = Vector128.ConvertToSingle(Vector128.Load(p + 32)) * Vector128.Load(f + 32);
            var t3 = Vector128.ConvertToSingle(Vector128.Load(p + 48)) * Vector128.Load(f + 48);
            var t10 = t0 + t2;
            var t11 = t0 - t2;
            var t13 = t1 + t3;
            var t12 = (t1 - t3) * c2 - t13;
            t0 = t10 + t13; t3 = t10 - t13; t1 = t11 + t12; t2 = t11 - t12;
            var t4 = Vector128.ConvertToSingle(Vector128.Load(p + 8)) * Vector128.Load(f + 8);
            var t5 = Vector128.ConvertToSingle(Vector128.Load(p + 24)) * Vector128.Load(f + 24);
            var t6 = Vector128.ConvertToSingle(Vector128.Load(p + 40)) * Vector128.Load(f + 40);
            var t7 = Vector128.ConvertToSingle(Vector128.Load(p + 56)) * Vector128.Load(f + 56);
            var z13 = t6 + t5;
            var z10 = t6 - t5;
            var z11 = t4 + t7;
            var z12 = t4 - t7;
            t7 = z11 + z13;
            t11 = (z11 - z13) * c2;
            var z5 = (z10 + z12) * c5;
            t10 = z5 - z12 * c10;
            t12 = z5 - z10 * c12;
            t6 = t12 - t7; t5 = t11 - t6; t4 = t10 - t5;
            float* w = ws + h;
            (t0 + t7).Store(w); (t0 - t7).Store(w + 56); (t1 + t6).Store(w + 8); (t1 - t6).Store(w + 48);
            (t2 + t5).Store(w + 16); (t2 - t5).Store(w + 40); (t3 + t4).Store(w + 24); (t3 - t4).Store(w + 32);
        }
        for (int i = 0; i < 8; i++) {
            float* w = ws + i * 8;
            float z5 = w[0] + 128.5f;
            float t10 = z5 + w[4], t11 = z5 - w[4], t13 = w[2] + w[6], t12 = (w[2] - w[6]) * 1.414213562f - t13;
            float t0 = t10 + t13, t3 = t10 - t13, t1 = t11 + t12, t2 = t11 - t12;
            float z13 = w[5] + w[3], z10 = w[5] - w[3], z11 = w[1] + w[7], z12 = w[1] - w[7];
            float t7 = z11 + z13;
            t11 = (z11 - z13) * 1.414213562f;
            z5 = (z10 + z12) * 1.847759065f;
            t10 = z5 - z12 * 1.082392200f;
            t12 = z5 - z10 * 2.613125930f;
            float t6 = t12 - t7, t5 = t11 - t6, t4 = t10 - t5;
            byte* o = dst + i * stride;
            o[0] = Clamp(t0 + t7); o[7] = Clamp(t0 - t7); o[1] = Clamp(t1 + t6); o[6] = Clamp(t1 - t6);
            o[2] = Clamp(t2 + t5); o[5] = Clamp(t2 - t5); o[3] = Clamp(t3 + t4); o[4] = Clamp(t3 - t4);
        }
    }

    // an n-point IDCT of the lowest n x n coefficients: the block at 1/2 or 1/4 of its size
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void IdctN(int* c, ushort[] q, int n, byte* dst, int stride) {
        float[] t = Cos[n];
        float* tmp = stackalloc float[16];
        for (int v = 0; v < n; v++)
            for (int x = 0; x < n; x++) {
                float s = 0;
                for (int u = 0; u < n; u++) s += c[v * 8 + u] * q[v * 8 + u] * t[u * n + x];
                tmp[v * n + x] = s;
            }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) {
                float s = 128.5f;
                for (int v = 0; v < n; v++) s += tmp[v * n + x] * t[v * n + y];
                dst[y * stride + x] = Clamp(s);
            }
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    public void Encode(InternalImage image, Stream stream, ImageSaveOptions options) {
        int quality = Math.Clamp(options.Quality, 1, 100), w = image.Width, h = image.Height;
        if (w > 65535 || h > 65535) throw new ArgumentOutOfRangeException(nameof(image), "JPEG images are limited to 65535 x 65535 pixels.");
        bool sub = quality < 90; // 4:2:0 below 90, as most encoders do
        int size = sub ? 16 : 8, per = sub ? 6 : 3, cols = Ceil(w, size), rows = Ceil(h, size);
        int[] qy = Quant(LuminanceQuant, quality), qc = Quant(ChrominanceQuant, quality);
        float[] dy = Divisors(qy), dc = Divisors(qc);
        var blocks = new short[checked(cols * rows * per * 64)];
        void Row(int my) {
            for (int mx = 0; mx < cols; mx++) Mcu(image, mx, my, sub, dy, dc, blocks.AsSpan((my * cols + mx) * per * 64, per * 64));
        }
        if (InternalImage.ShouldParallelize(w, h)) Parallel.For(0, rows, Row);
        else for (int y = 0; y < rows; y++) Row(y);

        int[][] freq = [new int[256], new int[256], new int[256], new int[256]];
        Entropy(blocks, sub, freq, null, null);
        var tables = freq.Select(Optimal).ToArray();

        stream.Write([0xFF, 0xD8]);
        Segment(stream, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        var dqt = new byte[130];
        dqt[65] = 1;
        for (int k = 0; k < 64; k++) {
            dqt[1 + k] = (byte)qy[ZigZag[k]];
            dqt[66 + k] = (byte)qc[ZigZag[k]];
        }
        Segment(stream, 0xDB, dqt);
        Segment(stream, 0xC0, [8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, (byte)(sub ? 0x22 : 0x11), 0, 2, 0x11, 1, 3, 0x11, 1]);
        var dht = new List<byte>();
        for (int t = 0; t < 4; t++) {
            dht.Add((byte)((t & 1) << 4 | t >> 1));
            dht.AddRange(tables[t].Counts);
            dht.AddRange(tables[t].Values);
        }
        Segment(stream, 0xC4, CollectionsMarshal.AsSpan(dht));
        Segment(stream, 0xDA, [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
        var writer = new BitWriter(Math.Max(4096, w * h / 4));
        Entropy(blocks, sub, null, tables.Select(t => new Codes(t.Counts, t.Values)).ToArray(), writer);
        writer.Flush();
        stream.Write(writer.Buffer, 0, writer.Length);
        stream.Write([0xFF, 0xD9]);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Mcu(InternalImage image, int mx, int my, bool sub, float[] dy, float[] dc, Span<short> output) {
        int w = image.Width, h = image.Height, size = sub ? 16 : 8;
        var px = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        float* y = stackalloc float[256], cb = stackalloc float[64], cr = stackalloc float[64], blk = stackalloc float[64];
        new Span<float>(cb, 64).Clear();
        new Span<float>(cr, 64).Clear();
        float share = sub ? 0.25f : 1f;
        fixed (uint* pixels = px)
        for (int j = 0; j < size; j++) {
            uint* row = pixels + Math.Min(my * size + j, h - 1) * w;
            for (int i = 0; i < size; i++) {
                uint p = row[Math.Min(mx * size + i, w - 1)];
                int r = (byte)p, g = (byte)(p >> 8), b = (byte)(p >> 16), a = (int)(p >> 24);
                if (a != 255) { // flattened onto white
                    int white = 255 * (255 - a) + 127;
                    r = (r * a + white) / 255;
                    g = (g * a + white) / 255;
                    b = (b * a + white) / 255;
                }
                y[j * size + i] = 0.299f * r + 0.587f * g + 0.114f * b - 128;
                int k = sub ? (j >> 1) * 8 + (i >> 1) : j * 8 + i;
                cb[k] += (-0.168736f * r - 0.331264f * g + 0.5f * b) * share;
                cr[k] += (0.5f * r - 0.418688f * g - 0.081312f * b) * share;
            }
        }
        int lumas = sub ? 4 : 1;
        for (int n = 0; n < lumas; n++) {
            float* src = y + (n >> 1) * 8 * size + (n & 1) * 8;
            for (int j = 0; j < 8; j++) new Span<float>(src + j * size, 8).CopyTo(new Span<float>(blk + j * 8, 8));
            Fdct(blk, dy, output.Slice(n * 64, 64));
        }
        Fdct(cb, dc, output.Slice(lumas * 64, 64));
        Fdct(cr, dc, output.Slice(lumas * 64 + 64, 64));
    }

    // AAN float forward DCT (as libjpeg's jfdctflt): the columns four at a time, then the rows; quantized into zigzag order
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Fdct(float* d, float[] div, Span<short> output) {
        var c0 = Vector128.Create(0.707106781f);
        var c1 = Vector128.Create(0.382683433f);
        var c2 = Vector128.Create(0.541196100f);
        var c3 = Vector128.Create(1.306562965f);
        for (int h = 0; h < 8; h += 4) {
            float* p = d + h;
            var d0 = Vector128.Load(p); var d1 = Vector128.Load(p + 8); var d2 = Vector128.Load(p + 16); var d3 = Vector128.Load(p + 24);
            var d4 = Vector128.Load(p + 32); var d5 = Vector128.Load(p + 40); var d6 = Vector128.Load(p + 48); var d7 = Vector128.Load(p + 56);
            var t0 = d0 + d7; var t7 = d0 - d7; var t1 = d1 + d6; var t6 = d1 - d6;
            var t2 = d2 + d5; var t5 = d2 - d5; var t3 = d3 + d4; var t4 = d3 - d4;
            var t10 = t0 + t3; var t13 = t0 - t3; var t11 = t1 + t2; var t12 = t1 - t2;
            (t10 + t11).Store(p);
            (t10 - t11).Store(p + 32);
            var z1 = (t12 + t13) * c0;
            (t13 + z1).Store(p + 16);
            (t13 - z1).Store(p + 48);
            t10 = t4 + t5; t11 = t5 + t6; t12 = t6 + t7;
            var z5 = (t10 - t12) * c1;
            var z2 = c2 * t10 + z5;
            var z4 = c3 * t12 + z5;
            var z3 = t11 * c0;
            var z11 = t7 + z3;
            var z13 = t7 - z3;
            (z13 + z2).Store(p + 40);
            (z13 - z2).Store(p + 24);
            (z11 + z4).Store(p + 8);
            (z11 - z4).Store(p + 56);
        }
        for (int i = 0; i < 64; i += 8) FdctRow(d + i);
        for (int k = 0; k < 64; k++) {
            int z = ZigZag[k];
            output[k] = (short)(float.ConvertToIntegerNative<int>(d[z] * div[z] + 16384.5f) - 16384);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void FdctRow(float* p) {
        float t0 = p[0] + p[7], t7 = p[0] - p[7], t1 = p[1] + p[6], t6 = p[1] - p[6];
        float t2 = p[2] + p[5], t5 = p[2] - p[5], t3 = p[3] + p[4], t4 = p[3] - p[4];
        float t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
        p[0] = t10 + t11;
        p[4] = t10 - t11;
        float z1 = (t12 + t13) * 0.707106781f;
        p[2] = t13 + z1;
        p[6] = t13 - z1;
        t10 = t4 + t5; t11 = t5 + t6; t12 = t6 + t7;
        float z5 = (t10 - t12) * 0.382683433f, z2 = 0.541196100f * t10 + z5, z4 = 1.306562965f * t12 + z5, z3 = t11 * 0.707106781f;
        float z11 = t7 + z3, z13 = t7 - z3;
        p[5] = z13 + z2;
        p[3] = z13 - z2;
        p[1] = z11 + z4;
        p[7] = z11 - z4;
    }

    // counts the symbols (freq) or writes them (codes): tables 0/1 are luminance DC/AC, 2/3 chrominance
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Entropy(short[] blocks, bool sub, int[][]? freq, Codes[]? codes, BitWriter? writer) {
        int per = sub ? 6 : 3, lumas = sub ? 4 : 1;
        Span<int> pred = stackalloc int[3];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Put(int table, int symbol, int value, int bits) {
            if (writer == null) freq![table][symbol]++;
            else writer.Put(codes![table].Code[symbol] << bits | (value & ((1 << bits) - 1)), codes[table].Size[symbol] + bits);
        }
        for (int at = 0, i = 0; at < blocks.Length; at += 64, i = i + 1 == per ? 0 : i + 1) {
            int comp = i < lumas ? 0 : i - lumas + 1, t = comp == 0 ? 0 : 2;
            var blk = blocks.AsSpan(at, 64);
            int diff = blk[0] - pred[comp];
            pred[comp] = blk[0];
            int cat = Category(diff);
            Put(t, cat, diff < 0 ? diff - 1 : diff, cat);
            int last = blk[1..].LastIndexOfAnyExcept((short)0) + 1, run = 0;
            for (int k = 1; k <= last; k++) {
                int v = blk[k];
                if (v == 0) { run++; continue; }
                for (; run >= 16; run -= 16) Put(t + 1, 0xF0, 0, 0);
                cat = Category(v);
                Put(t + 1, run << 4 | cat, v < 0 ? v - 1 : v, cat);
                run = 0;
            }
            if (last < 63) Put(t + 1, 0, 0, 0);
        }
    }

    static int Category(int v) => v == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(v));

    static int[] Quant(byte[] source, int quality) {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var table = new int[64];
        for (int i = 0; i < 64; i++) table[i] = Math.Clamp((source[i] * scale + 50) / 100, 1, 255);
        return table;
    }

    static float[] Divisors(int[] q) {
        var d = new float[64];
        for (int i = 0; i < 64; i++) d[i] = 1f / (q[i] * Aan[i >> 3] * Aan[i & 7] * 8f);
        return d;
    }

    // the optimal length-limited Huffman table for the counted symbols, as libjpeg's jpeg_gen_optimal_table
    static (byte[] Counts, byte[] Values) Optimal(int[] counted) {
        var freq = new long[257];
        for (int i = 0; i < 256; i++) freq[i] = counted[i];
        freq[256] = 1; // reserves the all-ones code
        var size = new int[257];
        var next = new int[257];
        Array.Fill(next, -1);
        while (true) {
            int c1 = Smallest(freq, -1), c2 = c1 < 0 ? -1 : Smallest(freq, c1);
            if (c2 < 0) break;
            freq[c1] += freq[c2];
            freq[c2] = 0;
            size[c1]++;
            while (next[c1] >= 0) { c1 = next[c1]; size[c1]++; }
            next[c1] = c2;
            size[c2]++;
            while (next[c2] >= 0) { c2 = next[c2]; size[c2]++; }
        }
        var bits = new int[64];
        for (int i = 0; i <= 256; i++) if (size[i] > 0) bits[size[i]]++;
        for (int i = 63; i > 16; i--)
            while (bits[i] > 0) {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        int longest = 16;
        while (bits[longest] == 0) longest--;
        bits[longest]--;
        var counts = new byte[16];
        for (int i = 1; i <= 16; i++) counts[i - 1] = (byte)bits[i];
        var values = new List<byte>();
        for (int len = 1; len < 64; len++)
            for (int s = 0; s < 256; s++)
                if (size[s] == len) values.Add((byte)s);
        return (counts, values.ToArray());
    }

    static int Smallest(long[] freq, int skip) {
        int c = -1;
        long v = long.MaxValue;
        for (int i = 0; i < freq.Length; i++)
            if (freq[i] != 0 && freq[i] <= v && i != skip) { v = freq[i]; c = i; }
        return c;
    }

    static void Segment(Stream stream, int marker, ReadOnlySpan<byte> payload) {
        Span<byte> head = [0xFF, (byte)marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)];
        stream.Write(head);
        stream.Write(payload);
    }

    sealed class Codes {
        public readonly int[] Code = new int[256], Size = new int[256];
        public Codes(byte[] counts, byte[] values) {
            for (int len = 1, code = 0, k = 0; len <= 16; len++, code <<= 1)
                for (int i = 0; i < counts[len - 1]; i++, k++) {
                    Code[values[k]] = code++;
                    Size[values[k]] = len;
                }
        }
    }

    sealed class BitWriter(int capacity) {
        public byte[] Buffer = new byte[capacity];
        public int Length;
        ulong _acc;
        int _n;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(int bits, int count) {
            _acc = _acc << count | (uint)bits;
            if ((_n += count) >= 32) Drain();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Drain() {
            while (_n >= 8) {
                byte b = (byte)(_acc >> (_n -= 8));
                if (Length + 2 > Buffer.Length) Array.Resize(ref Buffer, Buffer.Length * 2);
                Buffer[Length++] = b;
                if (b == 0xFF) Buffer[Length++] = 0;
            }
        }

        public void Flush() {
            int pad = -_n & 7;
            if (pad > 0) Put((1 << pad) - 1, pad);
            Drain();
        }
    }
}
