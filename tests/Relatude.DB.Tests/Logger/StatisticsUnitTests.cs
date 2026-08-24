using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
[TestClass]
public class StatisticsUnitTests {
    [TestMethod]
    public void IntervalUtilsFloorForAllIntervalTypes() {
        var d = new DateTime(2026, 6, 3, 14, 35, 42, 123, DateTimeKind.Utc); // a Wednesday
        Assert.AreEqual(new DateTime(2026, 6, 3, 14, 35, 42, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Second, DayOfWeek.Monday));
        Assert.AreEqual(new DateTime(2026, 6, 3, 14, 35, 0, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Minute, DayOfWeek.Monday));
        Assert.AreEqual(new DateTime(2026, 6, 3, 14, 0, 0, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Hour, DayOfWeek.Monday));
        Assert.AreEqual(new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Day, DayOfWeek.Monday));
        Assert.AreEqual(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Week, DayOfWeek.Monday));
        Assert.AreEqual(new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Week, DayOfWeek.Sunday));
        Assert.AreEqual(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), IntervalUtils.Floor(d, IntervalType.Month, DayOfWeek.Monday));
        Assert.AreEqual(DateTimeKind.Utc, IntervalUtils.Floor(d, IntervalType.Week, DayOfWeek.Monday).Kind);
    }
    [TestMethod]
    public void IntervalUtilsAddOneAndSubtractOneAreInverse() {
        var d = new DateTime(2026, 6, 3, 14, 35, 42, DateTimeKind.Utc);
        foreach (var intervalType in Enum.GetValues<IntervalType>()) {
            Assert.AreEqual(d, IntervalUtils.SubtractOne(IntervalUtils.AddOne(d, intervalType), intervalType), $"round trip failed for {intervalType}");
            Assert.IsTrue(IntervalUtils.AddOne(d, intervalType) > d);
        }
        Assert.AreEqual(new DateTime(2026, 7, 3, 14, 35, 42, DateTimeKind.Utc), IntervalUtils.AddOne(d, IntervalType.Month));
        Assert.AreEqual(new DateTime(2026, 6, 10, 14, 35, 42, DateTimeKind.Utc), IntervalUtils.AddOne(d, IntervalType.Week));
    }
    [TestMethod]
    public void IntervalHoldsValueAndMapsPreservingRange() {
        var from = H.T0;
        var to = H.T0.AddHours(1);
        var interval = new Interval<int>(from, to, 5);
        Assert.IsTrue(interval.HasValue);
        Assert.AreEqual(5, interval.Value);
        interval.Value = 7;
        Assert.AreEqual(7, interval.Value);
        var mapped = interval.Map(v => v.ToString());
        Assert.AreEqual("7", mapped.Value);
        Assert.AreEqual(from, mapped.From);
        Assert.AreEqual(to, mapped.To);
        var empty = new Interval<int>(from, to);
        Assert.IsFalse(empty.HasValue);
        Assert.ThrowsException<Exception>(() => empty.Value = 3);
        Assert.IsFalse(empty.Map(v => v + 1).HasValue);
    }
    [TestMethod]
    public void RecordAtExactIntervalBoundaryGoesToNextInterval() {
        var stat = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Monday, "k");
        var h11 = H.T0.AddHours(1);
        stat.RecordIfPossible(H.T0.AddMinutes(30), true);
        stat.RecordIfPossible(h11, true); // exactly at the boundary, belongs to the 11:00 interval
        var values = stat.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(2), false, true, null).ToList();
        Assert.AreEqual(2, values.Count);
        Assert.AreEqual(1, values[0].Value);
        Assert.AreEqual(1, values[1].Value);
    }
    [TestMethod]
    public void IntegerSumCoercesAllNumericTypes() {
        var stat = new StatisticsIntegerSum(new StatisticsInfo(StatisticsType.Sum), DayOfWeek.Monday, "k");
        stat.RecordIfPossible(H.T0, 1);
        stat.RecordIfPossible(H.T0, 2L);
        stat.RecordIfPossible(H.T0, 3u);
        stat.RecordIfPossible(H.T0, 4ul);
        stat.RecordIfPossible(H.T0, 5.9);  // truncates to 5
        stat.RecordIfPossible(H.T0, 6.2f); // truncates to 6
        stat.RecordIfPossible(H.T0, "7");
        stat.RecordIfPossible(H.T0, "not a number"); // ignored
        stat.RecordIfPossible(H.T0, new object());   // ignored
        var v = stat.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(1), false, false, null).Single();
        Assert.AreEqual(1 + 2 + 3 + 4 + 5 + 6 + 7, v.Value);
    }
    [TestMethod]
    public void DoubleSumCoercesAllNumericTypes() {
        var stat = new StatisticsDoubleSum(new StatisticsInfo(StatisticsType.Sum), DayOfWeek.Monday, "k");
        stat.RecordIfPossible(H.T0, 1.5);
        stat.RecordIfPossible(H.T0, 2);
        stat.RecordIfPossible(H.T0, 3L);
        stat.RecordIfPossible(H.T0, 0.5f);
        stat.RecordIfPossible(H.T0, "4"); // integer-style string parses in any culture
        stat.RecordIfPossible(H.T0, "not a number"); // ignored
        var v = stat.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(1), false, false, null).Single();
        Assert.AreEqual(11.0, v.Value, 1e-9);
    }
    [TestMethod]
    public void AvgMinMaxTracksAverageMinAndMax() {
        var stat = new StatisticsAvgMinMax(new StatisticsInfo(StatisticsType.AvgMinMax), DayOfWeek.Monday, "k");
        stat.RecordIfPossible(H.T0, 1.0);
        stat.RecordIfPossible(H.T0.AddMinutes(1), 5.0);
        stat.RecordIfPossible(H.T0.AddMinutes(2), 3.0);
        var v = stat.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(1), false, false, null).Single().Value;
        Assert.AreEqual(3.0, v.Average, 1e-9);
        Assert.AreEqual(1.0, v.Min!.Value, 1e-9);
        Assert.AreEqual(5.0, v.Max!.Value, 1e-9);
    }
    [TestMethod]
    public void CountIgnoresValueType() {
        var stat = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Monday, "k");
        stat.RecordIfPossible(H.T0, "text");
        stat.RecordIfPossible(H.T0, 42);
        stat.RecordIfPossible(H.T0, new object());
        var v = stat.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(1), false, false, null).Single();
        Assert.AreEqual(3, v.Value);
    }
    [TestMethod]
    public void SaveStateAndLoadStateRoundTrip() {
        var a = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Monday, "k");
        a.RecordIfPossible(H.T0, true);
        a.RecordIfPossible(H.T0.AddHours(1), true);
        a.RecordIfPossible(H.T0.AddHours(1).AddMinutes(1), true);
        Assert.IsTrue(a.IsDirty);
        var io = new IOProviderMemory();
        using (var s = io.OpenAppend(["stat"])) a.SaveState(s);
        Assert.IsFalse(a.IsDirty); // saving clears the dirty flag
        var b = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Monday, "k");
        using (var r = io.OpenRead(["stat"], 0)) b.LoadState(r);
        Assert.IsFalse(b.IsDirty);
        var expected = a.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(2), false, true, null).Select(v => v.Value).ToArray();
        var actual = b.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(2), false, true, null).Select(v => v.Value).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, expected);
        CollectionAssert.AreEqual(expected, actual);
    }
    [TestMethod]
    public void LoadStateIgnoresDataSavedWithDifferentFirstDayOfWeek() {
        var a = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Monday, "k");
        a.RecordIfPossible(H.T0, true);
        var io = new IOProviderMemory();
        using (var s = io.OpenAppend(["stat"])) a.SaveState(s);
        var b = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Sunday, "k");
        using (var r = io.OpenRead(["stat"], 0)) b.LoadState(r);
        Assert.AreEqual(0, b.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(1), false, false, null).Count());
    }
    [TestMethod]
    public void GroupCountAggregatorCondensesToTopGroups() {
        var agg = new AggregatorGroupCount();
        for (var g = 0; g < 60; g++) {
            for (var n = 0; n <= g; n++) agg.Record($"g{g}"); // g0 once ... g59 sixty times
        }
        Assert.AreEqual(60, agg.UniqueCount());
        Assert.AreEqual(60 * 61 / 2, agg.RecordCount());
        agg.Condense();
        Assert.AreEqual(50, agg.UniqueCount()); // only the top 50 groups survive
        Assert.IsTrue(agg.Values.ContainsKey("g59"));
        Assert.IsFalse(agg.Values.ContainsKey("g0"));
        Assert.ThrowsException<Exception>(() => agg.Record("x"));
    }
    [TestMethod]
    public void SmallUniqueCountAggregatorCountsDistinctValues() {
        var agg = new AggregatorSmallUniqueCount();
        for (var i = 0; i < 100; i++) agg.Record($"value{i}");
        for (var i = 0; i < 100; i++) agg.Record($"value{i}"); // repeats do not add
        Assert.AreEqual(100, agg.HashCount());
        agg.Condense();
        Assert.AreEqual(100, agg.HashCount()); // count survives condensing
        Assert.ThrowsException<Exception>(() => agg.Record("x"));
    }
    [TestMethod]
    public void ProbabilisticCountAggregatorEstimatesCardinality() {
        var agg = new AggregatorProbabilisticCount();
        for (var i = 0; i < 1000; i++) agg.Record($"value{i}");
        var estimate = agg.EstimateCount();
        Assert.IsTrue(Math.Abs(estimate - 1000) < 50, $"estimate {estimate} is off by more than 5%");
        agg.Condense();
        Assert.AreEqual(estimate, agg.EstimateCount());
        Assert.ThrowsException<Exception>(() => agg.Record("x"));
    }
    [TestMethod]
    public void UniqueCountStatisticsCannotCombine() {
        var s = new StatisticsUniqueCount(new StatisticsInfo(StatisticsType.UniqueCountHashedValues), DayOfWeek.Monday, "k");
        Assert.IsFalse(s.CanCombine);
        Assert.IsFalse(s.GetCombinedValue(IntervalType.Hour, H.T0, H.T0.AddHours(1)).HasValue);
        Assert.ThrowsException<NotSupportedException>(() => s.Combine(new(), H.T0, H.T0.AddHours(1), IntervalType.Hour));
        var e = new StatisticsEstimatedUniqueCount(new StatisticsInfo(StatisticsType.UniqueCountEstimate), DayOfWeek.Monday, "k");
        Assert.IsFalse(e.CanCombine);
    }
    [TestMethod]
    public void OldIntervalsExpireBeyondMaxCount() {
        // resolution 1 keeps 48 hour-intervals; recording a 50th hour must evict the oldest
        var stat = new StatisticsCount(new StatisticsInfo(StatisticsType.Count, 1), DayOfWeek.Monday, "k");
        for (var h = 0; h < 50; h++) stat.RecordIfPossible(H.T0.AddHours(h), true);
        var all = stat.GetValues(IntervalType.Hour, H.T0, H.T0.AddHours(50), false, false, null).ToList();
        Assert.AreEqual(48, all.Count);
        Assert.AreEqual(H.T0.AddHours(2), all[0].From); // the two oldest hours were evicted
    }
    [TestMethod]
    public void HyperLogLogSerializeRoundTripKeepsEstimate() {
        var hll = new HyperLogLog();
        for (var i = 0; i < 500; i++) hll.Add($"value{i}");
        var estimate = hll.EstimateCount();
        Assert.IsTrue(Math.Abs(estimate - 500) < 25, $"estimate {estimate} is off by more than 5%");
        var restored = new HyperLogLog(hll.Serialize());
        Assert.AreEqual(estimate, restored.EstimateCount());
    }
}
