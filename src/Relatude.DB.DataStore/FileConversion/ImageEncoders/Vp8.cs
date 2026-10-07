using System.Runtime.CompilerServices;

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
            new Span<byte>(above, size).CopyTo(new Span<byte>(top, size));
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
        for (int y = 0; y < size; y++)
            new Span<byte>(buf + Origin + y * Bps, size).CopyTo(new Span<byte>(plane + (long)(mby * size + y) * stride + mbx * size, size));
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
                byte v = n == 0 ? (byte)128 : (byte)((sum + n / 2) / n);
                for (int y = 0; y < size; y++) new Span<byte>(d + y * Bps, size).Fill(v);
                break;
            }
            case TM:
                for (int y = 0; y < size; y++) {
                    int left = d[y * Bps - 1] - top[-1];
                    for (int x = 0; x < size; x++) d[y * Bps + x] = Clip(top[x] + left);
                }
                break;
            case VE:
                for (int y = 0; y < size; y++) new Span<byte>(top, size).CopyTo(new Span<byte>(d + y * Bps, size));
                break;
            default:
                for (int y = 0; y < size; y++) new Span<byte>(d + y * Bps, size).Fill(d[y * Bps - 1]);
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
                byte v = (byte)((A + B + C + D + I + J + K + L + 4) >> 3);
                for (int y = 0; y < 4; y++) new Span<byte>(d + y * Bps, 4).Fill(v);
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
                new Span<byte>(d, 4).Fill(Avg3(X, I, J));
                new Span<byte>(d + Bps, 4).Fill(Avg3(I, J, K));
                new Span<byte>(d + 2 * Bps, 4).Fill(Avg3(J, K, L));
                new Span<byte>(d + 3 * Bps, 4).Fill(Avg3(K, L, L));
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
        bool ac = false;
        for (int i = 1; i < 16; i++) ac |= c[i] != 0;
        if (!ac) {
            if (c[0] == 0) return;
            int dc = (c[0] + 4) >> 3;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) dst[y * Bps + x] = Clip(dst[y * Bps + x] + dc);
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

    static int Sclip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;
    static int Sclip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Filter2(byte* p, int s) {
        int p1 = p[-2 * s], p0 = p[-s], q0 = p[0], q1 = p[s];
        int a = 3 * (q0 - p0) + Sclip1(p1 - q1);
        int a1 = Sclip2((a + 4) >> 3), a2 = Sclip2((a + 3) >> 3);
        p[-s] = Clip(p0 + a2);
        p[0] = Clip(q0 - a1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Filter4(byte* p, int s) {
        int p1 = p[-2 * s], p0 = p[-s], q0 = p[0], q1 = p[s];
        int a = 3 * (q0 - p0);
        int a1 = Sclip2((a + 4) >> 3), a2 = Sclip2((a + 3) >> 3), a3 = (a1 + 1) >> 1;
        p[-2 * s] = Clip(p1 + a3);
        p[-s] = Clip(p0 + a2);
        p[0] = Clip(q0 - a1);
        p[s] = Clip(q1 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Filter6(byte* p, int s) {
        int p2 = p[-3 * s], p1 = p[-2 * s], p0 = p[-s], q0 = p[0], q1 = p[s], q2 = p[2 * s];
        int a = Sclip1(3 * (q0 - p0) + Sclip1(p1 - q1));
        int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
        p[-3 * s] = Clip(p2 + a3);
        p[-2 * s] = Clip(p1 + a2);
        p[-s] = Clip(p0 + a1);
        p[0] = Clip(q0 - a1);
        p[s] = Clip(q1 - a2);
        p[2 * s] = Clip(q2 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Needs(byte* p, int s, int t) => 4 * Math.Abs(p[-s] - p[0]) + Math.Abs(p[-2 * s] - p[s]) <= t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Needs2(byte* p, int s, int t, int it) {
        int p3 = p[-4 * s], p2 = p[-3 * s], p1 = p[-2 * s], p0 = p[-s], q0 = p[0], q1 = p[s], q2 = p[2 * s], q3 = p[3 * s];
        return 4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) <= t && Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it
            && Math.Abs(p1 - p0) <= it && Math.Abs(q3 - q2) <= it && Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Hev(byte* p, int s, int t) => Math.Abs(p[-2 * s] - p[-s]) > t || Math.Abs(p[s] - p[0]) > t;

    // across `size` positions of one edge: hstride steps across the edge, vstride along it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Loop(byte* p, int hstride, int vstride, int size, int thresh, int inner, int hev, bool macroblockEdge) {
        int t = 2 * thresh + 1;
        for (int i = 0; i < size; i++, p += vstride) {
            if (!Needs2(p, hstride, t, inner)) continue;
            if (Hev(p, hstride, hev)) Filter2(p, hstride);
            else if (macroblockEdge) Filter6(p, hstride);
            else Filter4(p, hstride);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Simple(byte* p, int hstride, int vstride, int thresh) {
        int t = 2 * thresh + 1;
        for (int i = 0; i < 16; i++, p += vstride)
            if (Needs(p, hstride, t)) Filter2(p, hstride);
    }

    /// <summary>Filters one macroblock's left and top edges and, when inner, its inner block edges.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void FilterMacroblock(byte* y, byte* u, byte* v, int ys, int uvs, int mbx, int mby, FilterStrength f, bool inner, bool simple) {
        if (f.Limit == 0) return;
        y += (long)mby * 16 * ys + mbx * 16;
        int edge = f.Limit + 4;
        if (simple) {
            if (mbx > 0) Simple(y, 1, ys, edge);
            if (inner) for (int i = 4; i < 16; i += 4) Simple(y + i, 1, ys, f.Limit);
            if (mby > 0) Simple(y, ys, 1, edge);
            if (inner) for (int i = 4; i < 16; i += 4) Simple(y + i * ys, ys, 1, f.Limit);
            return;
        }
        u += (long)mby * 8 * uvs + mbx * 8;
        v += (long)mby * 8 * uvs + mbx * 8;
        int il = f.InnerLevel, hev = f.HevThreshold;
        if (mbx > 0) {
            Loop(y, 1, ys, 16, edge, il, hev, true);
            Loop(u, 1, uvs, 8, edge, il, hev, true);
            Loop(v, 1, uvs, 8, edge, il, hev, true);
        }
        if (inner) {
            for (int i = 4; i < 16; i += 4) Loop(y + i, 1, ys, 16, f.Limit, il, hev, false);
            Loop(u + 4, 1, uvs, 8, f.Limit, il, hev, false);
            Loop(v + 4, 1, uvs, 8, f.Limit, il, hev, false);
        }
        if (mby > 0) {
            Loop(y, ys, 1, 16, edge, il, hev, true);
            Loop(u, uvs, 1, 8, edge, il, hev, true);
            Loop(v, uvs, 1, 8, edge, il, hev, true);
        }
        if (inner) {
            for (int i = 4; i < 16; i += 4) Loop(y + i * ys, ys, 1, 16, f.Limit, il, hev, false);
            Loop(u + 4 * uvs, uvs, 1, 8, f.Limit, il, hev, false);
            Loop(v + 4 * uvs, uvs, 1, 8, f.Limit, il, hev, false);
        }
    }
}
