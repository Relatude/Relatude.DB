namespace Relatude.DB.Common;
public static class RangeGenerators {
    public static RangeGenerator<decimal> Decimals = new();
    public static RangeGenerator<float> Floats = new();
    public static RangeGenerator<int> Ints = new();
    public static RangeGenerator<long> Longs = new();
    public static RangeGenerator<double> Doubles = new();
    public static RangeGenerator<byte> Bytes = new();
    public static RangeGenerator<DateTime> DateTimes = new();
    public static RangeGenerator<TimeSpan> TimeSpans = new();
    public static RangeGenerator<T>? TryGet<T>() where T : notnull { // null for types that cannot be range-bucketed
        if (typeof(T) == typeof(decimal)) return (RangeGenerator<T>)(object)Decimals;
        if (typeof(T) == typeof(float)) return (RangeGenerator<T>)(object)Floats;
        if (typeof(T) == typeof(int)) return (RangeGenerator<T>)(object)Ints;
        if (typeof(T) == typeof(long)) return (RangeGenerator<T>)(object)Longs;
        if (typeof(T) == typeof(double)) return (RangeGenerator<T>)(object)Doubles;
        if (typeof(T) == typeof(byte)) return (RangeGenerator<T>)(object)Bytes;
        if (typeof(T) == typeof(DateTime)) return (RangeGenerator<T>)(object)DateTimes;
        if (typeof(T) == typeof(TimeSpan)) return (RangeGenerator<T>)(object)TimeSpans;
        return null;
    }
}
// Generates the range buckets shown in facet UIs. Boundaries are chosen the way chart axes pick tick
// marks: every boundary is an aligned multiple of a human-friendly step (1, 2, 2.5 or 5 times a power
// of ten for numbers; seconds/minutes/hours/days/weeks/quarters/years for DateTime; similar duration
// steps for TimeSpan). The finest step that keeps the bucket count within maxNoRanges is used, so the
// requested count is approached as closely as nice boundaries allow - a 50..60 span asked for 10
// buckets gives ten 1-wide buckets, not one rounded-away bucket.
// Ranges are contiguous: Item2 of each range equals Item1 of the next, the first Item1 is at or below
// the given minimum and the last Item2 at or above the given maximum, so half-open buckets built from
// the boundaries cover every value with no gaps.
public class RangeGenerator<T> where T : notnull { // only the types listed in RangeGenerators are supported (convertToT/convertToDecimal)
    static readonly bool _isDateTime = typeof(T) == typeof(DateTime);
    static readonly bool _isTimeSpan = typeof(T) == typeof(TimeSpan);
    static readonly bool _isIntegral = typeof(T) == typeof(byte) || typeof(T) == typeof(int) || typeof(T) == typeof(long);
    T convertToT(decimal n) {
        // byte,  long, int, float, double, decimal
        if (typeof(T) == typeof(byte)) {
            if (n > byte.MaxValue) return (T)(object)byte.MaxValue;
            if (n < byte.MinValue) return (T)(object)byte.MinValue;
            return (T)(object)(byte)n;
        }
        if (typeof(T) == typeof(long)) {
            if (n > long.MaxValue) return (T)(object)long.MaxValue;
            if (n < long.MinValue) return (T)(object)long.MinValue;
            return (T)(object)(long)n;
        }
        if (typeof(T) == typeof(int)) {
            if (n > int.MaxValue) return (T)(object)int.MaxValue;
            if (n < int.MinValue) return (T)(object)int.MinValue;
            return (T)(object)(int)n;
        }
        if (typeof(T) == typeof(float)) {
            return (T)(object)(float)n;
        }
        if (typeof(T) == typeof(double)) {
            return (T)(object)(double)n;
        }
        if (typeof(T) == typeof(decimal)) return (T)(object)n;
        if (typeof(T) == typeof(DateTime)) {
            if (n > DateTime.MaxValue.Ticks) return (T)(object)DateTime.MaxValue;
            if (n < DateTime.MinValue.Ticks) return (T)(object)DateTime.MinValue;
            return (T)(object)new DateTime((long)n);
        }
        if (typeof(T) == typeof(TimeSpan)) {
            if (n > TimeSpan.MaxValue.Ticks) return (T)(object)TimeSpan.MaxValue;
            if (n < TimeSpan.MinValue.Ticks) return (T)(object)TimeSpan.MinValue;
            return (T)(object)new TimeSpan((long)n);
        }
        throw new NotImplementedException();
    }
    static decimal convertToDecimal(T value) {
        if (typeof(T) == typeof(DateTime)) return ((DateTime)(object)value!).Ticks;
        if (typeof(T) == typeof(TimeSpan)) return ((TimeSpan)(object)value!).Ticks;
        if (typeof(T) == typeof(double) || typeof(T) == typeof(float)) { // may exceed the decimal range
            var d = Convert.ToDouble(value);
            if (double.IsNaN(d)) return 0;
            if (d >= (double)decimal.MaxValue) return decimal.MaxValue;
            if (d <= (double)decimal.MinValue) return decimal.MinValue;
        }
        return Convert.ToDecimal(value);
    }
    static readonly Dictionary<string, List<Tuple<T, T>>> _rangeCache = new();
    const int _maxCachedResults = 5000; // min/max follow the data, so keys keep changing on live stores; reset rather than grow forever
    public List<Tuple<T, T>> GetRanges(T value1, T value2, int maxNoRanges, double powerBase, byte precision) {
        // precision is kept for signature compatibility but is no longer used: boundary "niceness" is
        // derived from the bucket width instead of a fixed number of significant digits
        var key = typeof(T).Name + "|" + value1 + "|" + value2 + "|" + maxNoRanges + "|" + powerBase;
        lock (_rangeCache) {
            if (_rangeCache.TryGetValue(key, out var cached)) return cached;
            var ranges = generateRanges(value1, value2, maxNoRanges, powerBase);
            if (_rangeCache.Count >= _maxCachedResults) _rangeCache.Clear();
            _rangeCache.Add(key, ranges);
            return ranges;
        }
    }
    List<Tuple<T, T>> generateRanges(T value1, T value2, int maxNoRanges, double powerBase) {
        try {
            var min = convertToDecimal(value1);
            var max = convertToDecimal(value2);
            if (min > max) { (min, max) = (max, min); (value1, value2) = (value2, value1); }
            if (min == max) return new() { new(value1, value2) };
            if (maxNoRanges < 1) maxNoRanges = 1;
            if (double.IsNaN(powerBase) || double.IsInfinity(powerBase) || powerBase <= 0) powerBase = 1;
            if (min < 0) powerBase = 1; // a power curve anchored at a negative minimum gives unintuitive buckets
            List<decimal> bounds;
            if (_isDateTime) bounds = dateTimeBoundaries(min, max, maxNoRanges, powerBase);
            else if (_isTimeSpan) bounds = timeSpanBoundaries(min, max, maxNoRanges, powerBase);
            else bounds = numberBoundaries(min, max, maxNoRanges, powerBase);
            bounds = strictlyAscending(bounds);
            if (bounds.Count < 2) bounds = new() { min, max };
            var ranges = new List<Tuple<T, T>>(bounds.Count - 1);
            for (var i = 0; i < bounds.Count - 1; i++) ranges.Add(new(convertToT(bounds[i]), convertToT(bounds[i + 1])));
            // conversion clamps (byte/int/long/DateTime limits, doubles beyond the decimal range) can
            // pull the outer boundaries inside the data; stretch them back so every value is covered:
            var c = Comparer<T>.Default;
            if (c.Compare(ranges[0].Item1, value1) > 0) ranges[0] = new(value1, ranges[0].Item2);
            var last = ranges.Count - 1;
            if (c.Compare(ranges[last].Item2, value2) < 0) ranges[last] = new(ranges[last].Item1, value2);
            return ranges;
        } catch (OverflowException) { // magnitudes at the edge of the decimal range: one covering range is always safe
            return new() { new(value1, value2) };
        }
    }
    // asking for e.g. 10 buckets and landing on 11 nice ones is better for a UI than dropping to 6
    // coarse ones, so the cap is soft: up to ~20% more buckets than asked for are accepted (a request
    // for a single range is always honored exactly)
    static int maxAllowedRanges(int maxNoRanges) => maxNoRanges == 1 ? 1 : maxNoRanges + Math.Max(1, maxNoRanges / 5);
    static List<decimal> strictlyAscending(List<decimal> bounds) {
        var result = new List<decimal>(bounds.Count); // snapping/clamping can produce equal neighbours; drop them
        foreach (var b in bounds) if (result.Count == 0 || b > result[^1]) result.Add(b);
        return result;
    }
    static List<decimal> boundariesOf(decimal start, decimal step, int count) {
        var bounds = new List<decimal>(count + 1);
        for (var i = 0; i <= count; i++) bounds.Add(start + step * i);
        return bounds;
    }

