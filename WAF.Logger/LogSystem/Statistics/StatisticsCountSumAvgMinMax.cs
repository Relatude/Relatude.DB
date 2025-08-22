using WAF.Hash.xxHash;
using WAF.IO;
using System.Text;
using WAF.LogSystem;
using WAF.LogSystem.Statistics;

namespace WAF.LogSystem.Statistics;

// for example per log entry
public class StatisticsCount : StatisticsBase<int, bool> {
    public StatisticsCount(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        Record(dtUtc, true);
    }
    protected override StatisticsIntervalBase<int, bool> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new IntervalCount(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override Interval<int> Combine(List<Interval<int>> values, DateTime from, DateTime to, IntervalType interval) {
        return new(from, to, values.Where(v => v.HasValue).Sum(v => v.Value));
    }
    public override bool CanCombine => true;
}
class IntervalCount : StatisticsIntervalBase<int, bool> {
    public IntervalCount(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override int CreateAggregator() {
        return 0;
    }
    protected override void Record(Interval<int> interval, bool recordValue) {
        if (recordValue) interval.Value = interval.Value + 1;
    }
    protected override int DeserializeAggregator(byte[] bytes) {
        return BitConverter.ToInt32(bytes);
    }
    protected override byte[] SerializeAggregator(int item) {
        return BitConverter.GetBytes(item);
    }
}

// for example amount on a shopping cart
public class StatisticsDoubleSum : StatisticsBase<double, double> {
    public StatisticsDoubleSum(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is double d) Record(dtUtc, d);
        else if (value is float f) Record(dtUtc, f);
        else if (value is int i) Record(dtUtc, i);
        else if (value is uint ui) Record(dtUtc, ui);
        else if (value is long l) Record(dtUtc, l);
        else if (value is ulong ul) Record(dtUtc, ul);
        else if (value is string s && double.TryParse(s, out var sd)) Record(dtUtc, sd);
    }
    protected override StatisticsIntervalBase<double, double> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new FloatingSum(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override Interval<double> Combine(List<Interval<double>> values, DateTime from, DateTime to, IntervalType interval) {
        return new(from, to, values.Where(v => v.HasValue).Sum(v => v.Value));
    }
    public override bool CanCombine => true;
}
class FloatingSum : StatisticsIntervalBase<double, double> {
    public FloatingSum(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }

