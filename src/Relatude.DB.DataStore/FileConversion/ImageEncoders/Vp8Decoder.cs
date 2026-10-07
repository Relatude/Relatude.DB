using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>A VP8 key frame (lossy WebP) decoded to Y, U and V planes padded to whole macroblocks.</summary>
internal sealed unsafe class Vp8Decoder {
    public int Width, Height, MbW, MbH;
    public byte[] Y = [], U = [], V = [];
    public int YStride => MbW * 16;
    public int UvStride => MbW * 8;

    readonly byte[] _probs = (byte[])Vp8Tables.CoeffProbs.Clone();
    readonly Vp8.Quant[] _quant = new Vp8.Quant[4];
    readonly Vp8.FilterStrength[,] _strength = new Vp8.FilterStrength[4, 2];
    readonly byte[] _segProbs = [255, 255, 255];
    bool _updateMap, _useSkip, _simple;
    int _skipProb, _filterType, _parts;

    // ── Boolean decoder (as libwebp's VP8BitReader) ──────────────────────────

    sealed class BoolReader {
        readonly byte[] _d;
        int _pos;
        readonly int _end;
        ulong _value;
        uint _range = 254;
        int _bits = -8;
        bool _eof;

        public BoolReader(byte[] d, int start, int end) {
            _d = d;
            _pos = start;
            _end = end;
            Load();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Load() {
            if (_pos + 8 <= _end) {
                _value = (_value << 56) | (BinaryPrimitives.ReadUInt64BigEndian(_d.AsSpan(_pos)) >> 8);
                _pos += 7;
                _bits += 56;
            } else if (_pos < _end) {
                _value = (_value << 8) | _d[_pos++];
                _bits += 8;
            } else if (!_eof) {
                _value <<= 8;
                _bits += 8;
                _eof = true;
            } else {
                _bits = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Bit(int prob) {
            if (_bits < 0) Load();
            uint split = (_range * (uint)prob) >> 8;
            bool bit = (uint)(_value >> _bits) > split;
            uint range;
            if (bit) {
                range = _range - split;
                _value -= (ulong)(split + 1) << _bits;
            } else {
                range = split + 1;
            }
            int shift = 7 ^ BitOperations.Log2(range);
            _bits -= shift;
            _range = (range << shift) - 1;
            return bit;
        }

        public int Value(int bits) {
            int v = 0;
            while (bits-- > 0) v |= (Bit(128) ? 1 : 0) << bits;
            return v;
        }

        public int Signed(int bits) {
            int v = Value(bits);
            return Bit(128) ? -v : v;
        }

        public int Optional(int bits) => Bit(128) ? Signed(bits) : 0;
    }

    public static Vp8Decoder Decode(byte[] d, int start, int length) {
        var dec = new Vp8Decoder();
        dec.Run(d, start, length);
        return dec;
    }

    void Run(byte[] d, int start, int length) {
        if (length < 10) throw new ImageFormatException("WEBP VP8 frame is truncated.");
        int tag = d[start] | d[start + 1] << 8 | d[start + 2] << 16;
        int part0 = tag >> 5;
        if ((tag & 1) != 0) throw new ImageFormatException("WEBP VP8 data is not a key frame.");
        if (d[start + 3] != 0x9d || d[start + 4] != 0x01 || d[start + 5] != 0x2a) throw new ImageFormatException("Invalid WEBP VP8 start code.");
        Width = (d[start + 6] | d[start + 7] << 8) & 0x3fff;
        Height = (d[start + 8] | d[start + 9] << 8) & 0x3fff;
        if (Width == 0 || Height == 0 || 10 + part0 > length) throw new ImageFormatException("Invalid WEBP VP8 frame header.");
        MbW = (Width + 15) >> 4;
        MbH = (Height + 15) >> 4;
        var br = new BoolReader(d, start + 10, start + 10 + part0);
        br.Bit(128); // colour space
        br.Bit(128); // clamping type
        ReadSegmentsAndFilter(br, out int[] segQuant, out int[] segFilter, out bool useSegment, out bool absolute);
        _parts = 1 << br.Value(2);
        var parts = new BoolReader[_parts];
        int at = start + 10 + part0, sizes = at, end = start + length;
        at += 3 * (_parts - 1);
        for (int p = 0; p < _parts; p++) {
            int size = p < _parts - 1 ? d[sizes + 3 * p] | d[sizes + 3 * p + 1] << 8 | d[sizes + 3 * p + 2] << 16 : end - at;
            if (size < 0 || at + size > end) throw new ImageFormatException("WEBP VP8 partition is truncated.");
            parts[p] = new BoolReader(d, at, at + size);
            at += size;
        }
        int baseQ = br.Value(7);
        int dy1Dc = br.Optional(4), dy2Dc = br.Optional(4), dy2Ac = br.Optional(4), duvDc = br.Optional(4), duvAc = br.Optional(4);
        for (int s = 0; s < 4; s++) {
            int q = useSegment ? segQuant[s] + (absolute ? 0 : baseQ) : baseQ;
            _quant[s] = Vp8.QuantFor(q, dy1Dc, dy2Dc, dy2Ac, duvDc, duvAc);
        }
        br.Bit(128); // refresh entropy probabilities
        for (int i = 0; i < _probs.Length; i++)
            if (br.Bit(Vp8Tables.CoeffUpdateProbs[i])) _probs[i] = (byte)br.Value(8);
        _useSkip = br.Bit(128);
        if (_useSkip) _skipProb = br.Value(8);
        Macroblocks(br, parts, segFilter, useSegment, absolute);
    }

    int _level, _sharpness;
    readonly int[] _refDelta = new int[4], _modeDelta = new int[4];
    bool _useLfDelta;

    void ReadSegmentsAndFilter(BoolReader br, out int[] quant, out int[] filter, out bool useSegment, out bool absolute) {
        quant = new int[4];
        filter = new int[4];
        absolute = false;
        useSegment = br.Bit(128);
        if (useSegment) {
            _updateMap = br.Bit(128);
            if (br.Bit(128)) {
                absolute = br.Bit(128);
                for (int s = 0; s < 4; s++) quant[s] = br.Optional(7);
                for (int s = 0; s < 4; s++) filter[s] = br.Optional(6);
            }
            if (_updateMap)
                for (int s = 0; s < 3; s++) _segProbs[s] = br.Bit(128) ? (byte)br.Value(8) : (byte)255;
        }
        _simple = br.Bit(128);
        _level = br.Value(6);
        _sharpness = br.Value(3);
        _useLfDelta = br.Bit(128);
        if (_useLfDelta && br.Bit(128)) {
            for (int i = 0; i < 4; i++) if (br.Bit(128)) _refDelta[i] = br.Signed(6);
            for (int i = 0; i < 4; i++) if (br.Bit(128)) _modeDelta[i] = br.Signed(6);
        }
        _filterType = _level == 0 ? 0 : _simple ? 1 : 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Macroblocks(BoolReader br, BoolReader[] parts, int[] segFilter, bool useSegment, bool absolute) {
        for (int s = 0; s < 4; s++)
            for (int i4 = 0; i4 < 2; i4++) {
                int level = useSegment ? segFilter[s] + (absolute ? 0 : _level) : _level;
                if (_useLfDelta) level += _refDelta[0] + (i4 == 1 ? _modeDelta[0] : 0);
                _strength[s, i4] = Vp8.StrengthFor(level, _sharpness);
            }
        Y = new byte[MbW * 16 * MbH * 16];
        U = new byte[MbW * 8 * MbH * 8];
        V = new byte[MbW * 8 * MbH * 8];
        var info = new byte[MbW * MbH]; // segment | i4 << 2 | inner << 3
        var topModes = new byte[MbW * 4];
        var topNz = new byte[MbW * 9];  // 4 luma, 2 + 2 chroma, 1 luma DC
        byte* leftModes = stackalloc byte[4];
        byte* leftNz = stackalloc byte[9];
        byte* modes = stackalloc byte[16];
        short* coeffs = stackalloc short[25 * 16];
        byte* yb = stackalloc byte[Vp8.LumaBuffer];
        byte* ub = stackalloc byte[Vp8.ChromaBuffer];
        byte* vb = stackalloc byte[Vp8.ChromaBuffer];
        fixed (byte* yp = Y, up = U, vp = V, probs = _probs, tm = topModes, tn = topNz) {
            for (int mby = 0; mby < MbH; mby++) {
                var tokens = parts[mby & (_parts - 1)];
                new Span<byte>(leftModes, 4).Clear();
                new Span<byte>(leftNz, 9).Clear();
                for (int mbx = 0; mbx < MbW; mbx++) {
                    int segment = !_updateMap ? 0 : !br.Bit(_segProbs[0]) ? (br.Bit(_segProbs[1]) ? 1 : 0) : (br.Bit(_segProbs[2]) ? 3 : 2);
                    bool skip = _useSkip && br.Bit(_skipProb);
                    bool i4 = !br.Bit(145);
                    int ymode = 0;
                    byte* top = tm + mbx * 4;
                    if (!i4) {
                        ymode = br.Bit(156) ? (br.Bit(128) ? Vp8.TM : Vp8.HE) : (br.Bit(163) ? Vp8.VE : Vp8.DC);
                        new Span<byte>(top, 4).Fill((byte)ymode);
                        new Span<byte>(leftModes, 4).Fill((byte)ymode);
                    } else {
                        for (int y = 0; y < 4; y++) {
                            int left = leftModes[y];
                            for (int x = 0; x < 4; x++) {
                                int mode = ReadMode4(br, top[x], left);
                                modes[y * 4 + x] = (byte)mode;
                                top[x] = (byte)mode;
                                left = mode;
                            }
                            leftModes[y] = (byte)left;
                        }
                    }
                    int uvmode = !br.Bit(142) ? Vp8.DC : !br.Bit(114) ? Vp8.VE : br.Bit(183) ? Vp8.TM : Vp8.HE;

                    byte* nz = tn + mbx * 9;
                    bool coded = false;
                    new Span<short>(coeffs, 25 * 16).Clear();
                    if (!skip) {
                        coded = Residuals(tokens, probs, coeffs, nz, leftNz, i4, _quant[segment]);
                    } else {
                        new Span<byte>(nz, 8).Clear();
                        new Span<byte>(leftNz, 8).Clear();
                        if (!i4) nz[8] = leftNz[8] = 0;
                    }
                    info[mby * MbW + mbx] = (byte)(segment | (i4 ? 4 : 0) | (i4 || coded ? 8 : 0));

                    Vp8.LoadEdges(yb, yp, YStride, 16, mbx, mby, MbW);
                    byte* yd = yb + Vp8.Origin;
                    if (i4) {
                        uint topRight = *(uint*)(yd - Vp8.Bps + 16);
                        *(uint*)(yd + 3 * Vp8.Bps + 16) = *(uint*)(yd + 7 * Vp8.Bps + 16) = *(uint*)(yd + 11 * Vp8.Bps + 16) = topRight;
                        for (int n = 0; n < 16; n++) {
                            byte* dst = yd + (n >> 2) * 4 * Vp8.Bps + (n & 3) * 4;
                            Vp8.Predict4(dst, modes[n]);
                            Vp8.AddResidual(coeffs + n * 16, dst);
                        }
                    } else {
                        Vp8.PredictBlock(yd, 16, ymode, mby > 0, mbx > 0);
                        for (int n = 0; n < 16; n++) Vp8.AddResidual(coeffs + n * 16, yd + (n >> 2) * 4 * Vp8.Bps + (n & 3) * 4);
                    }
                    Vp8.Store(yb, yp, YStride, 16, mbx, mby);
                    Chroma(ub, up, coeffs + 256, uvmode, mbx, mby);
                    Chroma(vb, vp, coeffs + 320, uvmode, mbx, mby);
                }
            }
            if (_filterType == 0) return;
            for (int mby = 0; mby < MbH; mby++)
                for (int mbx = 0; mbx < MbW; mbx++) {
                    int i = info[mby * MbW + mbx];
                    Vp8.FilterMacroblock(yp, up, vp, YStride, UvStride, mbx, mby, _strength[i & 3, (i >> 2) & 1], (i & 8) != 0, _filterType == 1);
                }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Chroma(byte* buf, byte* plane, short* coeffs, int mode, int mbx, int mby) {
        Vp8.LoadEdges(buf, plane, UvStride, 8, mbx, mby, MbW);
        byte* d = buf + Vp8.Origin;
        Vp8.PredictBlock(d, 8, mode, mby > 0, mbx > 0);
        for (int n = 0; n < 4; n++) Vp8.AddResidual(coeffs + n * 16, d + (n >> 1) * 4 * Vp8.Bps + (n & 1) * 4);
        Vp8.Store(buf, plane, UvStride, 8, mbx, mby);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int ReadMode4(BoolReader br, int top, int left) {
        int p = (top * 10 + left) * 9;
        var probs = Vp8Tables.BModeProbs;
        if (!br.Bit(probs[p])) return Vp8.DC;
        if (!br.Bit(probs[p + 1])) return Vp8.TM;
        if (!br.Bit(probs[p + 2])) return Vp8.VE;
        if (!br.Bit(probs[p + 3])) {
            if (!br.Bit(probs[p + 4])) return Vp8.HE;
            return br.Bit(probs[p + 5]) ? Vp8.VR : Vp8.RD;
        }
        if (!br.Bit(probs[p + 6])) return Vp8.LD;
        if (!br.Bit(probs[p + 7])) return Vp8.VL;
        return br.Bit(probs[p + 8]) ? Vp8.HU : Vp8.HD;
    }

    // the macroblock's coefficients, dequantized; nz holds the above neighbours' non-zero flags, left the left ones
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool Residuals(BoolReader br, byte* probs, short* coeffs, byte* nz, byte* left, bool i4, Vp8.Quant q) {
        bool any = false;
        int first = 0, type = 3;
        if (!i4) {
            short* dc = coeffs + 384;
            int n = Coefficients(br, probs, 1, nz[8] + left[8], q.Y2Dc, q.Y2Ac, 0, dc);
            nz[8] = left[8] = (byte)(n > 0 ? 1 : 0);
            if (n > 1) {
                Vp8.InverseWht(dc, coeffs);
            } else {
                short dc0 = (short)((dc[0] + 3) >> 3);
                for (int i = 0; i < 16; i++) coeffs[i * 16] = dc0;
            }
            first = 1;
            type = 0;
        }
        for (int y = 0; y < 4; y++) {
            int l = left[y];
            for (int x = 0; x < 4; x++) {
                short* block = coeffs + (y * 4 + x) * 16;
                int n = Coefficients(br, probs, type, l + nz[x], q.Y1Dc, q.Y1Ac, first, block);
                l = n > first ? 1 : 0;
                nz[x] = (byte)l;
                any |= n > first || block[0] != 0;
            }
            left[y] = (byte)l;
        }
        for (int ch = 0; ch < 2; ch++) {
            for (int y = 0; y < 2; y++) {
                int l = left[4 + ch * 2 + y];
                for (int x = 0; x < 2; x++) {
                    int n = Coefficients(br, probs, 2, l + nz[4 + ch * 2 + x], q.UvDc, q.UvAc, 0, coeffs + 256 + ch * 64 + (y * 2 + x) * 16);
                    l = n > 0 ? 1 : 0;
                    nz[4 + ch * 2 + x] = (byte)l;
                    any |= l != 0;
                }
                left[4 + ch * 2 + y] = (byte)l;
            }
        }
        return any;
    }

    // one block's tokens (as libwebp's GetCoeffs); returns the position after the last coefficient read
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int Coefficients(BoolReader br, byte* probs, int type, int ctx, int dcq, int acq, int n, short* output) {
        byte* p = probs + ((type * 8 + Vp8Tables.Bands[n]) * 3 + ctx) * 11;
        for (; n < 16; n++) {
            if (!br.Bit(p[0])) return n;
            while (!br.Bit(p[1])) {
                if (++n == 16) return 16;
                p = probs + (type * 8 + Vp8Tables.Bands[n]) * 33;
            }
            byte* next = probs + (type * 8 + Vp8Tables.Bands[n + 1]) * 33;
            int v;
            if (!br.Bit(p[2])) {
                v = 1;
                p = next + 11;
            } else {
                v = LargeValue(br, p);
                p = next + 22;
            }
            if (br.Bit(128)) v = -v;
            output[Vp8Tables.Zigzag[n]] = (short)(v * (n > 0 ? acq : dcq));
        }
        return 16;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int LargeValue(BoolReader br, byte* p) {
        if (!br.Bit(p[3])) return !br.Bit(p[4]) ? 2 : 3 + (br.Bit(p[5]) ? 1 : 0);
        if (!br.Bit(p[6])) {
            if (!br.Bit(p[7])) return 5 + (br.Bit(159) ? 1 : 0);
            return 7 + (br.Bit(165) ? 2 : 0) + (br.Bit(145) ? 1 : 0);
        }
        int bit1 = br.Bit(p[8]) ? 1 : 0;
        int cat = 2 * bit1 + (br.Bit(p[9 + bit1]) ? 1 : 0);
        int v = 0;
        foreach (byte prob in Vp8Tables.Cat[cat]) v = v + v + (br.Bit(prob) ? 1 : 0);
        return v + 3 + (8 << cat);
    }
}