    // ---- numbers ----------------------------------------------------------------------------------
    static List<decimal> numberBoundaries(decimal min, decimal max, int maxNoRanges, double powerBase) {
        if (powerBase != 1) return powerBoundaries(min, max, maxNoRanges, powerBase, gap => largestNiceStepAtMost(gap / 2, _isIntegral));
        var allowed = maxAllowedRanges(maxNoRanges);
        var rawStep = ((double)max - (double)min) / maxNoRanges;
        foreach (var step in ascendingNiceSteps(rawStep, _isIntegral)) {
            var start = Math.Floor(min / step) * step;
            var end = Math.Ceiling(max / step) * step;
            var count = (end - start) / step;
            if (count > allowed) continue;
            return boundariesOf(start, step, (int)count);
        }
        return new() { min, max }; // no nice step fits (span at the edge of the decimal range)
    }
    static readonly decimal[] _niceMantissas = [1m, 2m, 2.5m, 5m];
    static readonly decimal[] _niceMantissasDescending = [5m, 2.5m, 2m, 1m];
    static IEnumerable<decimal> ascendingNiceSteps(double rawStep, bool integral) {
        if (!(rawStep > 0)) rawStep = double.Epsilon;
        var k = (int)Math.Floor(Math.Log10(rawStep)) - 1; // start a decade fine, so the first fitting step is as fine as allowed
        if (k < -28) k = -28;
        if (integral && k < 0) k = 0; // steps below 1 cannot produce distinct integer boundaries
        for (; k <= 28; k++) {
            var p = pow10(k);
            foreach (var m in _niceMantissas) {
                var step = m * p;
                if (integral && step != decimal.Truncate(step)) continue; // 2.5 only once it is whole (25, 250, ...)
                yield return step;
            }
        }
    }
    static decimal largestNiceStepAtMost(decimal maxStep, bool integral) {
        if (integral && maxStep < 1) return 1;
        if (maxStep <= 0) return 1e-28m; // degenerate gap from double underflow
        var k = (int)Math.Floor(Math.Log10((double)maxStep));
        if (k > 28) k = 28;
        for (; k >= -28; k--) {
            var p = pow10(k);
            foreach (var m in _niceMantissasDescending) {
                var step = m * p;
                if (integral && step != decimal.Truncate(step)) continue;
                if (step <= maxStep) return step;
            }
        }
        return integral ? 1m : 1e-28m;
    }
    static decimal pow10(int k) {
        var r = 1m;
        for (var i = 0; i < k; i++) r *= 10m;
        for (var i = 0; i > k; i--) r /= 10m;
        return r;
    }
    // Power buckets (finer near the minimum when powerBase > 1, typical for prices): raw boundaries
    // follow the curve min + delta * (i/n)^powerBase, then each boundary is snapped to the nearest
    // multiple of a nice granularity derived from half its local bucket width - boundaries stay round
    // without collapsing into each other (each moves less than half a local gap, order is preserved).
    static List<decimal> powerBoundaries(decimal min, decimal max, int n, double powerBase, Func<decimal, decimal> granularityOfGap) {
        var delta = (double)max - (double)min;
        var raw = new decimal[n + 1];
        raw[0] = min;
        raw[n] = max;
        for (var i = 1; i < n; i++) raw[i] = min + (decimal)(delta * Math.Pow((double)i / n, powerBase));
        var bounds = new List<decimal>(n + 1);
        var gFirst = granularityOfGap(raw[1] - raw[0]);
        bounds.Add(Math.Floor(min / gFirst) * gFirst);
        for (var i = 1; i < n; i++) {
            var gap = Math.Min(raw[i] - raw[i - 1], raw[i + 1] - raw[i]);
            var g = granularityOfGap(gap);
            bounds.Add(Math.Round(raw[i] / g, MidpointRounding.AwayFromZero) * g);
        }
        var gLast = granularityOfGap(raw[n] - raw[n - 1]);
        bounds.Add(Math.Ceiling(max / gLast) * gLast);
        return bounds;
    }

