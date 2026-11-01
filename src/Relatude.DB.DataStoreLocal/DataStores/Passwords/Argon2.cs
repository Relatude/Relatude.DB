using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Relatude.DB.DataStores;

/// <summary>
/// Argon2 version 1.3 (0x13) as RFC 9106 gives it, on the BLAKE2b of RFC 7693, as .NET has neither. Passwords are
/// hashed with Argon2id (<see cref="PasswordProtector"/>); Argon2d and Argon2i differ from it in addressing alone,
/// and are here so their test vectors check both halves of what Argon2id mixes.
/// </summary>
internal static class Argon2 {
    public enum Kind { D = 0, I = 1, Id = 2 }
    const int version = 0x13;
    const int blockWords = 128; // a block is 1024 bytes
    const int blockBytes = 1024;
    const int syncPoints = 4; // the slices of a pass, after each of which the lanes meet

    /// <summary>
    /// The tag of <paramref name="length"/> bytes for the password, with the salt, the secret (a pepper) and the
    /// associated data, filling <paramref name="memoryKiB"/> blocks of a KiB in <paramref name="parallelism"/>
    /// lanes, <paramref name="iterations"/> times.
    /// </summary>
    public static byte[] Hash(Kind kind, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int memoryKiB, int iterations, int parallelism, int length,
        ReadOnlySpan<byte> secret = default, ReadOnlySpan<byte> associatedData = default) {
        if (parallelism < 1 || parallelism > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(parallelism), "Argon2 takes 1 to 2^24-1 lanes. ");
        if (memoryKiB < 8 * parallelism) throw new ArgumentOutOfRangeException(nameof(memoryKiB), "Argon2 needs at least 8 KiB a lane. ");
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations), "Argon2 needs at least one pass. ");
        if (length < 4) throw new ArgumentOutOfRangeException(nameof(length), "Argon2 makes tags of 4 bytes or more. ");
        if (kind is not (Kind.D or Kind.I or Kind.Id)) throw new ArgumentOutOfRangeException(nameof(kind));

        Span<byte> h0 = stackalloc byte[64 + 8]; // H0, then room for a block's number and its lane
        var b = new Blake2b(64);
        b.UpdateLE32(parallelism);
        b.UpdateLE32(length);
        b.UpdateLE32(memoryKiB);
        b.UpdateLE32(iterations);
        b.UpdateLE32(version);
        b.UpdateLE32((int)kind);
        b.UpdateLE32(password.Length);
        b.Update(password);
        b.UpdateLE32(salt.Length);
        b.Update(salt);
        b.UpdateLE32(secret.Length);
        b.Update(secret);
        b.UpdateLE32(associatedData.Length);
        b.Update(associatedData);
        b.Final(h0[..64]);

        var instance = new Instance(kind, memoryKiB, iterations, parallelism);
        Span<byte> bytes = stackalloc byte[blockBytes];
        try {
            // the first two blocks of each lane come from H0
            for (var lane = 0; lane < parallelism; lane++) {
                for (var i = 0; i < 2; i++) {
                    BinaryPrimitives.WriteInt32LittleEndian(h0[64..], i);
                    BinaryPrimitives.WriteInt32LittleEndian(h0[68..], lane);
                    hPrime(h0, bytes);
                    fromBytes(bytes, instance.Block(lane * instance.LaneLength + i));
                }
            }
            for (var pass = 0; pass < iterations; pass++) {
                for (var slice = 0; slice < syncPoints; slice++) {
                    // a lane reads others' blocks only of slices done, so the lanes of a slice can be filled at once
                    if (parallelism == 1) instance.FillSegment(pass, slice, 0);
                    else Parallel.For(0, parallelism, lane => instance.FillSegment(pass, slice, lane));
                }
            }
            // the tag is made of the last blocks of the lanes, XORed together
            Span<ulong> last = stackalloc ulong[blockWords];
            instance.Block(instance.LaneLength - 1).CopyTo(last);
            for (var lane = 1; lane < parallelism; lane++) xor(last, instance.Block(lane * instance.LaneLength + instance.LaneLength - 1));
            toBytes(last, bytes);
            last.Clear();
            var tag = new byte[length];
            hPrime(bytes, tag);
            return tag;
        } finally {
            CryptographicOperations.ZeroMemory(h0);
            CryptographicOperations.ZeroMemory(bytes);
            instance.Clear();
        }
    }

    sealed class Instance {
        readonly Kind _kind;
        readonly int _passes;
        readonly int _lanes;
        readonly int _segment; // blocks in a slice of a lane
        readonly int _blocks; // the memory asked for, rounded down to a whole number of segments
        readonly ulong[] _memory;
        public readonly int LaneLength;

        public Instance(Kind kind, int memoryKiB, int passes, int lanes) {
            _kind = kind;
            _passes = passes;
            _lanes = lanes;
            _segment = memoryKiB / (syncPoints * lanes);
            LaneLength = _segment * syncPoints;
            _blocks = LaneLength * lanes;
            _memory = GC.AllocateUninitializedArray<ulong>(checked(_blocks * blockWords)); // every block is written before it is read
        }

        public Span<ulong> Block(int index) => _memory.AsSpan(index * blockWords, blockWords);
        public void Clear() => Array.Clear(_memory);

        public void FillSegment(int pass, int slice, int lane) {
            Span<ulong> r = stackalloc ulong[blockWords];
            Span<ulong> t = stackalloc ulong[blockWords];
            Span<ulong> addresses = stackalloc ulong[blockWords];
            Span<ulong> input = stackalloc ulong[blockWords];
            Span<ulong> zero = stackalloc ulong[blockWords];
            // Argon2i, and Argon2id in the first half of the first pass, takes its references from a counter rather
            // than from the data, so the order memory is read in says nothing of the password
            var independent = _kind == Kind.I || (_kind == Kind.Id && pass == 0 && slice < syncPoints / 2);
            if (independent) {
                zero.Clear();
                input.Clear();
                input[0] = (ulong)pass;
                input[1] = (ulong)lane;
                input[2] = (ulong)slice;
                input[3] = (ulong)_blocks;
                input[4] = (ulong)_passes;
                input[5] = (ulong)_kind;
            }
            var start = 0;
            if (pass == 0 && slice == 0) {
                start = 2; // the first two came from H0
                if (independent) nextAddresses(input, addresses, zero, r, t);
            }
            var laneLength = LaneLength;
            for (var i = start; i < _segment; i++) {
                var current = lane * laneLength + slice * _segment + i;
                var previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;
                ulong pseudoRandom;
                if (independent) {
                    if (i % blockWords == 0) nextAddresses(input, addresses, zero, r, t);
                    pseudoRandom = addresses[i % blockWords];
                } else {
                    pseudoRandom = _memory[previous * blockWords];
                }
                var refLane = pass == 0 && slice == 0 ? lane : (int)((pseudoRandom >> 32) % (ulong)_lanes);
                var refIndex = indexAlpha(pass, slice, i, (uint)pseudoRandom, refLane == lane);
                // from the second pass on, version 1.3 XORs the new block into the old
                fillBlock(Block(previous), Block(refLane * laneLength + refIndex), Block(current), pass != 0, r, t);
            }
            r.Clear();
            t.Clear();
            addresses.Clear();
        }

        // the block a block refers to, among those the lanes may read at this point (RFC 9106, 3.4.1.2)
        int indexAlpha(int pass, int slice, int index, uint pseudoRandom, bool sameLane) {
            int area;
            if (pass == 0) {
                if (slice == 0) area = index - 1;
                else if (sameLane) area = slice * _segment + index - 1;
                else area = slice * _segment + (index == 0 ? -1 : 0);
            } else {
                if (sameLane) area = LaneLength - _segment + index - 1;
                else area = LaneLength - _segment + (index == 0 ? -1 : 0);
            }
            ulong relative = pseudoRandom;
            relative = relative * relative >> 32;
            relative = (ulong)area - 1 - ((ulong)area * relative >> 32);
            var start = pass != 0 && slice != syncPoints - 1 ? (slice + 1) * _segment : 0;
            return (int)(((ulong)start + relative) % (ulong)LaneLength);
        }
    }

    static void nextAddresses(Span<ulong> input, Span<ulong> addresses, ReadOnlySpan<ulong> zero, Span<ulong> r, Span<ulong> t) {
        input[6]++;
        fillBlock(zero, input, addresses, false, r, t);
        fillBlock(zero, addresses, addresses, false, r, t);
    }

    // G, the compression function: the permutation P over the rows and then the columns of prev ^ refBlock,
    // seen as 8 by 8 pairs of words
    static void fillBlock(ReadOnlySpan<ulong> prev, ReadOnlySpan<ulong> refBlock, Span<ulong> next, bool withXor, Span<ulong> r, Span<ulong> t) {
        for (var i = 0; i < blockWords; i++) r[i] = prev[i] ^ refBlock[i];
        if (withXor) {
            for (var i = 0; i < blockWords; i++) t[i] = r[i] ^ next[i];
        } else {
            r.CopyTo(t);
        }
        for (var i = 0; i < 8; i++) {
            var c = 16 * i;
            permute(ref r[c], ref r[c + 1], ref r[c + 2], ref r[c + 3], ref r[c + 4], ref r[c + 5], ref r[c + 6], ref r[c + 7],
                ref r[c + 8], ref r[c + 9], ref r[c + 10], ref r[c + 11], ref r[c + 12], ref r[c + 13], ref r[c + 14], ref r[c + 15]);
        }
        for (var i = 0; i < 8; i++) {
            var c = 2 * i;
            permute(ref r[c], ref r[c + 1], ref r[c + 16], ref r[c + 17], ref r[c + 32], ref r[c + 33], ref r[c + 48], ref r[c + 49],
                ref r[c + 64], ref r[c + 65], ref r[c + 80], ref r[c + 81], ref r[c + 96], ref r[c + 97], ref r[c + 112], ref r[c + 113]);
        }
        for (var i = 0; i < blockWords; i++) next[i] = t[i] ^ r[i];
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void permute(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3, ref ulong v4, ref ulong v5, ref ulong v6, ref ulong v7,
        ref ulong v8, ref ulong v9, ref ulong v10, ref ulong v11, ref ulong v12, ref ulong v13, ref ulong v14, ref ulong v15) {
        g(ref v0, ref v4, ref v8, ref v12);
        g(ref v1, ref v5, ref v9, ref v13);
        g(ref v2, ref v6, ref v10, ref v14);
        g(ref v3, ref v7, ref v11, ref v15);
        g(ref v0, ref v5, ref v10, ref v15);
        g(ref v1, ref v6, ref v11, ref v12);
        g(ref v2, ref v7, ref v8, ref v13);
        g(ref v3, ref v4, ref v9, ref v14);
    }
    // BLAKE2b's G with BlaMka's multiplications in place of its additions
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void g(ref ulong a, ref ulong b, ref ulong c, ref ulong d) {
        a = blaMka(a, b);
        d = BitOperations.RotateRight(d ^ a, 32);
        c = blaMka(c, d);
        b = BitOperations.RotateRight(b ^ c, 24);
        a = blaMka(a, b);
        d = BitOperations.RotateRight(d ^ a, 16);
        c = blaMka(c, d);
        b = BitOperations.RotateRight(b ^ c, 63);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong blaMka(ulong x, ulong y) => x + y + 2 * ((ulong)(uint)x * (uint)y);

    // H', BLAKE2b stretched to any length (RFC 9106, 3.3)
    static void hPrime(ReadOnlySpan<byte> input, Span<byte> output) {
        var b = new Blake2b(Math.Min(output.Length, 64));
        b.UpdateLE32(output.Length);
        b.Update(input);
        if (output.Length <= 64) {
            b.Final(output);
            return;
        }
        Span<byte> v = stackalloc byte[64];
        b.Final(v);
        var r = (output.Length + 31) / 32 - 2;
        v[..32].CopyTo(output);
        var at = 32;
        for (var i = 2; i <= r; i++) {
            var next = new Blake2b(64);
            next.Update(v);
            next.Final(v);
            v[..32].CopyTo(output[at..]);
            at += 32;
        }
        var last = new Blake2b(output.Length - 32 * r);
        last.Update(v);
        last.Final(output[at..]);
        CryptographicOperations.ZeroMemory(v);
    }

    static void xor(Span<ulong> into, ReadOnlySpan<ulong> other) {
        for (var i = 0; i < blockWords; i++) into[i] ^= other[i];
    }
    static void fromBytes(ReadOnlySpan<byte> bytes, Span<ulong> words) {
        for (var i = 0; i < blockWords; i++) words[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 * i)..]);
    }
    static void toBytes(ReadOnlySpan<ulong> words, Span<byte> bytes) {
        for (var i = 0; i < blockWords; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes[(8 * i)..], words[i]);
    }
}

