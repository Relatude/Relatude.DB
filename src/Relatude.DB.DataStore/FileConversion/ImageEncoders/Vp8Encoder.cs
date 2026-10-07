using System.Numerics;
using System.Runtime.CompilerServices;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// A VP8 key frame (lossy WebP) from Y, U and V planes padded to whole macroblocks. Each macroblock is
/// predicted as one 16x16 block or as sixteen 4x4 blocks, whichever costs less in distortion plus bits, and
/// quantized by its segment; the token probabilities are then fitted to the frame before it is written.
/// </summary>
internal sealed unsafe class Vp8Encoder {
    const int MbLevels = 400; // 16 luma blocks, 4 + 4 chroma blocks, the luma DC block
    static readonly int[] Cost0 = new int[256], Cost1 = new int[256]; // in 1/256 bit

    static Vp8Encoder() {
        for (int p = 1; p < 256; p++) {
            Cost0[p] = (int)Math.Round(-Math.Log2(p / 256.0) * 256);
            Cost1[p] = (int)Math.Round(-Math.Log2(1 - p / 256.0) * 256);
        }
        Cost0[0] = Cost1[255] = 16 * 256;
    }

    readonly byte[] _y, _u, _v, _ry, _ru, _rv;
    readonly int _width, _height, _mbw, _mbh, _ys, _uvs;
    Vp8.Quant _quant; // of the macroblock being analysed
    long _lambda;
    // up to four segments of macroblocks, each with its own quantizer and filter strength
    readonly byte[] _segment;
    readonly int[] _segQ = new int[4], _segFilter = new int[4];
    readonly Vp8.Quant[] _segQuant = new Vp8.Quant[4];
    readonly long[] _segLambda = new long[4];
    readonly byte[] _segProbs = [255, 255, 255];
    int _segments, _dqUvDc, _dqUvAc;
    readonly short[] _levels;
    readonly byte[] _modes, _info; // info: i4 | skip << 1, ymode << 2, uvmode << 4
    readonly byte[] _topModes, _topNz;

    Vp8Encoder(byte[] y, byte[] u, byte[] v, int width, int height, int quality) {
        _y = y; _u = u; _v = v;
        _width = width; _height = height;
        _mbw = (width + 15) >> 4; _mbh = (height + 15) >> 4;
        _ys = _mbw * 16; _uvs = _mbw * 8;
        _ry = new byte[y.Length]; _ru = new byte[u.Length]; _rv = new byte[v.Length];
        _segment = new byte[_mbw * _mbh];
        // libwebp's quality to quantizer mapping, then adapted per segment
        double c = quality / 100.0, linear = c < 0.75 ? c * (2.0 / 3) : 2 * c - 1;
        Segment(Math.Cbrt(linear));
        _levels = new short[_mbw * _mbh * MbLevels];
        _modes = new byte[_mbw * _mbh * 16];
        _info = new byte[_mbw * _mbh];
        _topModes = new byte[_mbw * 4];
        _topNz = new byte[_mbw * 9];
    }

    public static byte[] Encode(byte[] y, byte[] u, byte[] v, int width, int height, int quality) {
        var e = new Vp8Encoder(y, u, v, width, height, Math.Clamp(quality, 0, 100));
        e.Analyse();
        return e.Write();
    }

    // ── Token emission, shared by cost estimates, statistics and the writer ──

    interface ISink { bool Bit(bool bit, int prob, int node); }

    struct CostSink : ISink {
        public int Cost;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Bit(bool bit, int prob, int node) {
            Cost += bit ? Cost1[prob] : Cost0[prob];
            return bit;
        }
    }

    struct StatSink(int[] stats) : ISink {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Bit(bool bit, int prob, int node) {
            if (node >= 0) stats[2 * node + (bit ? 1 : 0)]++;
            return bit;
        }
    }

    struct WriteSink(BoolWriter writer) : ISink {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Bit(bool bit, int prob, int node) {
            writer.Put(bit, prob);
            return bit;
        }
    }