    // ---- TimeSpan ---------------------------------------------------------------------------------
    // nice sub-second tick multiples, natural clock steps (seconds/minutes/hours), then day counts
    // including weeks, extended by whole "years" of days to cover any span
    static readonly decimal[] _timeSpanSteps = buildTimeSpanSteps();
    static decimal[] buildTimeSpanSteps() {
        var steps = new List<decimal>();
        var tick = 1m;
        for (var k = 0; k <= 6; k++) { // 100ns up to 0.5s in 1-2-5 steps
            steps.Add(tick); steps.Add(2 * tick); steps.Add(5 * tick);
            tick *= 10;
        }
        foreach (var s in new[] { 1, 2, 5, 10, 15, 30 }) steps.Add(s * (decimal)TimeSpan.TicksPerSecond);
        foreach (var m in new[] { 1, 2, 5, 10, 15, 30 }) steps.Add(m * (decimal)TimeSpan.TicksPerMinute);
        foreach (var h in new[] { 1, 2, 3, 6, 12 }) steps.Add(h * (decimal)TimeSpan.TicksPerHour);
        foreach (var d in new[] { 1, 2, 5, 7, 10, 14, 30, 60, 90, 180, 365 }) steps.Add(d * (decimal)TimeSpan.TicksPerDay);
        var yearTicks = 365m * TimeSpan.TicksPerDay;
        for (var f = 1m; f <= 100_000; f *= 10) { // 2y, 5y, 10y ... beyond the full TimeSpan range
            steps.Add(2 * f * yearTicks); steps.Add(5 * f * yearTicks); steps.Add(10 * f * yearTicks);
        }
        return steps.ToArray();
    }
    static List<decimal> timeSpanBoundaries(decimal min, decimal max, int maxNoRanges, double powerBase) {
        if (powerBase != 1) return powerBoundaries(min, max, maxNoRanges, powerBase, gap => largestStepAtMost(_timeSpanSteps, gap / 2));
        var allowed = maxAllowedRanges(maxNoRanges);
        foreach (var step in _timeSpanSteps) {
            var start = Math.Floor(min / step) * step;
            var end = Math.Ceiling(max / step) * step;
            var count = (end - start) / step;
            if (count > allowed) continue;
            return boundariesOf(start, step, (int)count);
        }
        return new() { min, max }; // e.g. a span straddling zero asked for a single range
    }
    static decimal largestStepAtMost(decimal[] steps, decimal maxStep) {
        for (var i = steps.Length - 1; i >= 0; i--) if (steps[i] <= maxStep) return steps[i];
        return steps[0];
    }