/// <summary>BLAKE2b (RFC 7693) without a key, digests of 1 to 64 bytes, as Argon2 uses it.</summary>
internal sealed class Blake2b {
    static readonly ulong[] iv = [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];
    static readonly byte[] sigma = [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];
    readonly ulong[] _h = new ulong[8];
    readonly byte[] _buffer = new byte[128];
    readonly int _length;
    int _filled;
    ulong _counter; // bytes compressed; a password hash never comes near the 2^64 that would need the high word

    public Blake2b(int length) {
        if (length < 1 || length > 64) throw new ArgumentOutOfRangeException(nameof(length), "BLAKE2b makes digests of 1 to 64 bytes. ");
        _length = length;
        iv.CopyTo(_h, 0);
        _h[0] ^= 0x01010000UL ^ (ulong)length;
    }
    public void Update(ReadOnlySpan<byte> data) {
        while (data.Length > 0) {
            // a full block is compressed only once more comes, as the last one is compressed as the last
            if (_filled == _buffer.Length) {
                _counter += (ulong)_buffer.Length;
                compress(false);
                _filled = 0;
            }
            var n = Math.Min(_buffer.Length - _filled, data.Length);
            data[..n].CopyTo(_buffer.AsSpan(_filled));
            _filled += n;
            data = data[n..];
        }
    }
    public void UpdateLE32(int value) {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Update(bytes);
    }
    public void Final(Span<byte> output) {
        _counter += (ulong)_filled;
        _buffer.AsSpan(_filled).Clear();
        compress(true);
        Span<byte> digest = stackalloc byte[64];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64LittleEndian(digest[(8 * i)..], _h[i]);
        digest[.._length].CopyTo(output);
        CryptographicOperations.ZeroMemory(digest);
        CryptographicOperations.ZeroMemory(_buffer);
        Array.Clear(_h);
    }

    void compress(bool last) {
        Span<ulong> m = stackalloc ulong[16];
        for (var i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(8 * i));
        Span<ulong> v = stackalloc ulong[16];
        _h.CopyTo(v);
        iv.CopyTo(v[8..]);
        v[12] ^= _counter;
        if (last) v[14] = ~v[14];
        for (var round = 0; round < 12; round++) {
            var s = sigma.AsSpan(16 * (round % 10), 16);
            mix(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            mix(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            mix(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            mix(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            mix(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            mix(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            mix(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            mix(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }
        for (var i = 0; i < 8; i++) _h[i] ^= v[i] ^ v[i + 8];
        m.Clear();
        v.Clear();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void mix(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y) {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
