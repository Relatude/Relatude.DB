using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// CMYK to sRGB through the A2B0 (perceptual) table of a picture's ICC profile, as Skia, the browsers and littleCMS
/// render such pictures: lut8, lut16 and lutAtoB tables, Lab or XYZ connection space. The profile is sampled once into
/// a 17^4 table of sRGB, cached per profile, which pixels interpolate: tetrahedral in C, M, Y and linear in K.
/// </summary>
internal sealed unsafe class CmykProfile {
    const int G = 17, SK = 3, SY = G * SK, SM = G * SY, SC = G * SM;
    // by stored value (Adobe's: 255 is no ink), the cell the ink is in and how far into it, in 1/256s
    static readonly int[] Cell = new int[256], Frac = new int[256];
    static readonly ConcurrentDictionary<string, Lazy<CmykProfile?>> Cache = new();
    // XYZ (D50, the connection space's white) to linear sRGB: the inverse of sRGB's colorants adapted to D50
    static readonly float[] ToSrgb = Invert([0.4360747f, 0.3850649f, 0.1430804f, 0.2225045f, 0.7168786f, 0.0606169f, 0.0139322f, 0.0971045f, 0.7141733f]);
    readonly ushort[] _lut = new ushort[G * SC]; // [c][m][y][k] by ink: r, g, b in 1/256s

    static CmykProfile() {
        for (int v = 0; v < 256; v++) {
            int pos = (255 - v) * (G - 1) * 256 / 255, cell = Math.Min(pos >> 8, G - 2);
            Cell[v] = cell;
            Frac[v] = pos - cell * 256;
        }
    }

    /// <summary>The conversion an ICC profile describes, or null when it is not one this can read.</summary>
    public static CmykProfile? For(byte[] icc) {
        if (!FindA2B(icc, out int offset, out int length, out bool lab)) return null;
        var key = Convert.ToHexString(SHA256.HashData(icc.AsSpan(offset, length))) + lab;
        if (Cache.Count > 16) Cache.Clear();
        return Cache.GetOrAdd(key, _ => new(() => Build(icc.AsSpan(offset, length).ToArray(), lab))).Value;
    }