    // ---- DateTime ---------------------------------------------------------------------------------
    // Calendar-aware steps: fixed-length steps up to two weeks (ticks are anchored at 0001-01-01,
    // a Monday, so day steps align to midnight and week steps to Mondays), then month multiples
    // (quarters/half years align to January) and year multiples.
    readonly struct TimeStep {
        public readonly decimal Ticks; // fixed-length step when > 0, otherwise a calendar step
        public readonly int Months;
        public readonly int Years;
        public TimeStep(decimal ticks, int months, int years) { Ticks = ticks; Months = months; Years = years; }
        public decimal ApproxTicks => Ticks > 0 ? Ticks : Months > 0 ? Months * 30.44m * TimeSpan.TicksPerDay : Years * 365.25m * TimeSpan.TicksPerDay;
    }
    static readonly TimeStep[] _dateTimeSteps = buildDateTimeSteps();
    static TimeStep[] buildDateTimeSteps() {
        var steps = new List<TimeStep>();
        var tick = 1m;
        for (var k = 0; k <= 6; k++) { // 100ns up to 0.5s in 1-2-5 steps
            steps.Add(new(tick, 0, 0)); steps.Add(new(2 * tick, 0, 0)); steps.Add(new(5 * tick, 0, 0));
            tick *= 10;
        }
        foreach (var s in new[] { 1, 2, 5, 10, 15, 30 }) steps.Add(new(s * (decimal)TimeSpan.TicksPerSecond, 0, 0));
        foreach (var m in new[] { 1, 2, 5, 10, 15, 30 }) steps.Add(new(m * (decimal)TimeSpan.TicksPerMinute, 0, 0));
        foreach (var h in new[] { 1, 2, 3, 6, 12 }) steps.Add(new(h * (decimal)TimeSpan.TicksPerHour, 0, 0));
        foreach (var d in new[] { 1, 2, 7, 14 }) steps.Add(new(d * (decimal)TimeSpan.TicksPerDay, 0, 0));
        foreach (var m in new[] { 1, 2, 3, 6 }) steps.Add(new(0, m, 0));
        foreach (var y in new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 }) steps.Add(new(0, 0, y));
        return steps.ToArray();
    }
    static List<decimal> dateTimeBoundaries(decimal min, decimal max, int maxNoRanges, double powerBase) {
        if (powerBase != 1) return dateTimePowerBoundaries(min, max, maxNoRanges, powerBase);
        var allowed = maxAllowedRanges(maxNoRanges);
        foreach (var step in _dateTimeSteps) {
            if (tryAlignedDateBoundaries(min, max, step, allowed, out var bounds)) return bounds;
        }
        return new() { min, max }; // unreachable: the year ladder always fits
    }
    static bool tryAlignedDateBoundaries(decimal min, decimal max, TimeStep step, int maxCount, out List<decimal> bounds) {
        bounds = null!;
        if (step.Ticks > 0) {
            var start = Math.Floor(min / step.Ticks) * step.Ticks;
            var end = Math.Ceiling(max / step.Ticks) * step.Ticks;
            var count = (end - start) / step.Ticks;
            if (count > maxCount) return false;
            bounds = boundariesOf(start, step.Ticks, (int)count);
            return true;
        }
        if (step.Months > 0) {
            var from = monthIndexOf(min);
            from -= from % step.Months;
            var to = monthIndexOf(max);
            to -= to % step.Months;
            if (monthStartTicks(to) < max) to += step.Months;
            if ((to - from) / step.Months > maxCount) return false;
            bounds = new();
            for (var m = from; m <= to; m += step.Months) bounds.Add(monthStartTicks(m));
            return true;
        }
        var yFrom = yearOf(min);
        yFrom -= yFrom % step.Years;
        var yTo = yearOf(max);
        yTo -= yTo % step.Years;
        if (yearStartTicks(yTo) < max) yTo += step.Years;
        if ((yTo - yFrom) / step.Years > maxCount) return false;
        bounds = new();
        for (var y = yFrom; y <= yTo; y += step.Years) bounds.Add(yearStartTicks(y));
        return true;
    }
    static List<decimal> dateTimePowerBoundaries(decimal min, decimal max, int n, double powerBase) {
        var delta = (double)(max - min);
        var raw = new decimal[n + 1];
        raw[0] = min;
        raw[n] = max;
        for (var i = 1; i < n; i++) raw[i] = min + (decimal)(delta * Math.Pow((double)i / n, powerBase));
        var bounds = new List<decimal>(n + 1) { snapDate(min, largestDateStepAtMost((raw[1] - raw[0]) / 2), -1) };
        for (var i = 1; i < n; i++) {
            var gap = Math.Min(raw[i] - raw[i - 1], raw[i + 1] - raw[i]);
            bounds.Add(snapDate(raw[i], largestDateStepAtMost(gap / 2), 0));
        }
        bounds.Add(snapDate(max, largestDateStepAtMost((raw[n] - raw[n - 1]) / 2), 1));
        return bounds;
    }
    static TimeStep largestDateStepAtMost(decimal ticks) {
        for (var i = _dateTimeSteps.Length - 1; i >= 0; i--) if (_dateTimeSteps[i].ApproxTicks <= ticks) return _dateTimeSteps[i];
        return _dateTimeSteps[0];
    }
    // snaps a tick value to a calendar boundary of the given step; direction -1 floors, 1 ceils, 0 rounds to nearest
    static decimal snapDate(decimal ticks, TimeStep step, int direction) {
        if (step.Ticks > 0) {
            return direction switch {
                -1 => Math.Floor(ticks / step.Ticks) * step.Ticks,
                1 => Math.Ceiling(ticks / step.Ticks) * step.Ticks,
                _ => Math.Round(ticks / step.Ticks, MidpointRounding.AwayFromZero) * step.Ticks,
            };
        }
        decimal lower, upper;
        if (step.Months > 0) {
            var m = monthIndexOf(ticks);
            m -= m % step.Months;
            lower = monthStartTicks(m);
            upper = monthStartTicks(m + step.Months);
        } else {
            var y = yearOf(ticks);
            y -= y % step.Years;
            lower = yearStartTicks(y);
            upper = yearStartTicks(y + step.Years);
        }
        if (ticks == lower) return lower;
        return direction switch { -1 => lower, 1 => upper, _ => ticks - lower <= upper - ticks ? lower : upper };
    }
    static int yearOf(decimal ticks) => new DateTime((long)ticks).Year;
    static int monthIndexOf(decimal ticks) { var d = new DateTime((long)ticks); return (d.Year - 1) * 12 + d.Month - 1; }
    static decimal monthStartTicks(int monthIndex) {
        if (monthIndex < 0) return 0; // clamp to DateTime.MinValue
        var year = monthIndex / 12 + 1;
        if (year > 9999) return DateTime.MaxValue.Ticks;
        return new DateTime(year, monthIndex % 12 + 1, 1).Ticks;
    }
    static decimal yearStartTicks(int year) {
        if (year < 1) return 0; // clamp to DateTime.MinValue
        if (year > 9999) return DateTime.MaxValue.Ticks;
        return new DateTime(year, 1, 1).Ticks;
    }
}
