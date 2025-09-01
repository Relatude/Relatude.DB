using Relatude.DB.IO;
using Relatude.DB.Logging;

namespace Relatude.DB.Logging.Statistics;
public class StatisticsEstimatedUniqueCount : StatisticsBase<AggregatorProbabilisticCount, string> {
    public StatisticsEstimatedUniqueCount(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is string s) Record(dtUtc, s);
        else Record(dtUtc, value + string.Empty);
    }
    protected override StatisticsIntervalBase<AggregatorProbabilisticCount, string> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new ProbabilisticUniqueCount(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => false;
    public override Interval<AggregatorProbabilisticCount> Combine(List<Interval<AggregatorProbabilisticCount>> values, DateTime from, DateTime to, IntervalType interval) {
        throw new NotSupportedException();
    }

}
public class AggregatorProbabilisticCount : ICondensable {
    HyperLogLog? _values;
    int _condensedValueCount = -1;
    public void Record(string group) {
        if (_condensedValueCount > -1) throw new Exception("Cannot add values after condense. ");
        if (_values == null) _values = new();
        _values.Add(group);
    }
    public int EstimateCount() {
        if (_condensedValueCount > -1) return _condensedValueCount;
        if (_values == null) return 0;
        return _values.EstimateCount();
    }
    internal static AggregatorProbabilisticCount DeSerialize(byte[] bytes) {
        var aggregator = new AggregatorProbabilisticCount();
        using var br = new BinaryReader(new MemoryStream(bytes));
        aggregator._condensedValueCount = br.ReadInt32();
        var length = br.ReadInt32();
        if (length > 0) {
            var state = br.ReadBytes(length);
            aggregator._values = new(state);
        } else {
            aggregator._values = null;
        }
        return aggregator;
    }
    internal byte[] Serialize() {
        MemoryStream ms = new();
        using var bw = new BinaryWriter(ms);
        bw.Write(_condensedValueCount);
        if (_values != null) {
            var state = _values.Serialize();
            bw.Write(state.Length);
            bw.Write(state);
        } else {
            bw.Write(0);
        }
        return ms.ToArray();
    }
    public void Condense() {
        _condensedValueCount = EstimateCount();
        _values = null;
    }
}
public class ProbabilisticUniqueCount : StatisticsIntervalBase<AggregatorProbabilisticCount, string> {
    public ProbabilisticUniqueCount(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override AggregatorProbabilisticCount CreateAggregator() {
        return new();
    }
    protected override void Record(Interval<AggregatorProbabilisticCount> interval, string recordValue) {
        interval.Value.Record(recordValue);
    }
    protected override AggregatorProbabilisticCount DeserializeAggregator(byte[] bytes) {
        return AggregatorProbabilisticCount.DeSerialize(bytes);
    }
    protected override byte[] SerializeAggregator(AggregatorProbabilisticCount item) {
        return item.Serialize();
    }
}
