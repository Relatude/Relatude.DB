namespace WAF.Common;
public static class RangeGenerators {
    public static RangeGenerator<decimal> Decimals = new();
    public static RangeGenerator<float> Floats = new();
    public static RangeGenerator<int> Ints = new();
    public static RangeGenerator<long> Longs = new();
    public static RangeGenerator<double> Doubles = new();
    public static RangeGenerator<byte> Bytes = new();
    public static RangeGenerator<DateTime> DateTimes = new();
    public static RangeGenerator<TimeSpan> TimeSpans = new();
}
public class RangeGenerator<T> where T : IComparable {
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
        throw new NotImplementedException();
    }
    static Dictionary<string, List<Tuple<T, T>>> _rangeCache = new();
    public List<Tuple<T, T>> GetRanges(T value1, T value2, int maxNoRanges, double powerBase, byte precision) {
        var key = typeof(T).Name + "|" + value1 + "|" + value2 + "|" + maxNoRanges + "|" + powerBase + "|" + precision;
        lock (_rangeCache) {
            if (_rangeCache.TryGetValue(key, out var rr)) return rr;
            var d = getDecimalRanges(Convert.ToDecimal(value1), Convert.ToDecimal(value2), maxNoRanges, powerBase, precision);
            var r = new List<Tuple<T, T>>();
            foreach (var t in d) {
                r.Add(new(convertToT(t.Item1), convertToT(t.Item2)));
            }
            _rangeCache.Add(key, r);
            return r;
        }
    }
    static List<Tuple<decimal, decimal>> getDecimalRanges(decimal decimal1, decimal decimal2, int maxNoRanges, double powerBase, byte precision) {
        if (decimal1 < 0) powerBase = 1;
        decimal v1 = decimal1; // to prevent int overflow...
        decimal v2 = decimal2;
        double delta = (double)v2 - (double)v1;
        var ratios = getRangeRatios(maxNoRanges + 1, powerBase);
        var values = new List<decimal>() { v1 };
        foreach (var r in ratios) {
            var dv = (decimal)Math.Ceiling((double)delta * r);
            if (values[values.Count - 1] < (dv + v1)) values.Add(dv + v1);
        }
        values = roundValues(values, precision);
        var ranges = new List<Tuple<decimal, decimal>>();
        for (int i = 0; i < values.Count - 1; i++) {
            var from = values[i];
            var next = values[i + 1];
            var to = next + (next >= 0 ? -1 : 1);
            if (i + 2 == values.Count) to++;
            if (from < decimal.MinValue) from = decimal.MinValue;
            else if (from > decimal.MaxValue) from = decimal.MaxValue;
            if (to < decimal.MinValue) to = decimal.MinValue;
            else if (to > decimal.MaxValue) to = decimal.MaxValue;
            ranges.Add(new((decimal)from, (decimal)to));
        }
        return ranges;
    }
    static double[] getRangeRatios(int rangeCount, double powerBase) {
        var ranges = new double[rangeCount];
        double maxV = Math.Pow(rangeCount - 1, powerBase);
        for (int i = 0; i < rangeCount; i++) {
            ranges[i] = Math.Pow((double)i, powerBase) / maxV;
        }
        return ranges;
    }
    static List<decimal> roundValues(List<decimal> values, byte precision) {
        List<decimal> l2 = new();
        for (int i = 0; i < values.Count; i++) {
            l2.Add(roundOfNumber(values[i], i != 0, precision));
        }
        return l2.Distinct().ToList();
    }
    static decimal roundOfNumber(decimal n, bool ceiling, byte precision) {
        if (n == 0) return 0;
        if (n < 0) {
            if (n == decimal.MinValue) n = decimal.MinValue + 1; // avoids recursive loop...
            return -roundOfNumber(-n, !ceiling, precision);
        }
        var expOf10 = ceiling ? Math.Ceiling(Math.Log10((double)n)) : Math.Ceiling(Math.Log10((double)n));
        var multipler = Math.Pow(10, expOf10);
        var fraction = (double)n / multipler;
        var roundedFraction = (ceiling ? Math.Ceiling(fraction * (double)precision) : Math.Floor(fraction * (double)precision)) / (double)precision;
        var result = roundedFraction * multipler;
        return (decimal)Math.Round(result);
    }
}