    static bool FindA2B(byte[] p, out int offset, out int length, out bool lab) {
        offset = length = 0;
        lab = p.Length >= 24 && p.AsSpan(20, 4).SequenceEqual("Lab "u8);
        if (p.Length < 132 || !p.AsSpan(16, 4).SequenceEqual("CMYK"u8) || !(lab || p.AsSpan(20, 4).SequenceEqual("XYZ "u8))) return false;
        long count = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(128));
        if (132 + count * 12 > p.Length) return false;
        foreach (var name in new[] { "A2B0"u8.ToArray(), "A2B1"u8.ToArray() })
            for (int i = 0; i < count; i++) {
                var e = p.AsSpan(132 + i * 12, 12);
                if (!e[..4].SequenceEqual(name)) continue;
                long o = BinaryPrimitives.ReadUInt32BigEndian(e[4..]), n = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
                if (o + n > p.Length || n < 12) return false;
                (offset, length) = ((int)o, (int)n);
                return true;
            }
        return false;
    }

    static CmykProfile? Build(byte[] tag, bool lab) {
        Pipeline? p;
        try {
            p = Pipeline.Read(tag, lab);
        } catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) {
            p = null; // a table running past its tag, should a check have missed it
        }
        return p == null ? null : new CmykProfile(p);
    }

    CmykProfile(Pipeline p) {
        Parallel.For(0, G, c => {
            Span<float> x = stackalloc float[4], o = stackalloc float[3];
            for (int m = 0; m < G; m++)
                for (int y = 0; y < G; y++)
                    for (int k = 0; k < G; k++) {
                        x[0] = c / (G - 1f); x[1] = m / (G - 1f); x[2] = y / (G - 1f); x[3] = k / (G - 1f);
                        p.Eval(x, o);
                        int i = c * SC + m * SM + y * SY + k * SK;
                        for (int ch = 0; ch < 3; ch++) _lut[i + ch] = (ushort)MathF.Round(Encode(o[ch]) * 255 * 256);
                    }
        });
    }

    static float Encode(float v) => !(v > 0) ? 0 : v >= 1 ? 1 : v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;

    /// <summary>A row of stored C, M, Y (in dst's red, green and blue; inverted for YCCK) and K to sRGB.</summary>
    public void Apply(uint* dst, byte* kp, int w, bool invert) {
        uint flip = invert ? 0x00FFFFFFu : 0, lastIn = 0, lastOut = 0;
        bool any = false;
        fixed (ushort* lut = _lut)
            for (int x = 0; x < w; x++) {
                uint p = (dst[x] ^ flip) & 0xFFFFFF, key = p | (uint)kp[x] << 24;
                if (any && key == lastIn) { dst[x] = lastOut; continue; } // flat areas
                int c = (int)(p & 255), m = (int)(p >> 8 & 255), y = (int)(p >> 16), k = kp[x];
                int fc = Frac[c], fm = Frac[m], fy = Frac[y], fk = Frac[k];
                ushort* v = lut + Cell[c] * SC + Cell[m] * SM + Cell[y] * SY + Cell[k] * SK;
                // the tetrahedron the point is in: from the cell's first corner along the axes, largest fraction first
                int o1, o2, f1, f2, f3;
                if (fc >= fm) {
                    if (fm >= fy) (o1, o2, f1, f2, f3) = (SC, SC + SM, fc, fm, fy);
                    else if (fc >= fy) (o1, o2, f1, f2, f3) = (SC, SC + SY, fc, fy, fm);
                    else (o1, o2, f1, f2, f3) = (SY, SY + SC, fy, fc, fm);
                } else {
                    if (fc >= fy) (o1, o2, f1, f2, f3) = (SM, SM + SC, fm, fc, fy);
                    else if (fm >= fy) (o1, o2, f1, f2, f3) = (SM, SM + SY, fm, fy, fc);
                    else (o1, o2, f1, f2, f3) = (SY, SY + SM, fy, fm, fc);
                }
                uint w0 = (uint)(256 - f1), w1 = (uint)(f1 - f2), w2 = (uint)(f2 - f3), w3 = (uint)f3, k1 = (uint)fk, k0 = 256 - k1;
                const int o3 = SC + SM + SY;
                uint rgb = 0xFF000000u;
                for (int ch = 0; ch < 3; ch++) {
                    ushort* a = v + ch, b = a + SK;
                    uint lo = w0 * a[0] + w1 * a[o1] + w2 * a[o2] + w3 * a[o3], hi = w0 * b[0] + w1 * b[o1] + w2 * b[o2] + w3 * b[o3];
                    rgb |= (lo * k0 + hi * k1 + (1u << 23)) >> 24 << (ch * 8);
                }
                dst[x] = lastOut = rgb;
                lastIn = key;
                any = true;
            }
    }

    static float[] Invert(float[] m) {
        float a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return [(e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
                (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
                (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det];
    }

    // device CMYK (0..1 ink) to the connection space and on to linear sRGB
    sealed class Pipeline {
        Curve[] _a = [], _b = [];
        Curve[]? _m;
        float[]? _matrix;
        Clut _clut = null!;
        float _lScale = 100, _abScale = 255; // the connection space's encoding: Lab of lut16 tables is the legacy one
        bool _lab;

        public static Pipeline? Read(byte[] t, bool lab) {
            var type = t.AsSpan(0, 4);
            var p = new Pipeline { _lab = lab };
            if (type.SequenceEqual("mft2"u8) || type.SequenceEqual("mft1"u8)) {
                bool wide = type[3] == '2';
                int head = wide ? 52 : 48, size = wide ? 2 : 1;
                if (t.Length < head || t[8] != 4 || t[9] != 3 || t[10] < 2) return null;
                int n = wide ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(48)) : 256, m = wide ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(50)) : 256;
                long nodes = (long)t[10] * t[10] * t[10] * t[10] * 3;
                if (n < 2 || m < 2 || head + (4L * n + nodes + 3L * m) * size > t.Length) return null;
                int at = head;
                p._a = new Curve[4];
                for (int i = 0; i < 4; i++, at += n * size) p._a[i] = Curve.Table(t, at, n, size);
                p._clut = new Clut(t, at, [t[10], t[10], t[10], t[10]], size);
                at += (int)nodes * size;
                p._b = new Curve[3];
                for (int i = 0; i < 3; i++, at += m * size) p._b[i] = Curve.Table(t, at, m, size);
                if (wide && lab) (p._lScale, p._abScale) = (100 * 65535f / 65280, 65535 / 256f);
                return p;
            }
            if (!type.SequenceEqual("mAB "u8) || t.Length < 32 || t[8] != 4 || t[9] != 3) return null;
            uint offB = Read32(t, 12), offMatrix = Read32(t, 16), offM = Read32(t, 20), offClut = Read32(t, 24), offA = Read32(t, 28);
            if (offB == 0 || offClut == 0 || offA == 0 || offClut + 20L > t.Length) return null;
            var a = Curve.ReadAll(t, offA, 4);
            var b = Curve.ReadAll(t, offB, 3);
            if (a == null || b == null) return null;
            (p._a, p._b) = (a, b);
            if (offM != 0) {
                if (offMatrix == 0 || offMatrix + 48L > t.Length || (p._m = Curve.ReadAll(t, offM, 3)) == null) return null;
                p._matrix = new float[12];
                for (int i = 0; i < 12; i++) p._matrix[i] = (int)Read32(t, offMatrix + 4 * (uint)i) / 65536f;
            }
            int[] grid = [t[offClut], t[offClut + 1], t[offClut + 2], t[offClut + 3]];
            int precision = t[offClut + 16];
            if (grid.Any(g => g < 2) || precision is not (1 or 2)) return null;
            if (offClut + 20 + (long)grid[0] * grid[1] * grid[2] * grid[3] * 3 * precision > t.Length) return null;
            p._clut = new Clut(t, (int)offClut + 20, grid, precision);
            return p;
        }

        static uint Read32(byte[] t, uint at) => at + 4L <= t.Length ? BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan((int)at)) : throw new ArgumentOutOfRangeException(nameof(at));

        public void Eval(ReadOnlySpan<float> x, Span<float> o) {
            Span<float> c = stackalloc float[4];
            for (int i = 0; i < 4; i++) c[i] = _a[i].Eval(x[i]);
            _clut.Eval(c, o);
            if (_m != null && _matrix != null) {
                for (int i = 0; i < 3; i++) o[i] = _m[i].Eval(o[i]);
                var mt = _matrix;
                float r = mt[0] * o[0] + mt[1] * o[1] + mt[2] * o[2] + mt[9], g = mt[3] * o[0] + mt[4] * o[1] + mt[5] * o[2] + mt[10];
                (o[0], o[1], o[2]) = (r, g, mt[6] * o[0] + mt[7] * o[1] + mt[8] * o[2] + mt[11]);
            }
            for (int i = 0; i < 3; i++) o[i] = _b[i].Eval(o[i]);
            float X, Y, Z;
            if (_lab) {
                float l = o[0] * _lScale, fy = (l + 16) / 116, fx = fy + (o[1] * _abScale - 128) / 500, fz = fy - (o[2] * _abScale - 128) / 200;
                (X, Y, Z) = (0.9642f * Lab(fx), Lab(fy), 0.8249f * Lab(fz));
            } else {
                (X, Y, Z) = (o[0] * (65535 / 32768f), o[1] * (65535 / 32768f), o[2] * (65535 / 32768f));
            }
            var s = ToSrgb;
            o[0] = s[0] * X + s[1] * Y + s[2] * Z;
            o[1] = s[3] * X + s[4] * Y + s[5] * Z;
            o[2] = s[6] * X + s[7] * Y + s[8] * Z;
        }

        static float Lab(float t) => t > 6 / 29f ? t * t * t : 3 * (6 / 29f) * (6 / 29f) * (t - 4 / 29f);
    }

    sealed class Curve {
        float[]? _table;
        float[] _p = [];
        int _type = -1; // parametric function type; -1 a table, or none: identity

        public static Curve Table(byte[] t, int at, int n, int size) {
            var table = new float[n];
            for (int i = 0; i < n; i++)
                table[i] = size == 2 ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 2 * i)) / 65535f : t[at + i] / 255f;
            return new Curve { _table = table };
        }

        // the curves of a lutAtoB table, each padded to four bytes
        public static Curve[]? ReadAll(byte[] t, uint at, int count) {
            var curves = new Curve[count];
            for (int i = 0; i < count; i++) {
                if (at + 12L > t.Length) return null;
                var s = t.AsSpan((int)at);
                long length;
                if (s[..4].SequenceEqual("curv"u8)) {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(s[8..]);
                    length = 12 + 2L * n;
                    if (length > s.Length || n > 65536) return null;
                    curves[i] = n == 0 ? new Curve()
                        : n == 1 ? new Curve { _type = 0, _p = [BinaryPrimitives.ReadUInt16BigEndian(s[12..]) / 256f] }
                        : Table(t, (int)at + 12, (int)n, 2);
                } else if (s[..4].SequenceEqual("para"u8)) {
                    int type = BinaryPrimitives.ReadUInt16BigEndian(s[8..]);
                    if (type > 4) return null;
                    int n = type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, _ => 7 };
                    length = 12 + 4 * n;
                    if (length > s.Length) return null;
                    var p = new float[n];
                    for (int j = 0; j < n; j++) p[j] = BinaryPrimitives.ReadInt32BigEndian(s[(12 + 4 * j)..]) / 65536f;
                    curves[i] = new Curve { _type = type, _p = p };
                } else {
                    return null;
                }
                at += (uint)((length + 3) & ~3);
            }
            return curves;
        }

        public float Eval(float x) {
            x = Math.Clamp(x, 0, 1);
            if (_table != null) {
                float pos = x * (_table.Length - 1);
                int i = Math.Min((int)pos, _table.Length - 2);
                return _table[i] + (_table[i + 1] - _table[i]) * (pos - i);
            }
            var p = _p;
            float y = _type switch {
                0 => MathF.Pow(x, p[0]),
                1 => x >= -p[2] / p[1] ? Pow(p[1] * x + p[2], p[0]) : 0,
                2 => x >= -p[2] / p[1] ? Pow(p[1] * x + p[2], p[0]) + p[3] : p[3],
                3 => x >= p[4] ? Pow(p[1] * x + p[2], p[0]) : p[3] * x,
                4 => x >= p[4] ? Pow(p[1] * x + p[2], p[0]) + p[5] : p[3] * x + p[6],
                _ => x,
            };
            return float.IsNaN(y) ? 0 : Math.Clamp(y, 0, 1);
        }

        static float Pow(float b, float e) => b <= 0 ? 0 : MathF.Pow(b, e);
    }

    // the table of a profile: quadrilinear between its nodes, the first input varying slowest
    sealed class Clut(byte[] t, int at, int[] grid, int size) {
        public void Eval(ReadOnlySpan<float> x, Span<float> o) {
            Span<int> i0 = stackalloc int[4], stride = stackalloc int[4];
            Span<float> f = stackalloc float[4];
            int s = 3;
            for (int d = 3; d >= 0; d--) {
                stride[d] = s;
                s *= grid[d];
            }
            for (int d = 0; d < 4; d++) {
                float pos = Math.Clamp(x[d], 0, 1) * (grid[d] - 1);
                i0[d] = Math.Min((int)pos, grid[d] - 2);
                f[d] = pos - i0[d];
            }
            o.Clear();
            for (int corner = 0; corner < 16; corner++) {
                float w = 1;
                int index = 0;
                for (int d = 0; d < 4; d++) {
                    bool up = (corner >> d & 1) != 0;
                    w *= up ? f[d] : 1 - f[d];
                    index += (i0[d] + (up ? 1 : 0)) * stride[d];
                }
                if (w == 0) continue;
                for (int ch = 0; ch < 3; ch++)
                    o[ch] += w * (size == 2 ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 2 * (index + ch))) / 65535f : t[at + index + ch] / 255f);
            }
        }
    }
}
