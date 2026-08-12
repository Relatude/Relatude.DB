using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Relatude.DB.VectorIndexHNSW;

/// <summary>SIMD helpers. All similarity math is plain dot products since the vectors are unit
/// length. Deliberately a copy of the same file in Relatude.DB.VectorIndex rather than a shared
/// dependency: the two vector index projects are independent plugin assemblies, and this is a
/// handful of stable lines that neither should have to reference the other for.</summary>
internal static class VectorMath {
    /// <summary>Dot product of two equal-length spans. Four independent accumulators keep the
    /// multiply pipeline busy; a single reduction per step would stall it. On x64 the multiply and
    /// add are fused (FMA) — half the arithmetic ops per element — and AVX-512 hardware gets
    /// 16-lane vectors, which <see cref="Vector{T}"/> does not use by default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported && a.Length >= 64) return dot512(a, b);
        if (Fma.IsSupported && a.Length >= 32) return dot256(a, b);
        return dotFallback(a, b);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static float dot512(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var n = a.Length;
        var acc0 = Vector512<float>.Zero;
        var acc1 = Vector512<float>.Zero;
        var acc2 = Vector512<float>.Zero;
        var acc3 = Vector512<float>.Zero;
        var i = 0;
        for (; i <= n - 64; i += 64) {
            acc0 = Avx512F.FusedMultiplyAdd(Vector512.LoadUnsafe(ref ra, (nuint)i), Vector512.LoadUnsafe(ref rb, (nuint)i), acc0);
            acc1 = Avx512F.FusedMultiplyAdd(Vector512.LoadUnsafe(ref ra, (nuint)(i + 16)), Vector512.LoadUnsafe(ref rb, (nuint)(i + 16)), acc1);
            acc2 = Avx512F.FusedMultiplyAdd(Vector512.LoadUnsafe(ref ra, (nuint)(i + 32)), Vector512.LoadUnsafe(ref rb, (nuint)(i + 32)), acc2);
            acc3 = Avx512F.FusedMultiplyAdd(Vector512.LoadUnsafe(ref ra, (nuint)(i + 48)), Vector512.LoadUnsafe(ref rb, (nuint)(i + 48)), acc3);
        }
        acc0 += acc1 + acc2 + acc3;
        for (; i <= n - 16; i += 16) {
            acc0 = Avx512F.FusedMultiplyAdd(Vector512.LoadUnsafe(ref ra, (nuint)i), Vector512.LoadUnsafe(ref rb, (nuint)i), acc0);
        }
        var sum = Vector512.Sum(acc0);
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static float dot256(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var n = a.Length;
        var acc0 = Vector256<float>.Zero;
        var acc1 = Vector256<float>.Zero;
        var acc2 = Vector256<float>.Zero;
        var acc3 = Vector256<float>.Zero;
        var i = 0;
        for (; i <= n - 32; i += 32) {
            acc0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref rb, (nuint)i), acc0);
            acc1 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)(i + 8)), Vector256.LoadUnsafe(ref rb, (nuint)(i + 8)), acc1);
            acc2 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)(i + 16)), Vector256.LoadUnsafe(ref rb, (nuint)(i + 16)), acc2);
            acc3 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)(i + 24)), Vector256.LoadUnsafe(ref rb, (nuint)(i + 24)), acc3);
        }
        acc0 += acc1 + acc2 + acc3;
        for (; i <= n - 8; i += 8) {
            acc0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref rb, (nuint)i), acc0);
        }
        var sum = Vector256.Sum(acc0);
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
    // ---- int8 routing math -------------------------------------------------------------------------
    // The walk scores candidates against a per-vector-scaled int8 copy: a quarter of the memory
    // traffic and, on AVX2, sixteen multiply-adds per instruction. Exactness is not lost — the walk
    // only decides which nodes to look at, and the final candidates are re-scored with the float
    // vectors — so the quantization error only has to be small enough not to misroute, which for
    // unit vectors it comfortably is.

    /// <summary>Quantizes a vector to int8 with a per-vector scale. <paramref name="rescale"/> takes
    /// a lane product back to float space: <c>dot(a, b) ≈ DotQ(qa, qb) · rescaleA · rescaleB</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Quantize(ReadOnlySpan<float> v, Span<sbyte> q, out float rescale) {
        var max = 0f;
        for (var i = 0; i < v.Length; i++) {
            var abs = MathF.Abs(v[i]);
            if (abs > max) max = abs;
        }
        if (max <= 0) {
            q[..v.Length].Clear();
            rescale = 0;
            return;
        }
        var scale = 127f / max;
        rescale = max / 127f;
        var n = v.Length;
        var i2 = 0;
        if (Avx2.IsSupported && n >= 32) {
            ref var rv = ref MemoryMarshal.GetReference(v);
            ref var rq = ref Unsafe.As<sbyte, byte>(ref MemoryMarshal.GetReference(q));
            var vscale = Vector256.Create(scale);
            // pack 32 floats to 32 sbytes per step; the two pack stages interleave 128-bit lanes,
            // which the final cross-lane permute puts back in order (the standard AVX2 idiom)
            var order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);
            for (; i2 <= n - 32; i2 += 32) {
                var i0 = Avx.ConvertToVector256Int32(Avx.Multiply(Vector256.LoadUnsafe(ref rv, (nuint)i2), vscale));
                var i1 = Avx.ConvertToVector256Int32(Avx.Multiply(Vector256.LoadUnsafe(ref rv, (nuint)(i2 + 8)), vscale));
                var i2v = Avx.ConvertToVector256Int32(Avx.Multiply(Vector256.LoadUnsafe(ref rv, (nuint)(i2 + 16)), vscale));
                var i3 = Avx.ConvertToVector256Int32(Avx.Multiply(Vector256.LoadUnsafe(ref rv, (nuint)(i2 + 24)), vscale));
                var packed = Avx2.PackSignedSaturate(Avx2.PackSignedSaturate(i0, i1), Avx2.PackSignedSaturate(i2v, i3));
                var ordered = Avx2.PermuteVar8x32(packed.AsInt32(), order).AsByte();
                ordered.StoreUnsafe(ref rq, (nuint)i2);
            }
        }
        for (; i2 < n; i2++) q[i2] = (sbyte)Math.Clamp(MathF.Round(v[i2] * scale), -127f, 127f);
    }

    /// <summary>Integer dot product of two quantized vectors; multiply by both rescales for the
    /// float-space value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int DotQ(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) {
        var n = a.Length;
        var i = 0;
        var sum = 0;
        if (Avx2.IsSupported && n >= 32) {
            ref var ra = ref MemoryMarshal.GetReference(a);
            ref var rb = ref MemoryMarshal.GetReference(b);
            var acc0 = Vector256<int>.Zero;
            var acc1 = Vector256<int>.Zero;
            for (; i <= n - 32; i += 32) {
                var a0 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref ra, (nuint)i));
                var b0 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref rb, (nuint)i));
                var a1 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref ra, (nuint)(i + 16)));
                var b1 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref rb, (nuint)(i + 16)));
                acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(a0, b0));
                acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(a1, b1));
            }
            sum = Vector256.Sum(Avx2.Add(acc0, acc1));
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static float dotFallback(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        int n = a.Length;
        int i = 0;
        float sum = 0f;
        if (Vector.IsHardwareAccelerated) {
            int w = Vector<float>.Count;
            if (n >= w * 4) {
                var acc0 = Vector<float>.Zero;
                var acc1 = Vector<float>.Zero;
                var acc2 = Vector<float>.Zero;
                var acc3 = Vector<float>.Zero;
                int limit = n - w * 4;
                for (; i <= limit; i += w * 4) {
                    acc0 += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
                    acc1 += new Vector<float>(a.Slice(i + w, w)) * new Vector<float>(b.Slice(i + w, w));
                    acc2 += new Vector<float>(a.Slice(i + 2 * w, w)) * new Vector<float>(b.Slice(i + 2 * w, w));
                    acc3 += new Vector<float>(a.Slice(i + 3 * w, w)) * new Vector<float>(b.Slice(i + 3 * w, w));
                }
                acc0 += acc1 + acc2 + acc3;
                for (; i <= n - w; i += w) acc0 += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
                sum = Vector.Sum(acc0);
            }
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
}
