using WAF.IO;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using WAF.LogSystem;
using WAF.LogSystem.Statistics;

namespace WAF.LogSystem.Statistics;
// Statistics is the class that holds the data
// Aggregator calculates and stores the aggregates for each interval
// The Interval holds the aggregator for one interval type
public enum IntervalType {
    Second = 0,
    Minute = 1,
    Hour = 2,
    Day = 3,
    Week = 4,
    Month = 5,
}
public interface ICondensable {
    void Condense();
}
public struct AvgMinMax<T> where T : struct {
    public AvgMinMax(double average, T? min, T? max) {
        Avg = average;
        Min = min;
        Max = max;
    }
    readonly public double Avg { get; }
    readonly public T? Min { get; }
    readonly public T? Max { get; }
}
public struct CountSumAvgMinMax<T> where T : struct {
    public CountSumAvgMinMax(int count, T sum, double average, T? min, T? max) {
        Count = count;
        Sum = sum;
        Avg = average;
        Min = min;
        Max = max;
    }
    readonly public double Avg { get; }
    readonly public int Count { get; }
    readonly public T Sum { get; }
    readonly public T? Min { get; }
    readonly public T? Max { get; }
}
public class Interval<T> {
    public DateTime From { get; }
    public DateTime To { get; } // not really needed to store as it could be calculated, but it is convenient and saves a few CPU cycles
    T _value;
    public T Value {
        get {
            return _value;
        }
        set {
            if (!HasValue) throw new Exception("Aggregator does not accept values. empty interval. ");
            _value = value;
        }
    }
    public bool HasValue { get; private set; }
    public Interval(DateTime from, DateTime to, T value) {
        From = from;
        To = to;
        _value = value;
        HasValue = true;
    }
    public Interval(DateTime from, DateTime to) {
        From = from;
        To = to;
        _value = default!;
        HasValue = false;
    }
    public Interval<K> Map<K>(Func<T, K> convert) {
        if (HasValue) {
            return new Interval<K>(From, To, convert(_value));
        } else {
            return new Interval<K>(From, To);
        }
    }
    internal void CondenseIfPossible() {
        if (HasValue && _value is ICondensable condensable)
            condensable.Condense(); // condense interval
    }
}
public interface IStatistics {
    string Key { get; }
    bool IsDirty { get; }
    void LoadState(IReadStream stream);
    void SaveState(IAppendStream stream);
    void RecordIfPossible(DateTime dtUtc, object value);
    StatisticsInfo Info { get; }
}
// not threadsafe!
public abstract class StatisticsBase<TAggregator, TValue> : IStatistics {
    Dictionary<IntervalType, StatisticsIntervalBase<TAggregator, TValue>> _stats;
    bool _isDirty;
    static int getDefaultNoIntervals(IntervalType i) {
        return i switch {
            IntervalType.Second => 60,
            IntervalType.Minute => 60,
            IntervalType.Hour => 48,
            IntervalType.Day => 60,
            IntervalType.Week => 52,
            IntervalType.Month => 60,
            _ => throw new NotImplementedException(),
        };
    }
    string _key;
    DayOfWeek _firstDayOfWeek;
    public StatisticsBase(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) {
        _stats = new();
        _firstDayOfWeek = firstDayOfWeek;
        _key = key;
        Info = info;
        foreach (var intervalType in Enum.GetValues<IntervalType>()) {
            var maxNoIntervals = getDefaultNoIntervals(intervalType) * info.Resolution;
            _stats.Add(intervalType, CreateStatistics(Info, intervalType, maxNoIntervals, firstDayOfWeek));
        }
    }
    public string Key => _key;
    public StatisticsInfo Info { get; }
    public void LoadState(IReadStream stream) {
        var count = stream.ReadVerifiedInt();
        for (var i = 0; i < count; i++) {
            var intervalType = (IntervalType)stream.ReadOneByte();
            var firstDayOfWeek = (DayOfWeek)stream.ReadOneByte();
            var bytes = stream.ReadByteArray();
            if (_stats.TryGetValue(intervalType, out var stat) && firstDayOfWeek == _firstDayOfWeek) {
                stat.Load(bytes); // only load if interval type and first day of week match
                // this is to avoid loading data from a previous version of the statistics
            }
        }
    }
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_stats.Count);
        foreach (var kv in _stats) {
            stream.WriteOneByte((byte)kv.Key); // interval type
            stream.WriteOneByte((byte)_firstDayOfWeek); // first day of week
            stream.WriteByteArray(kv.Value.Serialize());
        }
        _isDirty = false;
    }
    public bool IsDirty => _isDirty;
    public void Record(DateTime dtUtc, TValue value) {
        _isDirty = true;
        foreach (var stat in _stats) {
            stat.Value.Record(dtUtc, value);
        }
    }
    bool tryGetStatOfSmallerInterval(IntervalType interval, out IntervalType smaller, [MaybeNullWhen(false)] out StatisticsIntervalBase<TAggregator, TValue> stat) {
        smaller = IntervalType.Second;
        stat = null;
        if (interval == IntervalType.Second) return false;
        foreach (var kv in _stats) {
            if (kv.Key < interval) {
                var distanceOfLast = interval - smaller;
                var distanceOfThis = interval - kv.Key;
                if (distanceOfThis < distanceOfLast) {
                    smaller = kv.Key;
                    stat = kv.Value;
                }
            }
        }
        return stat != null;
    }
    public Interval<TAggregator> GetCombinedValue(IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (CanCombine && _stats.TryGetValue(intervalType, out var stat)) {
            var values = stat.GetValues(fromUtc, toUtc, true);
            return Combine(values, fromUtc, toUtc, intervalType);
        }
        return new(fromUtc, toUtc);
    }
    public IEnumerable<Interval<TAggregator>> GetValues(IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated) {
        if (!_stats.TryGetValue(intervalType, out var stat))
            return StatisticsIntervalBase<TAggregator, TValue>.GetEmptyValues(fromUtc, toUtc, intervalType, _firstDayOfWeek);
        if (!estimateNowInterval || !CanCombine) return stat.GetValues(fromUtc, toUtc, fillInBlanks);
        var lastIntervalFloor = IntervalUtils.Floor(toUtc, intervalType, _firstDayOfWeek);
        var lastIntervalCeiling = IntervalUtils.AddOne(lastIntervalFloor, intervalType);
        var now = nowSimulated ?? DateTime.UtcNow;
        var isNowInLastInterval = lastIntervalFloor <= now && now < lastIntervalCeiling;
        if (isNowInLastInterval) {
            // ok, can use lower interval to better estimate current interval:
            if (tryGetStatOfSmallerInterval(intervalType, out var smallerInterval, out var smallerStat)) {
                // estimate this interval by taking the average of the smaller intervals
                var smallerIntervalCeiling = IntervalUtils.Floor(now, smallerInterval, _firstDayOfWeek); // end of latest smaller interval
                var smallerIntervalFloor = IntervalUtils.SubtractOne(smallerIntervalCeiling, intervalType); // start of latest smaller interval
                var smallerValues = smallerStat.GetValues(smallerIntervalFloor, smallerIntervalCeiling, fillInBlanks);
                var averageValues = Combine(smallerValues, lastIntervalFloor, lastIntervalCeiling, intervalType);
                var resultUntilSmaller = stat.GetValues(fromUtc, lastIntervalFloor, fillInBlanks);
                resultUntilSmaller.Add(averageValues);
                return resultUntilSmaller;
            }
        }
        return stat.GetValues(fromUtc, toUtc, fillInBlanks);
    }
    protected abstract StatisticsIntervalBase<TAggregator, TValue> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek);
    public abstract void RecordIfPossible(DateTime dtUtc, object value);
    public abstract bool CanCombine { get; }
    public abstract Interval<TAggregator> Combine(List<Interval<TAggregator>> values, DateTime from, DateTime to, IntervalType interval);
}
public static class IntervalUtils {
    public static DateTime Floor(DateTime d, IntervalType intervalType, DayOfWeek firstDayOfWeek) {
        return intervalType switch {
            IntervalType.Second => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second, DateTimeKind.Utc),
            IntervalType.Minute => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc),
            IntervalType.Hour => new(d.Year, d.Month, d.Day, d.Hour, 0, 0, DateTimeKind.Utc),
            IntervalType.Day => new(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc),
            IntervalType.Week => d.AddDays(-(d.DayOfWeek - firstDayOfWeek + 7) % 7).Date,
            IntervalType.Month => new(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new NotImplementedException(),
        };
    }
    public static DateTime AddOne(DateTime d, IntervalType intervalType) {
        return intervalType switch {
            IntervalType.Second => d.AddSeconds(1),
            IntervalType.Minute => d.AddMinutes(1),
            IntervalType.Hour => d.AddHours(1),
            IntervalType.Day => d.AddDays(1),
            IntervalType.Week => d.AddDays(7),
            IntervalType.Month => d.AddMonths(1),
            _ => throw new NotImplementedException(),
        };
    }
    public static DateTime SubtractOne(DateTime d, IntervalType intervalType) {
        return intervalType switch {
            IntervalType.Second => d.AddSeconds(-1),
            IntervalType.Minute => d.AddMinutes(-1),
            IntervalType.Hour => d.AddHours(-1),
            IntervalType.Day => d.AddDays(-1),
            IntervalType.Week => d.AddDays(-7),
            IntervalType.Month => d.AddMonths(-1),
            _ => throw new NotImplementedException(),
        };
    }
}
public abstract class StatisticsIntervalBase<TAggregator, TValue> {
    public static List<Interval<TAggregator>> GetEmptyValues(DateTime fromUtc, DateTime toUtc, IntervalType intervalType, DayOfWeek firstDayOfWeek) {
        var result = new List<Interval<TAggregator>>();
        var current = IntervalUtils.Floor(fromUtc, intervalType, firstDayOfWeek);
        while (current < toUtc) {
            var to = IntervalUtils.AddOne(current, intervalType);
            result.Add(new Interval<TAggregator>(current, to));
            current = IntervalUtils.AddOne(current, intervalType);
        }
        return result;
    }
    public readonly StatisticsInfo Info;
    public readonly int MaxNoIntervals;
    public readonly IntervalType IntervalType;
    List<Interval<TAggregator>> _intervals;
    DayOfWeek _firstDayOfWeek;
    public StatisticsIntervalBase(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        Info = info;
        IntervalType = intervalType;
        MaxNoIntervals = maxNoIntervals;
        _firstDayOfWeek = firstDayOfWeek;
        _intervals = new();
    }
    public void Load(byte[] bytes) {
        if (bytes.Length == 0) return;
        MemoryStream ms = new(bytes);
        BinaryReader br = new(ms);
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++) {
            var from = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
            var to = IntervalUtils.AddOne(from, IntervalType); // calculated, not stored
            var length = br.ReadInt32();
            var value = DeserializeAggregator(br.ReadBytes(length));
            _intervals.Add(new(from, to, value));
        }
        while (_intervals.Count > MaxNoIntervals) _intervals.RemoveAt(0); // remove expired intervals
    }
    public byte[] Serialize() {
        MemoryStream ms = new();
        BinaryWriter bw = new(ms);
        bw.Write(_intervals.Count);
        for (var n = 0; n < _intervals.Count; n++) {
            var v = _intervals[n];
            var isNotLast = n < _intervals.Count - 1;
            if (isNotLast) v.CondenseIfPossible(); // condense all but last interval
            bw.Write(v.From.Ticks);
            // no need to write v.To, it is calculated on deserialization
            var bytes = SerializeAggregator(v.Value);
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }
        return ms.ToArray();
    }
    public void Record(DateTime dtUtc, TValue recordValue) {
        var last = _intervals.LastOrDefault();
        if (last == null || last.To < dtUtc) { // if none or too old, new interval needed
            if (last != null) last.CondenseIfPossible(); // condense all but last interval
            var agg = CreateAggregator();
            var from = IntervalUtils.Floor(dtUtc, IntervalType, _firstDayOfWeek);
            var to = IntervalUtils.AddOne(from, IntervalType);
            last = new(from, to, agg);
            _intervals.Add(last);
            while (_intervals.Count > MaxNoIntervals) _intervals.RemoveAt(0); // remove expired intervals
        }
        Record(last, recordValue);
    }
    public List<Interval<TAggregator>> GetValues(DateTime fromUtc, DateTime toUtc, bool fillInBlanks) {
        var result = new List<Interval<TAggregator>>();
        var current = IntervalUtils.Floor(fromUtc, IntervalType, _firstDayOfWeek);
        while (current < toUtc) {
            var value = _intervals.FirstOrDefault(v => v.From == current);
            if (value != null) {
                result.Add(value);
            } else if (fillInBlanks) {
                var to = IntervalUtils.AddOne(current, IntervalType);
                result.Add(new Interval<TAggregator>(current, to));
            }
            current = IntervalUtils.AddOne(current, IntervalType);
        }
        return result;
    }
    protected abstract byte[] SerializeAggregator(TAggregator item);
    protected abstract TAggregator DeserializeAggregator(byte[] bytes);
    protected abstract TAggregator CreateAggregator();
    protected abstract void Record(Interval<TAggregator> interval, TValue recordValue);
}
