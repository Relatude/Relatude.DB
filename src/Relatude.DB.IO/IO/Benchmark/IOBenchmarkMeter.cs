using System.Diagnostics;

namespace Relatude.DB.IO;

public sealed class IOBenchmarkMeter {
    const int reservoirCapacity = 20_000;
    readonly long _started = Stopwatch.GetTimestamp();
    readonly long _sliceTicks;
    readonly long _progressIntervalTicks = Stopwatch.Frequency / 10;
    readonly CancellationToken _cancellation;
    readonly Action<double>? _progress;
    readonly long[] _reservoir = new long[reservoirCapacity];
    ulong _randomState = (ulong)Stopwatch.GetTimestamp() | 1;
    long _lastProgress;
    long _operations, _bytes, _measuredTicks, _minTicks = long.MaxValue, _maxTicks;
    double _sumTicks;
    long _verified, _mismatches;

    internal IOBenchmarkMeter(long sliceTicks, CancellationToken cancellation, Action<double>? progress) {
        _sliceTicks = Math.Max(1, sliceTicks);
        _cancellation = cancellation;
        _progress = progress;
    }

    public static long Now => Stopwatch.GetTimestamp();
    public long Operations => _operations;

    public bool Continue() {
        if (_cancellation.IsCancellationRequested) return false;
        var now = Stopwatch.GetTimestamp();
        if (_progress != null && now - _lastProgress >= _progressIntervalTicks) {
            _lastProgress = now;
            _progress(Math.Min(1, (double)(now - _started) / _sliceTicks));
        }
        return _operations == 0 || now - _started < _sliceTicks;
    }

    public void Record(long startedTimestamp, long bytes) {
        var ticks = Stopwatch.GetTimestamp() - startedTimestamp;
        _measuredTicks += ticks;
        _sumTicks += ticks;
        _bytes += bytes;
        if (ticks < _minTicks) _minTicks = ticks;
        if (ticks > _maxTicks) _maxTicks = ticks;
        if (_operations < reservoirCapacity) {
            _reservoir[_operations] = ticks;
        } else {
            var slot = nextRandom() % (ulong)(_operations + 1);
            if (slot < reservoirCapacity) _reservoir[slot] = ticks;
        }
        _operations++;
    }

    public void Include(long startedTimestamp) => _measuredTicks += Stopwatch.GetTimestamp() - startedTimestamp;

    public void Verify(bool matches) {
        _verified++;
        if (!matches) _mismatches++;
    }

    ulong nextRandom() {
        _randomState ^= _randomState >> 12;
        _randomState ^= _randomState << 25;
        _randomState ^= _randomState >> 27;
        return _randomState * 2685821657736338717UL;
    }

    internal void CompleteInto(IOBenchmarkTestResult result) {
        var frequency = (double)Stopwatch.Frequency;
        var seconds = _measuredTicks / frequency;
        result.Operations = _operations;
        result.Bytes = _bytes;
        result.ElapsedMs = seconds * 1000;
        result.BytesPerSecond = seconds > 0 ? _bytes / seconds : 0;
        result.OperationsPerSecond = seconds > 0 ? _operations / seconds : 0;
        result.Verified = _verified;
        result.Mismatches = _mismatches;
        if (_operations == 0) return;
        var sorted = _reservoir.AsSpan(0, (int)Math.Min(_operations, reservoirCapacity)).ToArray();
        Array.Sort(sorted);
        double microseconds(double ticks) => ticks * 1_000_000 / frequency;
        result.Latency = new IOBenchmarkLatency {
            Samples = _operations,
            MinUs = microseconds(_minTicks),
            MeanUs = microseconds(_sumTicks / _operations),
            P50Us = microseconds(percentile(sorted, 0.50)),
            P90Us = microseconds(percentile(sorted, 0.90)),
            P99Us = microseconds(percentile(sorted, 0.99)),
            MaxUs = microseconds(_maxTicks),
        };
    }

    static long percentile(long[] sorted, double share) => sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Length) - 1, 0, sorted.Length - 1)];
}
