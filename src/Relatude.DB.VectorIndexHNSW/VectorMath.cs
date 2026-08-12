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