    // one block's levels (scan order) from position first; true when it has any (as libwebp's PutCoeffs)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool Tokens<T>(ref T s, byte* probs, int type, int ctx, short* levels, int first) where T : struct, ISink {
        int last = 15;
        while (last >= first && levels[last] == 0) last--;
        int n = first, node = ((type * 8 + Vp8Tables.Bands[n]) * 3 + ctx) * 11;
        if (!s.Bit(last >= n, probs[node], node)) return false;
        while (n < 16) {
            int c = levels[n++], v = c < 0 ? -c : c, band = (type * 8 + Vp8Tables.Bands[n]) * 33;
            if (!s.Bit(v != 0, probs[node + 1], node + 1)) {
                node = band;
                continue;
            }
            if (!s.Bit(v > 1, probs[node + 2], node + 2)) {
                node = band + 11;
            } else {
                if (!s.Bit(v > 4, probs[node + 3], node + 3)) {
                    if (s.Bit(v != 2, probs[node + 4], node + 4)) s.Bit(v == 4, probs[node + 5], node + 5);
                } else if (!s.Bit(v > 10, probs[node + 6], node + 6)) {
                    if (!s.Bit(v > 6, probs[node + 7], node + 7)) {
                        s.Bit(v == 6, 159, -1);
                    } else {
                        s.Bit(v >= 9, 165, -1);
                        s.Bit((v & 1) == 0, 145, -1);
                    }
                } else {
                    int cat = v < 19 ? 0 : v < 35 ? 1 : v < 67 ? 2 : 3, extra = v - 3 - (8 << cat);
                    s.Bit(cat >= 2, probs[node + 8], node + 8);
                    s.Bit((cat & 1) != 0, probs[node + 9 + (cat >> 1)], node + 9 + (cat >> 1));
                    var bits = Vp8Tables.Cat[cat];
                    for (int b = 0; b < bits.Length; b++) s.Bit(((extra >> (bits.Length - 1 - b)) & 1) != 0, bits[b], -1);
                }
                node = band + 22;
            }
            s.Bit(c < 0, 128, -1);
            if (n == 16 || !s.Bit(n <= last, probs[node], node)) return true;
        }
        return true;
    }

    // all of a macroblock's tokens, updating the neighbours' non-zero flags as the decoder will
    static void MacroblockTokens<T>(ref T s, byte* probs, short* lv, bool i4, byte* nz, byte* left) where T : struct, ISink {
        int first = 0, type = 3;
        if (!i4) {
            nz[8] = left[8] = (byte)(Tokens(ref s, probs, 1, nz[8] + left[8], lv + 384, 0) ? 1 : 0);
            first = 1;
            type = 0;
        }
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                nz[x] = left[y] = (byte)(Tokens(ref s, probs, type, nz[x] + left[y], lv + (y * 4 + x) * 16, first) ? 1 : 0);
        for (int ch = 4; ch < 8; ch += 2)
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                    nz[ch + x] = left[ch + y] = (byte)(Tokens(ref s, probs, 2, nz[ch + x] + left[ch + y], lv + 256 + (ch - 4) * 32 + (y * 2 + x) * 16, 0) ? 1 : 0);
    }

    static void SkipFlags(bool i4, byte* nz, byte* left) {
        new Span<byte>(nz, 8).Clear();
        new Span<byte>(left, 8).Clear();
        if (!i4) nz[8] = left[8] = 0;
    }

    // ── Modes, written or costed through the same trees ──────────────────────

    static void Mode16<T>(ref T s, int mode) where T : struct, ISink {
        if (s.Bit(mode is Vp8.TM or Vp8.HE, 156, -1)) s.Bit(mode == Vp8.TM, 128, -1);
        else s.Bit(mode == Vp8.VE, 163, -1);
    }

    static void Mode4<T>(ref T s, int mode, int top, int left) where T : struct, ISink {
        int p = (top * 10 + left) * 9;
        var probs = Vp8Tables.BModeProbs;
        if (!s.Bit(mode != Vp8.DC, probs[p], -1) || !s.Bit(mode != Vp8.TM, probs[p + 1], -1) || !s.Bit(mode != Vp8.VE, probs[p + 2], -1)) return;
        if (!s.Bit(mode >= Vp8.LD, probs[p + 3], -1)) {
            if (s.Bit(mode != Vp8.HE, probs[p + 4], -1)) s.Bit(mode != Vp8.RD, probs[p + 5], -1);
        } else if (s.Bit(mode != Vp8.LD, probs[p + 6], -1) && s.Bit(mode != Vp8.VL, probs[p + 7], -1)) {
            s.Bit(mode != Vp8.HD, probs[p + 8], -1);
        }
    }

    static void UvMode<T>(ref T s, int mode) where T : struct, ISink {
        if (s.Bit(mode != Vp8.DC, 142, -1) && s.Bit(mode != Vp8.VE, 114, -1)) s.Bit(mode != Vp8.HE, 183, -1);
    }

    static int Mode16Cost(int mode) {
        var c = new CostSink();
        c.Bit(true, 145, -1);
        Mode16(ref c, mode);
        return c.Cost;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int Mode4Cost(int mode, int top, int left) {
        var c = new CostSink();
        Mode4(ref c, mode, top, left);
        return c.Cost;
    }

    static int UvModeCost(int mode) {
        var c = new CostSink();
        UvMode(ref c, mode);
        return c.Cost;
    }

    // ── Segments (libwebp's spatial noise shaping) ───────────────────────────

    // the coefficient histogram of a block's residual: values / 8, the last bin catching the rest
    static void Collect(int* bins, byte* src, int stride, byte* pred, int size) {
        short* c = stackalloc short[16];
        for (int y = 0; y < size; y += 4)
            for (int x = 0; x < size; x += 4) {
                Vp8.ForwardDct(src + y * stride + x, stride, pred + y * Vp8.Bps + x, c);
                for (int k = 0; k < 16; k++) bins[Math.Min(Math.Abs((int)c[k]) >> 3, 31)]++;
            }
    }

    // how far the residual spreads: high for a busy block, which hides coarser quantization
    static int Spread(int* bins) {
        int max = 0, last = 1;
        for (int k = 0; k < 32; k++)
            if (bins[k] > 0) {
                max = Math.Max(max, bins[k]);
                last = k;
            }
        return max > 1 ? 510 * last / max : 0;
    }

    // libwebp's measure of each macroblock in a row, from the DC prediction of the source; the row's chroma total
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void MeasureRow(int mby, byte[] values, long[] uvSums) {
        byte* yb = stackalloc byte[Vp8.LumaBuffer];
        byte* ub = stackalloc byte[Vp8.ChromaBuffer];
        byte* vb = stackalloc byte[Vp8.ChromaBuffer];
        int* bins = stackalloc int[32];
        fixed (byte* sy = _y, su = _u, sv = _v)
            for (int mbx = 0; mbx < _mbw; mbx++) {
                Vp8.LoadEdges(yb, sy, _ys, 16, mbx, mby, _mbw);
                Vp8.LoadEdges(ub, su, _uvs, 8, mbx, mby, _mbw);
                Vp8.LoadEdges(vb, sv, _uvs, 8, mbx, mby, _mbw);
                Vp8.PredictBlock(yb + Vp8.Origin, 16, 0, mby > 0, mbx > 0);
                Vp8.PredictBlock(ub + Vp8.Origin, 8, 0, mby > 0, mbx > 0);
                Vp8.PredictBlock(vb + Vp8.Origin, 8, 0, mby > 0, mbx > 0);
                new Span<int>(bins, 32).Clear();
                Collect(bins, sy + (long)mby * 16 * _ys + mbx * 16, _ys, yb + Vp8.Origin, 16);
                int luma = Spread(bins);
                new Span<int>(bins, 32).Clear();
                Collect(bins, su + (long)mby * 8 * _uvs + mbx * 8, _uvs, ub + Vp8.Origin, 8);
                Collect(bins, sv + (long)mby * 8 * _uvs + mbx * 8, _uvs, vb + Vp8.Origin, 8);
                int chroma = Spread(bins);
                values[mby * _mbw + mbx] = (byte)Math.Clamp(255 - ((3 * luma + chroma + 2) >> 2), 0, 255);
                uvSums[mby] += chroma;
            }
    }

    /// <summary>
    /// Groups the macroblocks by how busy they are (k-means on libwebp's measure) and gives each group a
    /// quantizer bent away from the base one: busy groups coarser, smooth ones finer.
    /// </summary>
    void Segment(double cBase) {
        int count = _mbw * _mbh;
        var values = new byte[count];
        var uvSums = new long[_mbh];
        if (InternalImage.ShouldParallelize(_width, _height)) Parallel.For(0, _mbh, mby => MeasureRow(mby, values, uvSums));
        else for (int mby = 0; mby < _mbh; mby++) MeasureRow(mby, values, uvSums);
        var histogram = new int[256];
        foreach (byte value in values) histogram[value]++;

        // k-means on the histogram, centers spread evenly to start with
        int lo = 0, hi = 255;
        while (lo < 255 && histogram[lo] == 0) lo++;
        while (hi > lo && histogram[hi] == 0) hi--;
        var centers = new int[4];
        var map = new int[256];
        for (int k = 0; k < 4; k++) centers[k] = lo + (2 * k + 1) * (hi - lo) / 8;
        int mid = lo;
        for (int iteration = 0; iteration < 6; iteration++) {
            var sum = new long[4];
            var weight = new long[4];
            for (int a = lo, n = 0; a <= hi; a++) {
                if (histogram[a] == 0) continue;
                while (n < 3 && Math.Abs(a - centers[n + 1]) < Math.Abs(a - centers[n])) n++;
                map[a] = n;
                sum[n] += (long)a * histogram[a];
                weight[n] += histogram[a];
            }
            long weighted = 0, total = 0;
            int displaced = 0;
            for (int n = 0; n < 4; n++) {
                if (weight[n] == 0) continue;
                int center = (int)((sum[n] + weight[n] / 2) / weight[n]);
                displaced += Math.Abs(centers[n] - center);
                centers[n] = center;
                weighted += center * weight[n];
                total += weight[n];
            }
            mid = (int)((weighted + total / 2) / total);
            if (displaced < 5) break;
        }
        int min = centers.Min(), max = Math.Max(centers.Max(), min + 1);
        var counts = new int[4];
        for (int i = 0; i < count; i++) counts[_segment[i] = (byte)map[values[i]]]++;

        const double amp = 0.9 * 50 / 100.0 / 128; // libwebp's default noise-shaping strength
        for (int s = 0; s < 4; s++) {
            int alpha = Math.Clamp(255 * (centers[s] - mid) / (max - min), -127, 127);
            _segQ[s] = Math.Clamp((int)(127 * (1 - Math.Pow(cBase, 1 - amp * alpha))), 0, 127);
            int level = Vp8Tables.AcTable[_segQ[s]] * 3 / 8;
            _segFilter[s] = level < 2 ? 0 : Math.Min(level, 63);
        }
        // chroma is quantized a little coarser when it is busy, and its DC a little finer
        _dqUvAc = Math.Clamp((int)(uvSums.Sum() / count - 64) * 10 / 70 / 2, -4, 6);
        _dqUvDc = -2;
        for (int s = 0; s < 4; s++) {
            _segQuant[s] = Vp8.QuantFor(_segQ[s], uvDc: _dqUvDc, uvAc: _dqUvAc);
            _segLambda[s] = Math.Max(1, _segQuant[s].Y1Ac * _segQuant[s].Y1Ac / 25);
        }
        bool same = _segQ.All(q => q == _segQ[0]) && _segFilter.All(f => f == _segFilter[0]);
        _segments = same ? 1 : 4;
        if (same) Array.Clear(_segment);
        static byte Probability(int zeros, int ones) => zeros + ones == 0 ? (byte)255 : (byte)((255 * zeros + (zeros + ones) / 2) / (zeros + ones));
        _segProbs[0] = Probability(counts[0] + counts[1], counts[2] + counts[3]);
        _segProbs[1] = Probability(counts[0], counts[1]);
        _segProbs[2] = Probability(counts[2], counts[3]);
    }

    // ── Analysis: modes, levels and the reconstruction ───────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int Sse(byte* a, int aStride, byte* b, int size) {
        int sum = 0;
        for (int y = 0; y < size; y++, a += aStride, b += Vp8.Bps)
            for (int x = 0; x < size; x++) {
                int d = a[x] - b[x];
                sum += d * d;
            }
        return sum;
    }

    // levels in scan order from position first; the coefficients become their dequantized values
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool Quantize(short* c, short* levels, int dcq, int acq, int dcBias, int acBias, int first) {
        bool any = false;
        for (int k = first; k < 16; k++) {
            int j = Vp8Tables.Zigzag[k], q = k == 0 ? dcq : acq, v = c[j], a = v < 0 ? -v : v;
            int level = Math.Min(2047, (a * 256 + q * (k == 0 ? dcBias : acBias)) / (q * 256));
            if (v < 0) level = -level;
            levels[k] = (short)level;
            c[j] = (short)(level * q);
            any |= level != 0;
        }
        return any;
    }

    long Score(int distortion, int rate) => (long)distortion * 256 + _lambda * rate;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Analyse() {
        var probs = Vp8Tables.CoeffProbs;
        byte* yb = stackalloc byte[Vp8.LumaBuffer];
        byte* y4 = stackalloc byte[Vp8.LumaBuffer];
        byte* ub = stackalloc byte[Vp8.ChromaBuffer];
        byte* vb = stackalloc byte[Vp8.ChromaBuffer];
        short* c = stackalloc short[16 * 16];
        short* lv16 = stackalloc short[MbLevels];
        short* lv4 = stackalloc short[256];
        byte* modes4 = stackalloc byte[16];
        byte* leftModes = stackalloc byte[4];
        byte* leftNz = stackalloc byte[9];
        byte* nzTrial = stackalloc byte[9];
        byte* leftTrial = stackalloc byte[9];
        fixed (byte* sy = _y, su = _u, sv = _v, ry = _ry, ru = _ru, rv = _rv, pr = probs, tm = _topModes, tn = _topNz)
        fixed (short* levels = _levels) {
            for (int mby = 0; mby < _mbh; mby++) {
                new Span<byte>(leftModes, 4).Clear();
                new Span<byte>(leftNz, 9).Clear();
                for (int mbx = 0; mbx < _mbw; mbx++) {
                    int mb = mby * _mbw + mbx;
                    _quant = _segQuant[_segment[mb]];
                    _lambda = _segLambda[_segment[mb]];
                    byte* src = sy + (long)mby * 16 * _ys + mbx * 16;
                    byte* nz = tn + mbx * 9;
                    byte* top = tm + mbx * 4;
                    Vp8.LoadEdges(yb, ry, _ys, 16, mbx, mby, _mbw);
                    new Span<byte>(yb, Vp8.LumaBuffer).CopyTo(new Span<byte>(y4, Vp8.LumaBuffer));
                    byte* yd = yb + Vp8.Origin;

                    // one 16x16 prediction
                    int mode16 = 0;
                    long best = long.MaxValue;
                    for (int m = 0; m < 4; m++) {
                        Vp8.PredictBlock(yd, 16, m, mby > 0, mbx > 0);
                        long score = Score(Sse(src, _ys, yd, 16), Mode16Cost(m));
                        if (score < best) { best = score; mode16 = m; }
                    }
                    Vp8.PredictBlock(yd, 16, mode16, mby > 0, mbx > 0);
                    new Span<byte>(nz, 9).CopyTo(new Span<byte>(nzTrial, 9));
                    new Span<byte>(leftNz, 9).CopyTo(new Span<byte>(leftTrial, 9));
                    int rate16 = Luma16(src, yd, c, lv16, nzTrial, leftTrial, pr) + Mode16Cost(mode16);
                    long score16 = Score(Sse(src, _ys, yd, 16), rate16);

                    // sixteen 4x4 predictions, given up once they cost more than the 16x16 one
                    long score4 = Luma4(src, y4 + Vp8.Origin, c, lv4, modes4, top, leftModes, nz, leftNz, pr, score16);
                    bool i4 = score4 < score16;
                    short* lv = levels + (long)mb * MbLevels;
                    if (i4) {
                        new Span<short>(lv4, 256).CopyTo(new Span<short>(lv, 256));
                        new Span<byte>(modes4, 16).CopyTo(new Span<byte>(_modes, mb * 16, 16));
                        for (int i = 0; i < 4; i++) { top[i] = modes4[12 + i]; leftModes[i] = modes4[i * 4 + 3]; }
                        Vp8.Store(y4, ry, _ys, 16, mbx, mby);
                    } else {
                        new Span<short>(lv16, 256).CopyTo(new Span<short>(lv, 256));
                        new Span<short>(lv16 + 384, 16).CopyTo(new Span<short>(lv + 384, 16));
                        new Span<byte>(top, 4).Fill((byte)mode16);
                        new Span<byte>(leftModes, 4).Fill((byte)mode16);
                        Vp8.Store(yb, ry, _ys, 16, mbx, mby);
                    }

                    int uvMode = Chroma(su, sv, ru, rv, ub, vb, c, lv, mbx, mby);
                    bool skip = new Span<short>(lv, MbLevels).IndexOfAnyExcept((short)0) < 0;
                    _info[mb] = (byte)((i4 ? 1 : 0) | (skip ? 2 : 0) | mode16 << 2 | uvMode << 4);
                    if (skip) {
                        SkipFlags(i4, nz, leftNz);
                    } else {
                        var flags = new CostSink();
                        MacroblockTokens(ref flags, pr, lv, i4, nz, leftNz);
                    }
                }
            }
        }
    }

    // quantizes and reconstructs a 16x16 prediction in place; the rate of its tokens
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    int Luma16(byte* src, byte* yd, short* c, short* lv, byte* nz, byte* left, byte* probs) {
        short* dc = stackalloc short[16];
        for (int n = 0; n < 16; n++) Vp8.ForwardDct(src + (n >> 2) * 4 * _ys + (n & 3) * 4, _ys, yd + (n >> 2) * 4 * Vp8.Bps + (n & 3) * 4, c + n * 16);
        Vp8.ForwardWht(c, dc);
        Quantize(dc, lv + 384, _quant.Y2Dc, _quant.Y2Ac, 96, 108, 0);
        Vp8.InverseWht(dc, c);
        for (int n = 0; n < 16; n++) {
            lv[n * 16] = 0;
            Quantize(c + n * 16, lv + n * 16, 0, _quant.Y1Ac, 0, 110, 1);
            Vp8.AddResidual(c + n * 16, yd + (n >> 2) * 4 * Vp8.Bps + (n & 3) * 4);
        }
        var cost = new CostSink();
        nz[8] = left[8] = (byte)(Tokens(ref cost, probs, 1, nz[8] + left[8], lv + 384, 0) ? 1 : 0);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                nz[x] = left[y] = (byte)(Tokens(ref cost, probs, 0, nz[x] + left[y], lv + (y * 4 + x) * 16, 1) ? 1 : 0);
        return cost.Cost;
    }

    // the sixteen 4x4 blocks in turn, each with its best mode; the score, or long.MaxValue once past budget
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    long Luma4(byte* src, byte* yd, short* c, short* lv, byte* modes, byte* topModes, byte* leftModes, byte* topNz, byte* leftNz, byte* probs, long budget) {
        uint topRight = *(uint*)(yd - Vp8.Bps + 16);
        *(uint*)(yd + 3 * Vp8.Bps + 16) = *(uint*)(yd + 7 * Vp8.Bps + 16) = *(uint*)(yd + 11 * Vp8.Bps + 16) = topRight;
        byte* tnz = stackalloc byte[4];
        byte* lnz = stackalloc byte[4];
        new Span<byte>(topNz, 4).CopyTo(new Span<byte>(tnz, 4));
        new Span<byte>(leftNz, 4).CopyTo(new Span<byte>(lnz, 4));
        int distortion = 0, rate = Cost0[145];
        for (int n = 0; n < 16; n++) {
            int bx = n & 3, by = n >> 2;
            byte* dst = yd + by * 4 * Vp8.Bps + bx * 4;
            byte* s = src + by * 4 * _ys + bx * 4;
            int above = by == 0 ? topModes[bx] : modes[n - 4], left = bx == 0 ? leftModes[by] : modes[n - 1];
            int mode = 0;
            long best = long.MaxValue;
            for (int m = 0; m < 10; m++) {
                Vp8.Predict4(dst, m);
                long score = Score(Sse(s, _ys, dst, 4), Mode4Cost(m, above, left));
                if (score < best) { best = score; mode = m; }
            }
            modes[n] = (byte)mode;
            Vp8.Predict4(dst, mode);
            Vp8.ForwardDct(s, _ys, dst, c);
            Quantize(c, lv + n * 16, _quant.Y1Dc, _quant.Y1Ac, 96, 110, 0);
            Vp8.AddResidual(c, dst);
            var cost = new CostSink();
            tnz[bx] = lnz[by] = (byte)(Tokens(ref cost, probs, 3, tnz[bx] + lnz[by], lv + n * 16, 0) ? 1 : 0);
            distortion += Sse(s, _ys, dst, 4);
            rate += cost.Cost + Mode4Cost(mode, above, left);
            if (Score(distortion, rate) >= budget) return long.MaxValue;
        }
        return Score(distortion, rate);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    int Chroma(byte* su, byte* sv, byte* ru, byte* rv, byte* ub, byte* vb, short* c, short* lv, int mbx, int mby) {
        Vp8.LoadEdges(ub, ru, _uvs, 8, mbx, mby, _mbw);
        Vp8.LoadEdges(vb, rv, _uvs, 8, mbx, mby, _mbw);
        byte* u = su + (long)mby * 8 * _uvs + mbx * 8, v = sv + (long)mby * 8 * _uvs + mbx * 8;
        byte* ud = ub + Vp8.Origin, vd = vb + Vp8.Origin;
        int mode = 0;
        long best = long.MaxValue;
        for (int m = 0; m < 4; m++) {
            Vp8.PredictBlock(ud, 8, m, mby > 0, mbx > 0);
            Vp8.PredictBlock(vd, 8, m, mby > 0, mbx > 0);
            long score = Score(Sse(u, _uvs, ud, 8) + Sse(v, _uvs, vd, 8), UvModeCost(m));
            if (score < best) { best = score; mode = m; }
        }
        for (int ch = 0; ch < 2; ch++) {
            byte* s = ch == 0 ? u : v, d = ch == 0 ? ud : vd;
            Vp8.PredictBlock(d, 8, mode, mby > 0, mbx > 0);
            for (int n = 0; n < 4; n++) {
                int offset = (n >> 1) * 4, x = (n & 1) * 4;
                Vp8.ForwardDct(s + offset * _uvs + x, _uvs, d + offset * Vp8.Bps + x, c);
                Quantize(c, lv + 256 + ch * 64 + n * 16, _quant.UvDc, _quant.UvAc, 110, 115, 0);
                Vp8.AddResidual(c, d + offset * Vp8.Bps + x);
            }
        }
        Vp8.Store(ub, ru, _uvs, 8, mbx, mby);
        Vp8.Store(vb, rv, _uvs, 8, mbx, mby);
        return mode;
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    sealed class BoolWriter {
        byte[] _buffer = new byte[1 << 16];
        int _length, _range = 254, _value, _run, _bits = -8;

        public bool Put(bool bit, int prob) {
            int split = (_range * prob) >> 8;
            if (bit) {
                _value += split + 1;
                _range -= split + 1;
            } else {
                _range = split;
            }
            if (_range < 127) {
                int shift = 7 - BitOperations.Log2((uint)_range + 1);
                _range = ((_range + 1) << shift) - 1;
                _value <<= shift;
                _bits += shift;
                if (_bits > 0) Flush();
            }
            return bit;
        }

        void Flush() {
            int s = 8 + _bits, bits = _value >> s;
            _value -= bits << s;
            _bits -= 8;
            if ((bits & 0xff) == 0xff) {
                _run++; // held back until it is known whether a carry reaches it
                return;
            }
            if ((bits & 0x100) != 0 && _length > 0) _buffer[_length - 1]++;
            for (; _run > 0; _run--) Push((bits & 0x100) != 0 ? (byte)0 : (byte)0xff);
            Push((byte)bits);
        }

        void Push(byte b) {
            if (_length == _buffer.Length) Array.Resize(ref _buffer, _length * 2);
            _buffer[_length++] = b;
        }

        public void PutBits(int value, int count) {
            for (int mask = 1 << (count - 1); mask != 0; mask >>= 1) Put((value & mask) != 0, 128);
        }

        public void PutSigned(int value, int count) {
            Put(value != 0, 128);
            if (value != 0) PutBits(value < 0 ? (-value << 1) | 1 : value << 1, count + 1);
        }

        public byte[] Finish() {
            PutBits(0, 9 - _bits);
            _bits = 0;
            Flush();
            return _buffer[.._length];
        }
    }

    // every macroblock's tokens in decoding order; the number of macroblocks skipped
    int AllTokens<T>(ref T sink, byte* probs) where T : struct, ISink {
        byte* left = stackalloc byte[9];
        Array.Clear(_topNz);
        int skips = 0;
        fixed (short* levels = _levels)
        fixed (byte* tn = _topNz)
            for (int mby = 0; mby < _mbh; mby++) {
                new Span<byte>(left, 9).Clear();
                for (int mbx = 0; mbx < _mbw; mbx++) {
                    int mb = mby * _mbw + mbx;
                    bool i4 = (_info[mb] & 1) != 0;
                    if ((_info[mb] & 2) != 0) {
                        skips++;
                        SkipFlags(i4, tn + mbx * 9, left);
                    } else {
                        MacroblockTokens(ref sink, probs, levels + (long)mb * MbLevels, i4, tn + mbx * 9, left);
                    }
                }
            }
        return skips;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    byte[] Write() {
        // token probabilities fitted to this frame, where the saving pays for the update
        var stats = new int[2 * 1056];
        var probs = (byte[])Vp8Tables.CoeffProbs.Clone();
        int skips;
        fixed (byte* defaults = Vp8Tables.CoeffProbs) {
            var count = new StatSink(stats);
            skips = AllTokens(ref count, defaults);
        }
        for (int i = 0; i < 1056; i++) {
            int c0 = stats[2 * i], c1 = stats[2 * i + 1], old = Vp8Tables.CoeffProbs[i], update = Vp8Tables.CoeffUpdateProbs[i];
            if (c0 + c1 == 0) continue;
            int p = Math.Clamp((int)(((long)c0 * 256 + (c0 + c1) / 2) / (c0 + c1)), 1, 255);
            long before = (long)c0 * Cost0[old] + (long)c1 * Cost1[old] + Cost0[update];
            long after = (long)c0 * Cost0[p] + (long)c1 * Cost1[p] + Cost1[update] + 8 * 256;
            if (after < before) probs[i] = (byte)p;
        }
        int total = _mbw * _mbh, skipProb = Math.Clamp(((total - skips) * 256 + total / 2) / total, 1, 255);

        // the first partition: the frame header, then every macroblock's modes
        var head = new BoolWriter();
        var w = new WriteSink(head);
        head.PutBits(0, 2); // colour space, clamping
        if (_segments > 1) {
            head.PutBits(0b1111, 4); // segments, a map, their data, as absolute values
            for (int s = 0; s < 4; s++) head.PutSigned(_segQ[s], 7);
            for (int s = 0; s < 4; s++) head.PutSigned(_segFilter[s], 6);
            foreach (byte p in _segProbs) {
                head.Put(p != 255, 128);
                if (p != 255) head.PutBits(p, 8);
            }
        } else {
            head.PutBits(0, 1);
        }
        head.PutBits(0, 1); // the normal loop filter
        head.PutBits(_segFilter.Max(), 6); // with absolute segment strengths only whether it is on
        head.PutBits(0, 3); // sharpness
        head.PutBits(0, 1); // no filter deltas
        head.PutBits(0, 2); // one token partition
        head.PutBits(_segQ[0], 7);
        head.PutSigned(0, 4);
        head.PutSigned(0, 4);
        head.PutSigned(0, 4);
        head.PutSigned(_dqUvDc, 4);
        head.PutSigned(_dqUvAc, 4);
        head.PutBits(0, 1); // refresh entropy probabilities
        for (int i = 0; i < 1056; i++) {
            bool changed = probs[i] != Vp8Tables.CoeffProbs[i];
            head.Put(changed, Vp8Tables.CoeffUpdateProbs[i]);
            if (changed) head.PutBits(probs[i], 8);
        }
        head.PutBits(1, 1);
        head.PutBits(skipProb, 8);
        var leftModes = new byte[4];
        Array.Clear(_topModes);
        for (int mby = 0; mby < _mbh; mby++) {
            Array.Clear(leftModes);
            for (int mbx = 0; mbx < _mbw; mbx++) {
                int mb = mby * _mbw + mbx, info = _info[mb], mode16 = (info >> 2) & 3;
                bool i4 = (info & 1) != 0;
                if (_segments > 1) {
                    int segment = _segment[mb];
                    if (head.Put(segment >= 2, _segProbs[0])) head.Put(segment == 3, _segProbs[2]);
                    else head.Put(segment == 1, _segProbs[1]);
                }
                head.Put((info & 2) != 0, skipProb);
                head.Put(!i4, 145);
                if (!i4) {
                    Mode16(ref w, mode16);
                    Array.Fill(_topModes, (byte)mode16, mbx * 4, 4);
                    Array.Fill(leftModes, (byte)mode16);
                } else {
                    for (int n = 0; n < 16; n++) {
                        int mode = _modes[mb * 16 + n];
                        Mode4(ref w, mode, _topModes[mbx * 4 + (n & 3)], leftModes[n >> 2]);
                        _topModes[mbx * 4 + (n & 3)] = leftModes[n >> 2] = (byte)mode;
                    }
                }
                UvMode(ref w, info >> 4);
            }
        }
        byte[] part0 = head.Finish();

        var tokens = new BoolWriter();
        fixed (byte* p = probs) {
            var write = new WriteSink(tokens);
            AllTokens(ref write, p);
        }
        byte[] part1 = tokens.Finish();

        var frame = new byte[10 + part0.Length + part1.Length];
        int tag = 1 << 4 | part0.Length << 5; // a key frame, version 0, shown
        frame[0] = (byte)tag; frame[1] = (byte)(tag >> 8); frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9d; frame[4] = 0x01; frame[5] = 0x2a;
        frame[6] = (byte)_width; frame[7] = (byte)(_width >> 8);
        frame[8] = (byte)_height; frame[9] = (byte)(_height >> 8);
        part0.CopyTo(frame, 10);
        part1.CopyTo(frame, 10 + part0.Length);
        return frame;
    }
}