    protected override double DeserializeAggregator(byte[] bytes) {
        return BitConverter.ToDouble(bytes);
    }
    protected override byte[] SerializeAggregator(double item) {
        return BitConverter.GetBytes(item);
    }
    protected override double CreateAggregator() {
        return 0;
    }
    protected override void Record(Interval<double> interval, double recordValue) {
        interval.Value = interval.Value + recordValue;
    }
}

// for example number of producs in each cart
public class StatisticsIntegerSum : StatisticsBase<int, int> {
    public StatisticsIntegerSum(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is int i) Record(dtUtc, i);
        else if (value is uint ui) RecordIfPossible(dtUtc, ui);
        else if (value is long l) RecordIfPossible(dtUtc, l);
        else if (value is ulong ul) RecordIfPossible(dtUtc, ul);
        else if (value is double d) RecordIfPossible(dtUtc, d);
        else if (value is float f) RecordIfPossible(dtUtc, f);
        else if (value is string s && int.TryParse(s, out var si)) Record(dtUtc, si);
    }
    protected override StatisticsIntervalBase<int, int> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new IntegerSum(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => true;
    public override Interval<int> Combine(List<Interval<int>> values, DateTime from, DateTime to, IntervalType interval) {
        return new(from, to, values.Where(v => v.HasValue).Sum(v => v.Value));
    }
}
public class IntegerSum : StatisticsIntervalBase<int, int> {
    public IntegerSum(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override int DeserializeAggregator(byte[] bytes) {
        return BitConverter.ToInt32(bytes);
    }
    protected override byte[] SerializeAggregator(int item) {
        return BitConverter.GetBytes(item);
    }
    protected override int CreateAggregator() {
        return 0;
    }
    protected override void Record(Interval<int> interval, int recordValue) {
        interval.Value = interval.Value + recordValue;
    }
}

// for example amount on a shopping cart
public class StatisticsCountSumAvgMinMax : StatisticsBase<AggregatorSumCountAverageMinMax, double> {
    public StatisticsCountSumAvgMinMax(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is double d) Record(dtUtc, d);
        else if (value is float f) Record(dtUtc, f);
        else if (value is int i) Record(dtUtc, i);
        else if (value is uint ui) Record(dtUtc, ui);
        else if (value is long l) Record(dtUtc, l);
        else if (value is ulong ul) Record(dtUtc, ul);
        else if (value is string s && double.TryParse(s, out var sd)) Record(dtUtc, sd);
    }
    protected override StatisticsIntervalBase<AggregatorSumCountAverageMinMax, double> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new CountSumAvgMinMax(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => true;
    public override Interval<AggregatorSumCountAverageMinMax> Combine(List<Interval<AggregatorSumCountAverageMinMax>> values, DateTime from, DateTime to, IntervalType interval) {
        var onlyWithValue = values.Where(v => v.HasValue).ToList();
        if (onlyWithValue.Count == 0) return new(from, to, new());
        return new(from, to, new() {
            Sum = onlyWithValue.Sum(v => v.Value.Sum),
            RecordCount = onlyWithValue.Sum(v => v.Value.RecordCount),
            Min = onlyWithValue.Min(v => v.Value.Min),
            Max = onlyWithValue.Max(v => v.Value.Max),
        });
    }
}
public class AggregatorSumCountAverageMinMax {
    public double Sum;
    public double? Min;
    public double? Max;
    public int RecordCount;
    public double Average => RecordCount == 0 ? 0 : Sum / RecordCount;
}
class CountSumAvgMinMax : StatisticsIntervalBase<AggregatorSumCountAverageMinMax, double> {
    public CountSumAvgMinMax(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override AggregatorSumCountAverageMinMax CreateAggregator() {
        return new();
    }
    protected override void Record(Interval<AggregatorSumCountAverageMinMax> interval, double recordValue) {
        interval.Value.RecordCount += 1;
        interval.Value.Sum += recordValue;
        interval.Value.Min = Math.Min(interval.Value.Min ?? Double.MaxValue, recordValue);
        interval.Value.Max = Math.Max(interval.Value.Max ?? Double.MinValue, recordValue);
    }
    protected override AggregatorSumCountAverageMinMax DeserializeAggregator(byte[] bytes) {
        var res = new AggregatorSumCountAverageMinMax {
            Sum = BitConverter.ToDouble(bytes, 0),
            RecordCount = BitConverter.ToInt32(bytes, 8),
        };

        if (res.RecordCount > 0)
        {
            res.Min = BitConverter.ToDouble(bytes, 12);
            res.Max = BitConverter.ToDouble(bytes, 20);
        }

        return res;
    }
    protected override byte[] SerializeAggregator(AggregatorSumCountAverageMinMax item) {
        var bytes = new byte[28];
        BitConverter.GetBytes(item.Sum).CopyTo(bytes, 0);
        BitConverter.GetBytes(item.RecordCount).CopyTo(bytes, 8);
        if (item.RecordCount > 0)
        {
            BitConverter.GetBytes(item.Min ?? 0).CopyTo(bytes, 12);
            BitConverter.GetBytes(item.Max ?? 0).CopyTo(bytes, 20);
        }
        return bytes;
    }
}

// for example a sensor temperature reading, memory usage, etc
public sealed class StatisticsAvgMinMax : StatisticsBase<AggregatorAvgMinMax, double> {
    public StatisticsAvgMinMax(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is double d) Record(dtUtc, d);
        else if (value is float f) Record(dtUtc, f);
        else if (value is int i) Record(dtUtc, i);
        else if (value is uint ui) Record(dtUtc, ui);
        else if (value is long l) Record(dtUtc, l);
        else if (value is ulong ul) Record(dtUtc, ul);
        else if (value is string s && double.TryParse(s, out var sd)) Record(dtUtc, sd);
    }
    protected override StatisticsIntervalBase<AggregatorAvgMinMax, double> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new AvgMinMax(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => true;
    public override Interval<AggregatorAvgMinMax> Combine(List<Interval<AggregatorAvgMinMax>> values, DateTime from, DateTime to, IntervalType interval) {
        var onlyWithValue = values.Where(v => v.HasValue).ToList();
        if (onlyWithValue.Count == 0) return new(from, to, new());
        return new(from, to, new() {
            Min = onlyWithValue.Min(v => v.Value.Min),
            Max = onlyWithValue.Max(v => v.Value.Max),
            _count = onlyWithValue.Sum(v => v.Value._count),
            _sum = onlyWithValue.Sum(v => v.Value._sum),
        });
        
    }
}
public class AggregatorAvgMinMax {
    internal int _count;
    internal double _sum;
    public double? Min;
    public double? Max;
    public double Average => _count == 0 ? 0 : _sum / _count;
    public void Record(double value) {
        _count += 1;
        _sum += value;
        Min = Math.Min(Min ?? Double.MaxValue, value);
        Max = Math.Max(Max ?? Double.MinValue, value);
    }
    internal static AggregatorAvgMinMax DeSerialize(byte[] bytes) {
        var res = new AggregatorAvgMinMax {
            _count = BitConverter.ToInt32(bytes, 16),
            _sum = BitConverter.ToDouble(bytes, 20),
        };

        if (res._count > 0)
        {
            res.Min = BitConverter.ToDouble(bytes, 0);
            res.Max = BitConverter.ToDouble(bytes, 8);
        }
        
        return res;
    }
    internal byte[] Serialize() {
        var bytes = new byte[28];
        if (_count > 0)
        {
            BitConverter.GetBytes(Min ?? 0).CopyTo(bytes, 0);
            BitConverter.GetBytes(Max ?? 0).CopyTo(bytes, 8);
        }
        BitConverter.GetBytes(_count).CopyTo(bytes, 16);
        BitConverter.GetBytes(_sum).CopyTo(bytes, 20);
        return bytes;
    }
}
class AvgMinMax : StatisticsIntervalBase<AggregatorAvgMinMax, double> {
    public AvgMinMax(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override AggregatorAvgMinMax CreateAggregator() {
        return new();
    }
    protected override void Record(Interval<AggregatorAvgMinMax> interval, double recordValue) {
        interval.Value.Record(recordValue);
    }
    protected override AggregatorAvgMinMax DeserializeAggregator(byte[] bytes) {
        return AggregatorAvgMinMax.DeSerialize(bytes);
    }
    protected override byte[] SerializeAggregator(AggregatorAvgMinMax item) {
        return item.Serialize();
    }
}


