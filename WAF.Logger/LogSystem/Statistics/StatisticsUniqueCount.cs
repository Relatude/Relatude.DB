using WAF.Hash.xxHash;
using WAF.IO;
using System.Text;
using WAF.Common;
using WAF.LogSystem;
using WAF.LogSystem.Statistics;

namespace WAF.LogSystem.Statistics;

// for example session ids to count unique sessions per interval, optimized with hash to reduce mem    
public class StatisticsUniqueCount : StatisticsBase<AggregatorSmallUniqueCount, string> {
    public StatisticsUniqueCount(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is string s) Record(dtUtc, s);
        else Record(dtUtc, value + string.Empty);
    }
    protected override StatisticsIntervalBase<AggregatorSmallUniqueCount, string> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new SmallUniqueCount(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => false;
    public override Interval<AggregatorSmallUniqueCount> Combine(List<Interval<AggregatorSmallUniqueCount>> values, DateTime from, DateTime to, IntervalType interval) {
        throw new NotSupportedException();
    }
}
public class AggregatorSmallUniqueCount : ICondensable {
    static int _groupCountLimit = 50000;
    int _condensedValueCount = -1;
    internal HashSet<ulong>? _values;
    public void Record(string group) {
        if (_condensedValueCount > -1) throw new Exception("Cannot add values after condense. ");
        if (_values == null) _values = new();
        if (_values.Count > _groupCountLimit) return;
        var hash = group.XXH64Hash();
        _values.Add(hash);
    }
    public int HashCount() {
        if (_condensedValueCount > -1) return _condensedValueCount;
        if (_values == null) return 0;
        return _values.Count;
    }
    internal static AggregatorSmallUniqueCount DeSerialize(byte[] bytes) {
        var ms = new MemoryStream(bytes);
        var br = new BinaryReader(ms);
        var result = new AggregatorSmallUniqueCount();
        result._condensedValueCount = br.ReadInt32();
        var count = br.ReadInt32();
        if (count > 0) {
            result._values = new();
            for (var i = 0; i < count; i++) {
                var value = br.ReadUInt64();
                result._values.Add(value);
            }
        }
        return result;
    }
    internal byte[] Serialize() {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(_condensedValueCount);
        if (_values == null) {
            bw.Write(0);
        } else {
            bw.Write(_values.Count);
            foreach (var v in _values) {
                bw.Write(v);
            }
        }
        return ms.ToArray();
    }
    public void Condense() {
        _condensedValueCount = HashCount();
        _values = null;
    }
}
public class SmallUniqueCount : StatisticsIntervalBase<AggregatorSmallUniqueCount, string> {
    public SmallUniqueCount(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override AggregatorSmallUniqueCount CreateAggregator() {
        return new();
    }
    protected override void Record(Interval<AggregatorSmallUniqueCount> interval, string recordValue) {
        interval.Value.Record(recordValue);
    }
    protected override AggregatorSmallUniqueCount DeserializeAggregator(byte[] bytes) {
        return AggregatorSmallUniqueCount.DeSerialize(bytes);
    }
    protected override byte[] SerializeAggregator(AggregatorSmallUniqueCount item) {
        return item.Serialize();
    }
    public void Condense() {
        throw new NotImplementedException();
    }
}

