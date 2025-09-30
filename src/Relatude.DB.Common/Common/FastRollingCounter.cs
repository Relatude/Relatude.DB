using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Relatude.DB.Common;
public sealed class FastRollingCounter {
    private readonly int _windowSeconds;       // e.g., 10
    private readonly int _bucketCount;         // power-of-two >= windowSeconds+1 (e.g., 64)
    private readonly long[] _bucketSecond;     // last second this bucket was used
    private readonly int[] _bucketCounts;      // counts for that second
    private readonly long _startTicks;

    public FastRollingCounter(int windowSeconds = 10, int bucketCount = 64) {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (bucketCount <= windowSeconds) throw new ArgumentOutOfRangeException(nameof(bucketCount), "Must exceed window size.");
        if ((bucketCount & (bucketCount - 1)) != 0) throw new ArgumentException("bucketCount must be a power of two.", nameof(bucketCount));

        _windowSeconds = windowSeconds;
        _bucketCount = bucketCount;
        _bucketSecond = new long[_bucketCount];
        _bucketCounts = new int[_bucketCount];
        _startTicks = Stopwatch.GetTimestamp();

        for (int i = 0; i < _bucketCount; i++)
            _bucketSecond[i] = long.MinValue; // mark as unused
    }

    /// <summary>Record one occurrence (lock-free, O(1)).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record() {
        long s = CurrentSecond();
        int idx = (int)(s & (_bucketCount - 1)); // fast modulo (power-of-two)

        long bucketSec = Volatile.Read(ref _bucketSecond[idx]);
        if (bucketSec != s) {
            // Move/clear bucket to represent the current second.
            // Races are acceptable for a rough estimate.
            Volatile.Write(ref _bucketSecond[idx], s);
            Interlocked.Exchange(ref _bucketCounts[idx], 0);
        }

        Interlocked.Increment(ref _bucketCounts[idx]);
    }

    /// <summary>Approximate count over the last N seconds (default N=10).</summary>
    public int EstimateLastWindow() => EstimateLastSeconds(_windowSeconds);

    /// <summary>Approximate count over the last 10 seconds.</summary>
    public int EstimateLast10Seconds() => EstimateLastSeconds(10);

    /// <summary>Rough rate (per second) over the configured window.</summary>
    public double RatePerSecond() {
        int n = EstimateLastWindow();
        return n / (double)_windowSeconds;
    }

    private int EstimateLastSeconds(int seconds) {
        if (seconds <= 0) return 0;

        long nowS = CurrentSecond();
        long fromS = nowS - seconds + 1;
        int sum = 0;

        // Scan all buckets; only sum those whose second is within the window.
        for (int i = 0; i < _bucketCount; i++) {
            long bs = Volatile.Read(ref _bucketSecond[i]);
            if (bs >= fromS && bs <= nowS) {
                sum += Volatile.Read(ref _bucketCounts[i]);
            }
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CurrentSecond() {
        long elapsedTicks = Stopwatch.GetTimestamp() - _startTicks;
        return elapsedTicks / Stopwatch.Frequency;
    }
}
