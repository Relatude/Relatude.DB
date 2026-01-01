using System;
using System.Collections.Generic;
using System.Linq;


public class RunningZeroEstimator  {
    private readonly int _maxSamples;
    private readonly TimeSpan _maxAge;
    private readonly Queue<(DateTime Time, int Value)> _samples = new();
    private readonly object _lock = new();

    public RunningZeroEstimator(int maxSamples, TimeSpan maxAge) {
        _maxSamples = maxSamples > 1 ? maxSamples : throw new ArgumentException("Need at least 2 samples.");
        _maxAge = maxAge;
    }
    public void ReportValue(int value) {
        lock (_lock) {
            if (value <= 0) {
                _samples.Clear();
                return;
            }
            DateTime timestampUtc = DateTime.UtcNow;
            _samples.Enqueue((timestampUtc, value));
            Prune(timestampUtc);
        }
    }
    public bool TryEstimateDurationUntilZero(out TimeSpan duration) {
        DateTime nowUtc = DateTime.UtcNow;

        duration = TimeSpan.Zero;

        lock (_lock) {
            Prune(nowUtc);

            if (_samples.Count < 2) return false;

            // Convert Time to a numerical scale (seconds from the first sample)
            var data = _samples.ToList();
            DateTime t0 = data[0].Time;

            double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
            int n = data.Count;

            foreach (var s in data) {
                double x = (s.Time - t0).TotalSeconds;
                double y = s.Value;
                sumX += x;
                sumY += y;
                sumXy += x * y;
                sumXx += x * x;
            }

            // Standard Linear Regression formula for slope (m)
            double denominator = (n * sumXx - sumX * sumX);
            if (Math.Abs(denominator) < 1e-9) return false; // Prevent div by zero

            double m = (n * sumXy - sumX * sumY) / denominator;

            // If slope is positive or zero, it will never reach zero
            if (m >= 0) return false;

            double b = (sumY - m * sumX) / n;

            // Solve for y = 0: 0 = mx + b => x = -b/m
            double targetSeconds = -b / m;
            DateTime targetTime = t0.AddSeconds(targetSeconds);

            duration = targetTime - nowUtc;

            // If the estimated time is already in the past, return Zero
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            return true;
        }
    }

    private void Prune(DateTime nowUtc) {
        // Remove samples that are too old
        while (_samples.Count > 0 && (nowUtc - _samples.Peek().Time) > _maxAge) {
            _samples.Dequeue();
        }

        // Remove oldest samples if we exceed the count limit
        while (_samples.Count > _maxSamples) {
            _samples.Dequeue();
        }
    }
}