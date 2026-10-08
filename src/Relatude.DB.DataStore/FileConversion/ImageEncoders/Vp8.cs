using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// What the VP8 decoder and encoder share (RFC 6386, ported from libwebp): intra prediction, the 4x4
/// transforms, quantizer steps and the loop filter. Prediction works in a buffer of stride <see cref="Bps"/>
/// holding the macroblock with its top row, left column and the four pixels above-right.
/// </summary>
internal static unsafe class Vp8 {
    public const int Bps = 32;
    public const int Origin = Bps + 8; // row 0, column 0 of the macroblock in a work buffer
    public const int LumaBuffer = 17 * Bps, ChromaBuffer = 9 * Bps;
    // intra modes in libwebp's numbering; 16x16 and chroma use the first four
    public const int DC = 0, TM = 1, VE = 2, HE = 3, RD = 4, VR = 5, LD = 6, VL = 7, HD = 8, HU = 9;

    public readonly record struct Quant(int Y1Dc, int Y1Ac, int Y2Dc, int Y2Ac, int UvDc, int UvAc);

    public static Quant QuantFor(int q, int y1Dc = 0, int y2Dc = 0, int y2Ac = 0, int uvDc = 0, int uvAc = 0) {
        static int At(int i, int max) => Math.Clamp(i, 0, max);
        return new(Vp8Tables.DcTable[At(q + y1Dc, 127)], Vp8Tables.AcTable[At(q, 127)],
            Vp8Tables.DcTable[At(q + y2Dc, 127)] * 2, Math.Max(8, Vp8Tables.AcTable[At(q + y2Ac, 127)] * 101581 >> 16),
            Vp8Tables.DcTable[At(q + uvDc, 117)], Vp8Tables.AcTable[At(q + uvAc, 127)]);
    }

    static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>
    /// Fills the edges of a work buffer from the reconstruction so far: 127 above the frame, 129 left of it,
    /// and for luma the four pixels above-right (the last one repeated at the right edge of the frame).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void LoadEdges(byte* buf, byte* plane, int stride, int size, int mbx, int mby, int mbw) {
        byte* top = buf + Origin - Bps;
        int x0 = mbx * size, y0 = mby * size;
        if (mby == 0) {
            new Span<byte>(top - 1, size + 5).Fill(127);
        } else {
            byte* above = plane + (long)(y0 - 1) * stride + x0;
            if (size == 16) Vector128.Store(Vector128.Load(above), top);
            else *(ulong*)top = *(ulong*)above;
            top[-1] = mbx == 0 ? (byte)129 : above[-1];
            if (size == 16) {
                if (mbx == mbw - 1) new Span<byte>(top + 16, 4).Fill(above[15]);
                else *(uint*)(top + 16) = *(uint*)(above + 16);
            }
        }
        for (int y = 0; y < size; y++) buf[Origin + y * Bps - 1] = mbx == 0 ? (byte)129 : plane[(long)(y0 + y) * stride + x0 - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Store(byte* buf, byte* plane, int stride, int size, int mbx, int mby) {
        byte* src = buf + Origin, dst = plane + (long)mby * size * stride + mbx * size;
        if (size == 16) for (int y = 0; y < 16; y++) Vector128.Store(Vector128.Load(src + y * Bps), dst + (long)y * stride);
        else for (int y = 0; y < 8; y++) *(ulong*)(dst + (long)y * stride) = *(ulong*)(src + y * Bps);
    }

    // a row of a 16x16 or 8x8 block
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Row(byte* d, int size, Vector128<byte> v) {
        if (size == 16) Vector128.Store(v, d);
        else *(ulong*)d = v.AsUInt64().ToScalar();
    }

    /// <summary>A 16x16 luma or 8x8 chroma prediction; DC ignores the frame edges it lies on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void PredictBlock(byte* d, int size, int mode, bool hasTop, bool hasLeft) {
        byte* top = d - Bps;
        switch (mode) {
            case DC: {
                int sum = 0, n = 0;
                if (hasTop) { for (int i = 0; i < size; i++) sum += top[i]; n += size; }
                if (hasLeft) { for (int i = 0; i < size; i++) sum += d[i * Bps - 1]; n += size; }
                var v = Vector128.Create(n == 0 ? (byte)128 : (byte)((sum + n / 2) / n));
                for (int y = 0; y < size; y++) Row(d + y * Bps, size, v);
                break;
            }
            case TM: {
                var t = Vector128.Load(top);
                Vector128<short> lo = Vector128.WidenLower(t).AsInt16(), hi = Vector128.WidenUpper(t).AsInt16();
                for (int y = 0; y < size; y++) {
                    var left = Vector128.Create((short)(d[y * Bps - 1] - top[-1]));
                    Row(d + y * Bps, size, Vector128.Narrow(Pixel(lo + left), Pixel(hi + left)).AsByte());
                }
                break;
            }
            case VE: {
                var t = Vector128.Load(top);
                for (int y = 0; y < size; y++) Row(d + y * Bps, size, t);
                break;
            }
            default:
                for (int y = 0; y < size; y++) Row(d + y * Bps, size, Vector128.Create(d[y * Bps - 1]));
                break;
        }
    }

