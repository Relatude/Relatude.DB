using System.Numerics;
using System.Runtime.CompilerServices;

namespace Relatude.DB.VectorIndex;

/// <summary>SIMD helpers. All similarity math is plain dot products since the vectors are unit length.</summary>
internal static class VectorMath {
    /// <summary>Dot product of two equal-length spans. Four independent SIMD accumulators keep the
    /// multiply pipeline busy; a single Vector.Dot per step would do a horizontal reduction on every
    /// iteration.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
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
    public static void AddInPlace(float[] target, ReadOnlySpan<float> source) {
        var t = target.AsSpan();
        int n = t.Length;
        int i = 0;
        if (Vector.IsHardwareAccelerated) {
            int w = Vector<float>.Count;
            for (; i <= n - w; i += w) {
                (new Vector<float>(t.Slice(i, w)) + new Vector<float>(source.Slice(i, w))).CopyTo(t.Slice(i, w));
            }
        }
        for (; i < n; i++) t[i] += source[i];
    }
    /// <summary>Scales the vector to unit length. Returns false for a (near) zero vector.</summary>
    public static bool NormalizeInPlace(float[] v) {
        double sq = 0;
        for (int i = 0; i < v.Length; i++) sq += (double)v[i] * v[i];
        if (sq < 1e-24) return false;
        var inv = (float)(1.0 / Math.Sqrt(sq));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return true;
    }
}
