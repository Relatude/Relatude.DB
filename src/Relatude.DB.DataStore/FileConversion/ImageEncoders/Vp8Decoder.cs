using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    // what parsing leaves for reconstruction, per macroblock: the dequantized coefficients of 16 luma and 4 + 4 chroma
    // blocks, the sixteen 4x4 modes, the 16x16 mode and the chroma mode
    const int Record = 800, ModesAt = 768, YModeAt = 784, UvModeAt = 785;

    // ── Boolean decoder (as libwebp's VP8BitReader) ──────────────────────────

    // the decoder's working state: copied into a local where many bits are read, so that it stays in registers
    struct BoolState {
        public ulong Value;
        public uint Range;
        public int Bits;
    }

    sealed class BoolReader {
        readonly byte[] _d; // the partition, then zeros
        int _pos, _overrun;
        readonly int _end;
        public BoolState S = new() { Range = 254, Bits = -8 };

        public BoolReader(byte[] d, int start, int length) {
            _d = new byte[length + 8];
            Buffer.BlockCopy(d, start, _d, 0, length);
            _end = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong Next() {
            ulong v = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref _d[_pos])) >> 8;
            if (_pos == _end) _overrun++;
            _pos = Math.Min(_pos + 7, _end);
            return v;
        }

        // read well past the partition: libwebp refuses a stream as soon as it needs a byte beyond it
        public bool Overrun => _overrun > 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Bit(ref BoolState s, int prob) {
            if (s.Bits < 0) {
                s.Value = (s.Value << 56) | Next();
                s.Bits += 56;
            }
            uint split = (s.Range * (uint)prob) >> 8;
            bool bit = (uint)(s.Value >> s.Bits) > split;
            uint range;
            if (bit) {
                range = s.Range - split;
                s.Value -= (ulong)(split + 1) << s.Bits;
            } else {
                range = split + 1;
            }
            int shift = BitOperations.LeadingZeroCount(range) - 24; // the range is never zero
            s.Bits -= shift;
            s.Range = (range << shift) - 1;
            return bit;
        }

        // v with a sign read at probability one half, without a branch (libwebp's VP8GetSigned: the range is
        // below 254 after the first bit of a partition, so the shift is always one)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Sign(ref BoolState s, int v) {
            if (s.Bits < 0) {
                s.Value = (s.Value << 56) | Next();
                s.Bits += 56;
            }
            uint split = s.Range >> 1;
            int mask = (int)(split - (uint)(s.Value >> s.Bits)) >> 31;
            s.Value -= (ulong)((split + 1) & (uint)mask) << s.Bits;
            s.Bits--;
            s.Range = (s.Range + (uint)mask) | 1;
            return (v ^ mask) - mask;
        }

        public bool Bit(int prob) => Bit(ref S, prob);

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
        ImageLimits.ThrowIfTooLarge(Width, Height);
        MbW = (Width + 15) >> 4;
        MbH = (Height + 15) >> 4;
        // the modes of a macroblock take over three bits of the first partition, at probabilities the format fixes:
        // one too short for the size it declares is refused before the planes are allocated
        if (part0 * 4L < (long)MbW * MbH) throw Truncated();
        var br = new BoolReader(d, start + 10, part0);
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
            parts[p] = new BoolReader(d, at, size);
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
    volatile bool _stop; // a stage of the pipeline failed: the others leave the rows still to come

    static ImageFormatException Truncated() => new("WEBP VP8 data is truncated.");

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

    void Macroblocks(BoolReader br, BoolReader[] parts, int[] segFilter, bool useSegment, bool absolute) {
        for (int s = 0; s < 4; s++)
            for (int i4 = 0; i4 < 2; i4++) {
                int level = useSegment ? segFilter[s] + (absolute ? 0 : _level) : _level;
                if (_useLfDelta) level += _refDelta[0] + (i4 == 1 ? _modeDelta[0] : 0);
                _strength[s, i4] = Vp8.StrengthFor(level, _sharpness);
            }
        if (br.Overrun) throw Truncated();
        Y = GC.AllocateUninitializedArray<byte>(MbW * 16 * MbH * 16); // every macroblock is stored whole
        U = GC.AllocateUninitializedArray<byte>(MbW * 8 * MbH * 8);
        V = GC.AllocateUninitializedArray<byte>(MbW * 8 * MbH * 8);
        var info = new byte[MbW * MbH]; // segment | i4 << 2 | inner << 3
        var topModes = new byte[MbW * 4];
        var topNz = new byte[MbW * 9]; // 4 luma, 2 + 2 chroma, 1 luma DC
        // rows are parsed, reconstructed and loop filtered in turn, by three threads when the picture is big enough;
        // a row may be filtered once the row below it is reconstructed, which reads it unfiltered
        bool pipeline = InternalImage.ShouldParallelize(Width, Height);
        int slots = pipeline ? 4 : 1;
        var records = new byte[slots * MbW * Record];
        void Parse(int mby) => ParseRow(mby, br, parts[mby & (_parts - 1)], records, mby % slots * MbW * Record, topModes, topNz, info);
        if (!pipeline) {
            for (int mby = 0; mby < MbH; mby++) {
                Parse(mby);
                ReconstructRow(mby, records, 0, info);
                if (_filterType != 0 && mby > 0) FilterRow(mby - 1, info);
            }
            if (_filterType != 0) FilterRow(MbH - 1, info);
            return;
        }
        var parsed = new SemaphoreSlim(0);
        var free = new SemaphoreSlim(slots);
        var built = new SemaphoreSlim(0);
        // dedicated threads, as the parser waits for free slots: the pipeline cannot stall on a busy thread pool
        static Task Start(Action a) => Task.Factory.StartNew(a, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var building = Start(() => {
            try {
                for (int mby = 0; mby < MbH; mby++) {
                    parsed.Wait();
                    if (_stop) break;
                    ReconstructRow(mby, records, mby % slots * MbW * Record, info);
                    free.Release();
                    built.Release();
                }
            } catch {
                _stop = true;
                throw;
            } finally {
                if (_stop) {
                    free.Release(MbH);
                    built.Release(MbH);
                }
            }
        });
        var filtering = _filterType == 0 ? Task.CompletedTask : Start(() => {
            for (int mby = 0, ready = 0; mby < MbH && !_stop; mby++) {
                for (; ready < Math.Min(mby + 2, MbH); ready++) built.Wait();
                if (!_stop) FilterRow(mby, info);
            }
        });
        try {
            for (int mby = 0; mby < MbH; mby++) {
                free.Wait();
                if (_stop) break;
                Parse(mby);
                parsed.Release();
            }
        } catch {
            _stop = true;
            parsed.Release(MbH);
            throw;
        } finally {
            Task.WaitAll(building, filtering);
        }
    }

    // the modes and coefficients of a row of macroblocks, into info and the records from offset at
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void ParseRow(int mby, BoolReader br, BoolReader tokens, byte[] records, int at, byte[] topModes, byte[] topNz, byte[] info) {
        byte* leftModes = stackalloc byte[4];
        byte* leftNz = stackalloc byte[9];
        byte** bands = stackalloc byte*[4 * 17]; // the probabilities of each block type at each position
        var s = br.S;
        fixed (byte* probs = _probs, records0 = records, tm = topModes, tn = topNz) {
            for (int i = 0; i < 4 * 17; i++) bands[i] = probs + (i / 17 * 8 + Vp8Tables.Bands[i % 17]) * 33;
            byte* rec = records0 + at;
            for (int mbx = 0; mbx < MbW; mbx++, rec += Record) {
                int segment = !_updateMap ? 0 : !br.Bit(ref s, _segProbs[0]) ? (br.Bit(ref s, _segProbs[1]) ? 1 : 0) : (br.Bit(ref s, _segProbs[2]) ? 3 : 2);
                bool skip = _useSkip && br.Bit(ref s, _skipProb);
                bool i4 = !br.Bit(ref s, 145);
                byte* top = tm + mbx * 4;
                if (!i4) {
                    int ymode = br.Bit(ref s, 156) ? (br.Bit(ref s, 128) ? Vp8.TM : Vp8.HE) : (br.Bit(ref s, 163) ? Vp8.VE : Vp8.DC);
                    rec[YModeAt] = (byte)ymode;
                    new Span<byte>(top, 4).Fill((byte)ymode);
                    new Span<byte>(leftModes, 4).Fill((byte)ymode);
                } else {
                    byte* modes = rec + ModesAt;
                    for (int y = 0; y < 4; y++) {
                        int left = leftModes[y];
                        for (int x = 0; x < 4; x++) {
                            int mode = ReadMode4(br, ref s, top[x], left);
                            modes[y * 4 + x] = (byte)mode;
                            top[x] = (byte)mode;
                            left = mode;
                        }
                        leftModes[y] = (byte)left;
                    }
                }
                rec[UvModeAt] = (byte)(!br.Bit(ref s, 142) ? Vp8.DC : !br.Bit(ref s, 114) ? Vp8.VE : br.Bit(ref s, 183) ? Vp8.TM : Vp8.HE);

                byte* nz = tn + mbx * 9;
                bool coded = false;
                if (!skip) {
                    coded = Residuals(tokens, bands, (short*)rec, nz, leftNz, i4, _quant[segment]);
                } else {
                    new Span<byte>(nz, 8).Clear();
                    new Span<byte>(leftNz, 8).Clear();
                    if (!i4) nz[8] = leftNz[8] = 0;
                }
                info[mby * MbW + mbx] = (byte)(segment | (i4 ? 4 : 0) | (i4 || coded ? 8 : 0));
            }
        }
        br.S = s;
        if (br.Overrun || tokens.Overrun) throw Truncated();
    }

    // predictions plus residuals for a row of macroblocks; their records are cleared for the next row
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void ReconstructRow(int mby, byte[] records, int at, byte[] info) {
        byte* yb = stackalloc byte[Vp8.LumaBuffer];
        byte* ub = stackalloc byte[Vp8.ChromaBuffer];
        byte* vb = stackalloc byte[Vp8.ChromaBuffer];
        fixed (byte* yp = Y, up = U, vp = V, records0 = records) {
            byte* rec = records0 + at;
            for (int mbx = 0; mbx < MbW; mbx++, rec += Record) {
                short* coeffs = (short*)rec;
                Vp8.LoadEdges(yb, yp, YStride, 16, mbx, mby, MbW);
                byte* yd = yb + Vp8.Origin;
                if ((info[mby * MbW + mbx] & 4) != 0) {
                    uint topRight = *(uint*)(yd - Vp8.Bps + 16);
                    *(uint*)(yd + 3 * Vp8.Bps + 16) = *(uint*)(yd + 7 * Vp8.Bps + 16) = *(uint*)(yd + 11 * Vp8.Bps + 16) = topRight;
                    for (int n = 0; n < 16; n++) {
                        byte* dst = yd + (n >> 2) * 4 * Vp8.Bps + (n & 3) * 4;
                        Vp8.Predict4(dst, rec[ModesAt + n]);
                        Vp8.AddResidual(coeffs + n * 16, dst);
                    }
                } else {
                    Vp8.PredictBlock(yd, 16, rec[YModeAt], mby > 0, mbx > 0);
                    for (int n = 0; n < 16; n++) Vp8.AddResidual(coeffs + n * 16, yd + (n >> 2) * 4 * Vp8.Bps + (n & 3) * 4);
                }
                Vp8.Store(yb, yp, YStride, 16, mbx, mby);
                Chroma(ub, up, coeffs + 256, rec[UvModeAt], mbx, mby);
                Chroma(vb, vp, coeffs + 320, rec[UvModeAt], mbx, mby);
                new Span<byte>(rec, ModesAt).Clear();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void FilterRow(int mby, byte[] info) {
        fixed (byte* yp = Y, up = U, vp = V)
            for (int mbx = 0; mbx < MbW; mbx++) {
                int i = info[mby * MbW + mbx];
                Vp8.FilterMacroblock(yp, up, vp, YStride, UvStride, mbx, mby, _strength[i & 3, (i >> 2) & 1], (i & 8) != 0, _filterType == 1);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ReadMode4(BoolReader br, ref BoolState s, int top, int left) {
        ref byte p = ref Vp8Tables.BModeProbs[(top * 10 + left) * 9];
        if (!br.Bit(ref s, p)) return Vp8.DC;
        if (!br.Bit(ref s, Unsafe.Add(ref p, 1))) return Vp8.TM;
        if (!br.Bit(ref s, Unsafe.Add(ref p, 2))) return Vp8.VE;
        if (!br.Bit(ref s, Unsafe.Add(ref p, 3))) {
            if (!br.Bit(ref s, Unsafe.Add(ref p, 4))) return Vp8.HE;
            return br.Bit(ref s, Unsafe.Add(ref p, 5)) ? Vp8.VR : Vp8.RD;
        }
        if (!br.Bit(ref s, Unsafe.Add(ref p, 6))) return Vp8.LD;
        if (!br.Bit(ref s, Unsafe.Add(ref p, 7))) return Vp8.VL;
        return br.Bit(ref s, Unsafe.Add(ref p, 8)) ? Vp8.HU : Vp8.HD;
    }

    // the macroblock's coefficients, dequantized; nz holds the above neighbours' non-zero flags, left the left ones
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool Residuals(BoolReader br, byte** bands, short* coeffs, byte* nz, byte* left, bool i4, Vp8.Quant q) {
        bool any = false;
        int first = 0;
        byte** luma = bands + 3 * 17;
        short* dc = stackalloc short[16];
        if (!i4) {
            int n = Coefficients(br, bands + 17, nz[8] + left[8], q.Y2Dc, q.Y2Ac, 0, dc);
            nz[8] = left[8] = (byte)(n > 0 ? 1 : 0);
            if (n > 1) {
                Vp8.InverseWht(dc, coeffs);
            } else {
                short dc0 = (short)((dc[0] + 3) >> 3);
                for (int i = 0; i < 16; i++) coeffs[i * 16] = dc0;
            }
            first = 1;
            luma = bands;
        }
        for (int y = 0; y < 4; y++) {
            int l = left[y];
            for (int x = 0; x < 4; x++) {
                short* block = coeffs + (y * 4 + x) * 16;
                int n = Coefficients(br, luma, l + nz[x], q.Y1Dc, q.Y1Ac, first, block);
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
                    int n = Coefficients(br, bands + 2 * 17, l + nz[4 + ch * 2 + x], q.UvDc, q.UvAc, 0, coeffs + 256 + ch * 64 + (y * 2 + x) * 16);
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
    static int Coefficients(BoolReader br, byte** bands, int ctx, int dcq, int acq, int n, short* output) {
        var s = br.S;
        ref byte zigzag = ref MemoryMarshal.GetArrayDataReference(Vp8Tables.Zigzag);
        byte* p = bands[n] + ctx * 11;
        for (; n < 16; n++) {
            if (!br.Bit(ref s, p[0])) break;
            while (!br.Bit(ref s, p[1])) {
                if (++n == 16) goto done;
                p = bands[n];
            }
            int v;
            if (!br.Bit(ref s, p[2])) {
                v = 1;
                p = bands[n + 1] + 11;
            } else {
                v = LargeValue(br, ref s, p);
                p = bands[n + 1] + 22;
            }
            output[Unsafe.Add(ref zigzag, n)] = (short)(br.Sign(ref s, v) * (n > 0 ? acq : dcq));
        }
        done:
        br.S = s;
        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int LargeValue(BoolReader br, ref BoolState s, byte* p) {
        if (!br.Bit(ref s, p[3])) return !br.Bit(ref s, p[4]) ? 2 : 3 + (br.Bit(ref s, p[5]) ? 1 : 0);
        if (!br.Bit(ref s, p[6])) {
            if (!br.Bit(ref s, p[7])) return 5 + (br.Bit(ref s, 159) ? 1 : 0);
            return 7 + (br.Bit(ref s, 165) ? 2 : 0) + (br.Bit(ref s, 145) ? 1 : 0);
        }
        int bit1 = br.Bit(ref s, p[8]) ? 1 : 0;
        int cat = 2 * bit1 + (br.Bit(ref s, p[9 + bit1]) ? 1 : 0);
        int v = 0;
        foreach (byte prob in Vp8Tables.Cat[cat]) v = v + v + (br.Bit(ref s, prob) ? 1 : 0);
        return v + 3 + (8 << cat);
    }
}
