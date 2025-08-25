using WAF.IO;
using WAF.Logging;

namespace WAF.Logging.Statistics;

// for example country of a request to record count and name of countries per interval
public class StatisticsGroupCount : StatisticsBase<AggregatorGroupCount, string> {
    public StatisticsGroupCount(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is string s) Record(dtUtc, s);
        else Record(dtUtc, value + string.Empty);
    }
    protected override StatisticsIntervalBase<AggregatorGroupCount, string> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new GroupCount(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => true;
    public override Interval<AggregatorGroupCount> Combine(List<Interval<AggregatorGroupCount>> values, DateTime from, DateTime to, IntervalType interval) {
        var combined = new AggregatorGroupCount();
        foreach (var value in values.Where(v => v.HasValue)) {
            foreach (var kv in value.Value.Values) {
                if (!combined.Values.ContainsKey(kv.Key)) combined.Values[kv.Key] = 0;
                combined.Values[kv.Key] += kv.Value;
            }
        }
        return new(from, to, combined);
    }
}
public class AggregatorGroupCount : ICondensable {
    static int _groupCountLimitCondensed = 50; // only store history for top 50 groups
    static int _groupCountLimitCurrent = 5000; // upper limit to reduce mem usage
    bool _condensed = false;
    public Dictionary<string, int> Values = new();
    public void Record(string group) {
        if (_condensed) throw new Exception("Cannot add values after condense. ");
        if (Values.Count > _groupCountLimitCurrent) return;
        if (!Values.ContainsKey(group)) Values[group] = 0;
        Values[group] += 1;
    }
    public int RecordCount() {
        return Values.Sum(v => v.Value);
    }
    public int UniqueCount() {
        return Values.Count;
    }
    internal static AggregatorGroupCount DeSerialize(byte[] bytes) {
        var ms = new MemoryStream(bytes);
        var br = new BinaryReader(ms);
        var result = new AggregatorGroupCount();
        result._condensed = br.ReadBoolean();
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++) {
            var key = br.ReadString();
            var value = br.ReadInt32();
            result.Values[key] = value;
        }
        return result;
    }
    internal byte[] Serialize() {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(_condensed);
        bw.Write(Values.Count);
        foreach (var kv in Values) {
            bw.Write(kv.Key);
            bw.Write(kv.Value);
        }
        return ms.ToArray();
    }
    public void Condense() {
        _condensed = true;
        if (Values.Count <= _groupCountLimitCondensed) return;
        Values = Values.OrderByDescending(v => v.Value).Take(_groupCountLimitCondensed).ToDictionary(v => v.Key, v => v.Value);

    }
}
class GroupCount : StatisticsIntervalBase<AggregatorGroupCount, string> {
    public GroupCount(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override AggregatorGroupCount CreateAggregator() {
        return new();
    }
    protected override void Record(Interval<AggregatorGroupCount> interval, string recordValue) {
        interval.Value.Record(recordValue);
    }
    protected override AggregatorGroupCount DeserializeAggregator(byte[] bytes) {
        return AggregatorGroupCount.DeSerialize(bytes);
    }
    protected override byte[] SerializeAggregator(AggregatorGroupCount item) {
        return item.Serialize();
    }
}