    static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);
    static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

    /// <summary>A 4x4 prediction in place, from the pixels above (eight, with above-right) and to the left.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Predict4(byte* d, int mode) {
        byte* t = d - Bps;
        int X = t[-1], A = t[0], B = t[1], C = t[2], D = t[3], E = t[4], F = t[5], G = t[6], H = t[7];
        int I = d[-1], J = d[Bps - 1], K = d[2 * Bps - 1], L = d[3 * Bps - 1];
        void P(int x, int y, byte v) => d[x + y * Bps] = v;
        switch (mode) {
            case DC: {
                uint v = (uint)((A + B + C + D + I + J + K + L + 4) >> 3) * 0x01010101u;
                for (int y = 0; y < 4; y++) *(uint*)(d + y * Bps) = v;
                break;
            }
            case TM:
                for (int y = 0; y < 4; y++) {
                    int left = d[y * Bps - 1] - X;
                    for (int x = 0; x < 4; x++) d[y * Bps + x] = Clip(t[x] + left);
                }
                break;
            case VE: {
                byte a = Avg3(X, A, B), b = Avg3(A, B, C), c = Avg3(B, C, D), e = Avg3(C, D, E);
                for (int y = 0; y < 4; y++) { P(0, y, a); P(1, y, b); P(2, y, c); P(3, y, e); }
                break;
            }
            case HE:
                *(uint*)d = Avg3(X, I, J) * 0x01010101u;
                *(uint*)(d + Bps) = Avg3(I, J, K) * 0x01010101u;
                *(uint*)(d + 2 * Bps) = Avg3(J, K, L) * 0x01010101u;
                *(uint*)(d + 3 * Bps) = Avg3(K, L, L) * 0x01010101u;
                break;
            case RD:
                P(0, 3, Avg3(J, K, L));
                P(1, 3, Avg3(I, J, K)); P(0, 2, Avg3(I, J, K));
                P(2, 3, Avg3(X, I, J)); P(1, 2, Avg3(X, I, J)); P(0, 1, Avg3(X, I, J));
                P(3, 3, Avg3(A, X, I)); P(2, 2, Avg3(A, X, I)); P(1, 1, Avg3(A, X, I)); P(0, 0, Avg3(A, X, I));
                P(3, 2, Avg3(B, A, X)); P(2, 1, Avg3(B, A, X)); P(1, 0, Avg3(B, A, X));
                P(3, 1, Avg3(C, B, A)); P(2, 0, Avg3(C, B, A));
                P(3, 0, Avg3(D, C, B));
                break;
            case VR:
                P(0, 0, Avg2(X, A)); P(1, 2, Avg2(X, A));
                P(1, 0, Avg2(A, B)); P(2, 2, Avg2(A, B));
                P(2, 0, Avg2(B, C)); P(3, 2, Avg2(B, C));
                P(3, 0, Avg2(C, D));
                P(0, 3, Avg3(K, J, I));
                P(0, 2, Avg3(J, I, X));
                P(0, 1, Avg3(I, X, A)); P(1, 3, Avg3(I, X, A));
                P(1, 1, Avg3(X, A, B)); P(2, 3, Avg3(X, A, B));
                P(2, 1, Avg3(A, B, C)); P(3, 3, Avg3(A, B, C));
                P(3, 1, Avg3(B, C, D));
                break;
            case LD:
                P(0, 0, Avg3(A, B, C));
                P(1, 0, Avg3(B, C, D)); P(0, 1, Avg3(B, C, D));
                P(2, 0, Avg3(C, D, E)); P(1, 1, Avg3(C, D, E)); P(0, 2, Avg3(C, D, E));
                P(3, 0, Avg3(D, E, F)); P(2, 1, Avg3(D, E, F)); P(1, 2, Avg3(D, E, F)); P(0, 3, Avg3(D, E, F));
                P(3, 1, Avg3(E, F, G)); P(2, 2, Avg3(E, F, G)); P(1, 3, Avg3(E, F, G));
                P(3, 2, Avg3(F, G, H)); P(2, 3, Avg3(F, G, H));
                P(3, 3, Avg3(G, H, H));
                break;
            case VL:
                P(0, 0, Avg2(A, B));
                P(1, 0, Avg2(B, C)); P(0, 2, Avg2(B, C));
                P(2, 0, Avg2(C, D)); P(1, 2, Avg2(C, D));
                P(3, 0, Avg2(D, E)); P(2, 2, Avg2(D, E));
                P(0, 1, Avg3(A, B, C));
                P(1, 1, Avg3(B, C, D)); P(0, 3, Avg3(B, C, D));
                P(2, 1, Avg3(C, D, E)); P(1, 3, Avg3(C, D, E));
                P(3, 1, Avg3(D, E, F)); P(2, 3, Avg3(D, E, F));
                P(3, 2, Avg3(E, F, G));
                P(3, 3, Avg3(F, G, H));
                break;
            case HD:
                P(0, 0, Avg2(I, X)); P(2, 1, Avg2(I, X));
                P(0, 1, Avg2(J, I)); P(2, 2, Avg2(J, I));
                P(0, 2, Avg2(K, J)); P(2, 3, Avg2(K, J));
                P(0, 3, Avg2(L, K));
                P(3, 0, Avg3(A, B, C));
                P(2, 0, Avg3(X, A, B));
                P(1, 0, Avg3(I, X, A)); P(3, 1, Avg3(I, X, A));
                P(1, 1, Avg3(J, I, X)); P(3, 2, Avg3(J, I, X));
                P(1, 2, Avg3(K, J, I)); P(3, 3, Avg3(K, J, I));
                P(1, 3, Avg3(L, K, J));
                break;
            default: // HU
                P(0, 0, Avg2(I, J));
                P(2, 0, Avg2(J, K)); P(0, 1, Avg2(J, K));
                P(2, 1, Avg2(K, L)); P(0, 2, Avg2(K, L));
                P(1, 0, Avg3(I, J, K));
                P(3, 0, Avg3(J, K, L)); P(1, 1, Avg3(J, K, L));
                P(3, 1, Avg3(K, L, L)); P(1, 2, Avg3(K, L, L));
                P(3, 2, (byte)L); P(2, 2, (byte)L); P(0, 3, (byte)L); P(1, 3, (byte)L); P(2, 3, (byte)L); P(3, 3, (byte)L);
                break;
        }
    }

    static int Mul1(int a) => ((a * 20091) >> 16) + a;
    static int Mul2(int a) => (a * 35468) >> 16;

    /// <summary>Adds the inverse transform of 16 dequantized coefficients to a 4x4 block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AddResidual(short* c, byte* dst) {
        var any = Vector128.Load(c) & Vector128.Create(0, -1, -1, -1, -1, -1, -1, -1) | Vector128.Load(c + 8);
        if (any == Vector128<short>.Zero) {
            if (c[0] == 0) return;
            var px = Vector128.Create(*(uint*)dst, *(uint*)(dst + Bps), *(uint*)(dst + 2 * Bps), *(uint*)(dst + 3 * Bps)).AsByte();
            var dc = Vector128.Create((short)((c[0] + 4) >> 3));
            var sum = Vector128.Narrow(Pixel(Vector128.WidenLower(px).AsInt16() + dc), Pixel(Vector128.WidenUpper(px).AsInt16() + dc)).AsUInt32();
            *(uint*)dst = sum[0]; *(uint*)(dst + Bps) = sum[1]; *(uint*)(dst + 2 * Bps) = sum[2]; *(uint*)(dst + 3 * Bps) = sum[3];
            return;
        }
        int* t = stackalloc int[16];
        for (int i = 0; i < 4; i++) {
            int a = c[i] + c[8 + i], b = c[i] - c[8 + i];
            int cc = Mul2(c[4 + i]) - Mul1(c[12 + i]), d = Mul1(c[4 + i]) + Mul2(c[12 + i]);
            t[i * 4] = a + d; t[i * 4 + 1] = b + cc; t[i * 4 + 2] = b - cc; t[i * 4 + 3] = a - d;
        }
        for (int i = 0; i < 4; i++, dst += Bps) {
            int dc = t[i] + 4;
            int a = dc + t[8 + i], b = dc - t[8 + i];
            int cc = Mul2(t[4 + i]) - Mul1(t[12 + i]), d = Mul1(t[4 + i]) + Mul2(t[12 + i]);
            dst[0] = Clip(dst[0] + ((a + d) >> 3));
            dst[1] = Clip(dst[1] + ((b + cc) >> 3));
            dst[2] = Clip(dst[2] + ((b - cc) >> 3));
            dst[3] = Clip(dst[3] + ((a - d) >> 3));
        }
    }

    /// <summary>The inverse Walsh-Hadamard transform: the 16 luma DCs, written to every 16th short.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void InverseWht(short* input, short* output) {
        int* t = stackalloc int[16];
        for (int i = 0; i < 4; i++) {
            int a0 = input[i] + input[12 + i], a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i], a3 = input[i] - input[12 + i];
            t[i] = a0 + a1; t[8 + i] = a0 - a1; t[4 + i] = a3 + a2; t[12 + i] = a3 - a2;
        }
        for (int i = 0; i < 4; i++, output += 64) {
            int dc = t[i * 4] + 3;
            int a0 = dc + t[i * 4 + 3], a1 = t[i * 4 + 1] + t[i * 4 + 2];
            int a2 = t[i * 4 + 1] - t[i * 4 + 2], a3 = dc - t[i * 4 + 3];
            output[0] = (short)((a0 + a1) >> 3);
            output[16] = (short)((a3 + a2) >> 3);
            output[32] = (short)((a0 - a1) >> 3);
            output[48] = (short)((a3 - a2) >> 3);
        }
    }

    /// <summary>The forward 4x4 transform of src minus the prediction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ForwardDct(byte* src, int srcStride, byte* pred, short* output) {
        int* t = stackalloc int[16];
        for (int i = 0; i < 4; i++, src += srcStride, pred += Bps) {
            int d0 = src[0] - pred[0], d1 = src[1] - pred[1], d2 = src[2] - pred[2], d3 = src[3] - pred[3];
            int a0 = d0 + d3, a1 = d1 + d2, a2 = d1 - d2, a3 = d0 - d3;
            t[i * 4] = (a0 + a1) * 8;
            t[i * 4 + 1] = (a2 * 2217 + a3 * 5352 + 1812) >> 9;
            t[i * 4 + 2] = (a0 - a1) * 8;
            t[i * 4 + 3] = (a3 * 2217 - a2 * 5352 + 937) >> 9;
        }
        for (int i = 0; i < 4; i++) {
            int a0 = t[i] + t[12 + i], a1 = t[4 + i] + t[8 + i], a2 = t[4 + i] - t[8 + i], a3 = t[i] - t[12 + i];
            output[i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)(((a2 * 2217 + a3 * 5352 + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)((a3 * 2217 - a2 * 5352 + 51000) >> 16);
        }
    }

    /// <summary>The forward Walsh-Hadamard transform of the 16 luma DCs found at every 16th short.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ForwardWht(short* input, short* output) {
        int* t = stackalloc int[16];
        for (int i = 0; i < 4; i++, input += 64) {
            int a0 = input[0] + input[32], a1 = input[16] + input[48], a2 = input[16] - input[48], a3 = input[0] - input[32];
            t[i * 4] = a0 + a1; t[i * 4 + 1] = a3 + a2; t[i * 4 + 2] = a3 - a2; t[i * 4 + 3] = a0 - a1;
        }
        for (int i = 0; i < 4; i++) {
            int a0 = t[i] + t[8 + i], a1 = t[4 + i] + t[12 + i], a2 = t[4 + i] - t[12 + i], a3 = t[i] - t[8 + i];
            output[i] = (short)((a0 + a1) >> 1);
            output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1);
            output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }

    // ── Loop filter ──────────────────────────────────────────────────────────

    public readonly record struct FilterStrength(int Limit, int InnerLevel, int HevThreshold);

    public static FilterStrength StrengthFor(int level, int sharpness) {
        level = Math.Clamp(level, 0, 63);
        if (level == 0) return default;
        int inner = level;
        if (sharpness > 0) {
            inner >>= sharpness > 4 ? 2 : 1;
            inner = Math.Min(inner, 9 - sharpness);
        }
        inner = Math.Max(inner, 1);
        return new(2 * level + inner, inner, level >= 40 ? 2 : level >= 15 ? 1 : 0);
    }

    const int SimpleEdge = 0, InnerEdge = 1, MacroblockEdge = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<short> Clamp(Vector128<short> v, short min, short max) => Vector128.Min(Vector128.Max(v, Vector128.Create(min)), Vector128.Create(max));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<short> Pixel(Vector128<short> v) => Clamp(v, 0, 255);

    // a line's positions as 16-bit lanes: sixteen, eight from each half, where Vector<short> has sixteen lanes, else eight
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<short> Widen(ulong a, ulong b) => Vector<short>.Count == 16
        ? Vector256.WidenLower(Vector128.Create(a, b).AsByte().ToVector256Unsafe()).AsInt16().AsVector()
        : Vector128.WidenLower(Vector128.CreateScalarUnsafe(a).AsByte()).AsInt16().AsVector();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Narrow(Vector<short> v, ulong* a, ulong* b) {
        if (Vector<short>.Count == 16) {
            var n = Vector256.Narrow(v.AsVector256().AsUInt16(), v.AsVector256().AsUInt16()).AsUInt64();
            *a = n.GetElement(0);
            *b = n.GetElement(1);
        } else {
            *a = Vector128.Narrow(v.AsVector128().AsUInt16(), v.AsVector128().AsUInt16()).AsUInt64().ToScalar();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<short> Clamp(Vector<short> v, short min, short max) => Vector.Min(Vector.Max(v, new Vector<short>(min)), new Vector<short>(max));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<short> Pixel(Vector<short> v) => Clamp(v, 0, 255);

    /// <summary>
    /// Eight lines across an edge (p3 p2 p1 p0 | q0 q1 q2 q3), each holding the same positions along it in two
    /// halves of eight, filtered as libwebp's scalar filters would one position at a time. False when nothing changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool FilterLines(ulong* a, ulong* b, int thresh, int inner, int hevThreshold, int kind) {
        if (Vector<short>.Count == 16) return Filter(a, b, thresh, inner, hevThreshold, kind);
        return Filter(a, a, thresh, inner, hevThreshold, kind) | Filter(b, b, thresh, inner, hevThreshold, kind);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Filter(ulong* a, ulong* b, int thresh, int inner, int hevThreshold, int kind) {
        Vector<short> p3 = Widen(a[0], b[0]), p2 = Widen(a[1], b[1]), p1 = Widen(a[2], b[2]), p0 = Widen(a[3], b[3]);
        Vector<short> q0 = Widen(a[4], b[4]), q1 = Widen(a[5], b[5]), q2 = Widen(a[6], b[6]), q3 = Widen(a[7], b[7]);
        var mask = Vector.LessThanOrEqual(Vector.Abs(p0 - q0) * 4 + Vector.Abs(p1 - q1), new Vector<short>((short)(2 * thresh + 1)));
        if (kind != SimpleEdge) {
            var it = new Vector<short>((short)inner);
            mask &= Vector.LessThanOrEqual(Vector.Abs(p3 - p2), it) & Vector.LessThanOrEqual(Vector.Abs(p2 - p1), it)
                & Vector.LessThanOrEqual(Vector.Abs(p1 - p0), it) & Vector.LessThanOrEqual(Vector.Abs(q3 - q2), it)
                & Vector.LessThanOrEqual(Vector.Abs(q2 - q1), it) & Vector.LessThanOrEqual(Vector.Abs(q1 - q0), it);
        }
        if (mask == Vector<short>.Zero) return false;
        var hev = kind == SimpleEdge ? Vector<short>.AllBitsSet
            : Vector.GreaterThan(Vector.Abs(p1 - p0), new Vector<short>((short)hevThreshold)) | Vector.GreaterThan(Vector.Abs(q1 - q0), new Vector<short>((short)hevThreshold));
        // high edge variance: only p0 and q0 move, with the outer taps
        var d = q0 - p0;
        var outer = Clamp(d * 3 + Clamp(p1 - q1, -128, 127), -128, 127);
        var m = mask & hev;
        var np0 = Vector.ConditionalSelect(m, Pixel(p0 + Clamp(Vector.ShiftRightArithmetic(outer + new Vector<short>(3), 3), -16, 15)), p0);
        var nq0 = Vector.ConditionalSelect(m, Pixel(q0 - Clamp(Vector.ShiftRightArithmetic(outer + new Vector<short>(4), 3), -16, 15)), q0);
        m = mask & ~hev;
        if (kind == InnerEdge) {
            var a1 = Clamp(Vector.ShiftRightArithmetic(d * 3 + new Vector<short>(4), 3), -16, 15);
            var a2 = Clamp(Vector.ShiftRightArithmetic(d * 3 + new Vector<short>(3), 3), -16, 15);
            var a3 = Vector.ShiftRightArithmetic(a1 + Vector<short>.One, 1);
            Narrow(Vector.ConditionalSelect(m, Pixel(p1 + a3), p1), a + 2, b + 2);
            np0 = Vector.ConditionalSelect(m, Pixel(p0 + a2), np0);
            nq0 = Vector.ConditionalSelect(m, Pixel(q0 - a1), nq0);
            Narrow(Vector.ConditionalSelect(m, Pixel(q1 - a3), q1), a + 5, b + 5);
        } else if (kind == MacroblockEdge) {
            var a1 = Vector.ShiftRightArithmetic(outer * 27 + new Vector<short>(63), 7);
            var a2 = Vector.ShiftRightArithmetic(outer * 18 + new Vector<short>(63), 7);
            var a3 = Vector.ShiftRightArithmetic(outer * 9 + new Vector<short>(63), 7);
            Narrow(Vector.ConditionalSelect(m, Pixel(p2 + a3), p2), a + 1, b + 1);
            Narrow(Vector.ConditionalSelect(m, Pixel(p1 + a2), p1), a + 2, b + 2);
            np0 = Vector.ConditionalSelect(m, Pixel(p0 + a1), np0);
            nq0 = Vector.ConditionalSelect(m, Pixel(q0 - a1), nq0);
            Narrow(Vector.ConditionalSelect(m, Pixel(q1 - a2), q1), a + 5, b + 5);
            Narrow(Vector.ConditionalSelect(m, Pixel(q2 - a3), q2), a + 6, b + 6);
        }
        Narrow(np0, a + 3, b + 3);
        Narrow(nq0, a + 4, b + 4);
        return true;
    }

    // an 8x8 block of bytes held as eight rows, turned into eight columns (and back again)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Transpose(ulong* r) {
        for (int i = 0; i < 8; i += 2) {
            ulong t = ((r[i] >> 8) ^ r[i + 1]) & 0x00FF00FF00FF00FFUL;
            r[i] ^= t << 8;
            r[i + 1] ^= t;
        }
        for (int i = 0; i < 8; i += i % 4 == 1 ? 3 : 1) {
            ulong t = ((r[i] >> 16) ^ r[i + 2]) & 0x0000FFFF0000FFFFUL;
            r[i] ^= t << 16;
            r[i + 2] ^= t;
        }
        for (int i = 0; i < 4; i++) {
            ulong t = ((r[i] >> 32) ^ r[i + 4]) & 0x00000000FFFFFFFFUL;
            r[i] ^= t << 32;
            r[i + 4] ^= t;
        }
    }

    // a horizontal edge in two halves of eight pixels, whose first q0 pixels are a and b: the lines are rows
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void RowEdge(byte* a, byte* b, int stride, int thresh, int inner, int hev, int kind) {
        ulong* la = stackalloc ulong[16];
        ulong* lb = la + 8;
        for (int k = 0; k < 8; k++) {
            la[k] = *(ulong*)(a + (k - 4) * stride);
            lb[k] = *(ulong*)(b + (k - 4) * stride);
        }
        if (!FilterLines(la, lb, thresh, inner, hev, kind)) return;
        for (int k = 1; k < 7; k++) {
            *(ulong*)(a + (k - 4) * stride) = la[k];
            *(ulong*)(b + (k - 4) * stride) = lb[k];
        }
    }

    // a vertical edge in two halves of eight rows, whose first q0 pixels are a and b: the lines are columns
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void ColumnEdge(byte* a, byte* b, int stride, int thresh, int inner, int hev, int kind) {
        ulong* la = stackalloc ulong[16];
        ulong* lb = la + 8;
        for (int k = 0; k < 8; k++) {
            la[k] = *(ulong*)(a - 4 + k * stride);
            lb[k] = *(ulong*)(b - 4 + k * stride);
        }
        Transpose(la);
        Transpose(lb);
        if (!FilterLines(la, lb, thresh, inner, hev, kind)) return;
        Transpose(la);
        Transpose(lb);
        for (int k = 0; k < 8; k++) {
            *(ulong*)(a - 4 + k * stride) = la[k];
            *(ulong*)(b - 4 + k * stride) = lb[k];
        }
    }

    /// <summary>Filters one macroblock's left and top edges and, when inner, its inner block edges.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void FilterMacroblock(byte* y, byte* u, byte* v, int ys, int uvs, int mbx, int mby, FilterStrength f, bool inner, bool simple) {
        if (f.Limit == 0) return;
        y += (long)mby * 16 * ys + mbx * 16;
        byte* y8 = y + 8 * ys; // the lower half of the luma rows
        int edge = f.Limit + 4, il = f.InnerLevel, hev = f.HevThreshold;
        if (simple) {
            if (mbx > 0) ColumnEdge(y, y8, ys, edge, 0, 0, SimpleEdge);
            if (inner) for (int i = 4; i < 16; i += 4) ColumnEdge(y + i, y8 + i, ys, f.Limit, 0, 0, SimpleEdge);
            if (mby > 0) RowEdge(y, y + 8, ys, edge, 0, 0, SimpleEdge);
            if (inner) for (int i = 4; i < 16; i += 4) RowEdge(y + i * ys, y + i * ys + 8, ys, f.Limit, 0, 0, SimpleEdge);
            return;
        }
        u += (long)mby * 8 * uvs + mbx * 8;
        v += (long)mby * 8 * uvs + mbx * 8;
        if (mbx > 0) {
            ColumnEdge(y, y8, ys, edge, il, hev, MacroblockEdge);
            ColumnEdge(u, v, uvs, edge, il, hev, MacroblockEdge);
        }
        if (inner) {
            for (int i = 4; i < 16; i += 4) ColumnEdge(y + i, y8 + i, ys, f.Limit, il, hev, InnerEdge);
            ColumnEdge(u + 4, v + 4, uvs, f.Limit, il, hev, InnerEdge);
        }
        if (mby > 0) {
            RowEdge(y, y + 8, ys, edge, il, hev, MacroblockEdge);
            RowEdge(u, v, uvs, edge, il, hev, MacroblockEdge);
        }
        if (inner) {
            for (int i = 4; i < 16; i += 4) RowEdge(y + i * ys, y + i * ys + 8, ys, f.Limit, il, hev, InnerEdge);
            RowEdge(u + 4 * uvs, v + 4 * uvs, uvs, f.Limit, il, hev, InnerEdge);
        }
    }
}
