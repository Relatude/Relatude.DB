using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
// All tests use the fixed data set from H.RecordRichHours:
// hour A (10:00) has 4 records, hour B (11:00) has 2 records, see the helper for values.
[TestClass]
public class LogStoreAnalyseTests {
    static readonly DateTime _from = H.T0;
    static readonly DateTime _to = H.T0.AddHours(2);
    static LogStore createRecordedStore(IOProviderMemory io) {
        var store = H.Store(io, H.RichSettings());
        H.RecordRichHours(store);
        return store;
    }
    [TestMethod]
    public void AnalyseRowsPerIntervalAndCombined() {
        var store = createRecordedStore(new());
        var rows = store.AnalyseRows("test", IntervalType.Hour, _from, _to, false, true).ToList();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(4, rows[0].Value);
        Assert.AreEqual(2, rows[1].Value);
        Assert.AreEqual(H.T0, rows[0].From);
        Assert.AreEqual(H.T0.AddHours(1), rows[0].To);
        Assert.AreEqual(6, store.AnalyseCombinedRows("test", IntervalType.Hour, _from, _to).Value);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseCountsPerPropertyAndCombined() {
        var store = createRecordedStore(new());
        var counts = store.AnalyseCounts("test", "pInt", IntervalType.Hour, _from, _to, false, true).ToList();
        CollectionAssert.AreEqual(new[] { 4, 2 }, counts.Select(c => c.Value).ToArray());
        Assert.AreEqual(6, store.AnalyseCombinedCounts("test", "pInt", IntervalType.Hour, _from, _to).Value);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseIntegerSumsAndCombined() {
        var store = createRecordedStore(new());
        var sums = store.AnalyseIntegerSums("test", "pInt", IntervalType.Hour, _from, _to, false, true).ToList();
        CollectionAssert.AreEqual(new[] { 10, 30 }, sums.Select(s => s.Value).ToArray());
        Assert.AreEqual(40, store.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Hour, _from, _to).Value);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseFloatSumsAndCombined() {
        var store = createRecordedStore(new());
        var sums = store.AnalyseFloatSums("test", "pDouble", IntervalType.Hour, _from, _to, false, true).ToList();
        Assert.AreEqual(2, sums.Count);
        Assert.AreEqual(8.0, sums[0].Value, 1e-9);
        Assert.AreEqual(3.0, sums[1].Value, 1e-9);
        Assert.AreEqual(11.0, store.AnalyseCombinedFloatSums("test", "pDouble", IntervalType.Hour, _from, _to).Value, 1e-9);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseAvgMinMaxAndCombined() {
        var store = createRecordedStore(new());
        var values = store.AnalyseAvgMinMax("test", "pDouble", IntervalType.Hour, _from, _to, false, true).ToList();
        Assert.AreEqual(2, values.Count);
        Assert.AreEqual(2.0, values[0].Value.Avg, 1e-9);
        Assert.AreEqual(0.5, values[0].Value.Min!.Value, 1e-9);
        Assert.AreEqual(3.5, values[0].Value.Max!.Value, 1e-9);
        Assert.AreEqual(1.5, values[1].Value.Avg, 1e-9);
        var combined = store.AnalyseCombinedAvgMinMax("test", "pDouble", IntervalType.Hour, _from, _to);
        Assert.AreEqual(11.0 / 6, combined.Value.Avg, 1e-9);
        Assert.AreEqual(0.5, combined.Value.Min!.Value, 1e-9);
        Assert.AreEqual(3.5, combined.Value.Max!.Value, 1e-9);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseCountSumAvgMinMaxAndCombined() {
        var store = createRecordedStore(new());
        var values = store.AnalyseCountSumAvgMinMax("test", "pInt", IntervalType.Hour, _from, _to, false, true).ToList();
        Assert.AreEqual(2, values.Count);
        Assert.AreEqual(4, values[0].Value.Count);
        Assert.AreEqual(10.0, values[0].Value.Sum, 1e-9);
        Assert.AreEqual(2.5, values[0].Value.Avg, 1e-9);
        Assert.AreEqual(1.0, values[0].Value.Min!.Value, 1e-9);
        Assert.AreEqual(4.0, values[0].Value.Max!.Value, 1e-9);
        Assert.AreEqual(2, values[1].Value.Count);
        Assert.AreEqual(30.0, values[1].Value.Sum, 1e-9);
        var combined = store.AnalyseCombinedCountSumAvgMinMax("test", "pInt", IntervalType.Hour, _from, _to);
        Assert.AreEqual(6, combined.Value.Count);
        Assert.AreEqual(40.0, combined.Value.Sum, 1e-9);
        Assert.AreEqual(40.0 / 6, combined.Value.Avg, 1e-9);
        Assert.AreEqual(1.0, combined.Value.Min!.Value, 1e-9);
        Assert.AreEqual(20.0, combined.Value.Max!.Value, 1e-9);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseGroupCountsAndCombined() {
        var store = createRecordedStore(new());
        var values = store.AnalyseGroupCounts("test", "pGroup", IntervalType.Hour, _from, _to, false, true).ToList();
        Assert.AreEqual(2, values.Count);
        var hourA = values[0].Value;
        Assert.AreEqual(3, hourA.Count);
        Assert.AreEqual(2, hourA["a"]);
        Assert.AreEqual(1, hourA["b"]);
        Assert.AreEqual(1, hourA["c"]);
        var combined = store.AnalyseCombinedGroupCounts("test", "pGroup", IntervalType.Hour, _from, _to).Value;
        Assert.AreEqual(4, combined.Count);
        Assert.AreEqual(3, combined["a"]);
        Assert.AreEqual(1, combined["d"]);
        store.Dispose();
    }
    [TestMethod]
    public void AnalyseUniqueCountsExactAndEstimated() {
        var store = createRecordedStore(new());
        var exact = store.AnalyseUniqueCounts("test", "pUnique", IntervalType.Hour, _from, _to, false, true).ToList();
        CollectionAssert.AreEqual(new[] { 3, 1 }, exact.Select(v => v.Value).ToArray());
        var estimated = store.AnalyseEstimatedUniqueCounts("test", "pUnique", IntervalType.Hour, _from, _to, false, true).ToList();
        CollectionAssert.AreEqual(new[] { 3, 1 }, estimated.Select(v => v.Value).ToArray()); // exact for tiny sets
        store.Dispose();
    }
    [TestMethod]
    public void FillInBlanksControlsEmptyIntervals() {
        var store = createRecordedStore(new());
        var to = H.T0.AddHours(3); // hour 12:00 has no data
        var filled = store.AnalyseRows("test", IntervalType.Hour, _from, to, false, true).ToList();
        Assert.AreEqual(3, filled.Count);
        Assert.IsTrue(filled[0].HasValue);
        Assert.IsFalse(filled[2].HasValue);
        var sparse = store.AnalyseRows("test", IntervalType.Hour, _from, to, false, false).ToList();
        Assert.AreEqual(2, sparse.Count);
        Assert.IsTrue(sparse.All(v => v.HasValue));
        store.Dispose();
    }
    [TestMethod]
    public void EstimateNowIntervalProjectsFromSmallerIntervals() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        for (var m = 0; m < 60; m++) store.Record("test", H.Entry(H.T0.AddMinutes(m)));      // hour 10: 60 rows
        for (var m = 0; m < 30; m++) store.Record("test", H.Entry(H.T0.AddMinutes(60 + m))); // hour 11: 30 rows so far
        var nowSim = H.T0.AddMinutes(90); // simulated now = 11:30, halfway into hour 11
        var estimated = store.AnalyseRows("test", IntervalType.Hour, H.T0, nowSim, true, true, nowSim).ToList();
        Assert.AreEqual(2, estimated.Count);
        Assert.AreEqual(60, estimated[0].Value);
        Assert.AreEqual(60, estimated[1].Value); // the trailing 60 minutes (10:30-11:30) projected onto the hour
        var plain = store.AnalyseRows("test", IntervalType.Hour, H.T0, nowSim, false, true, nowSim).ToList();
        Assert.AreEqual(30, plain[1].Value); // without estimation, just the actual count so far
        store.Dispose();
    }
    [TestMethod]
    public void UnknownKeysAndPropertiesReturnEmptyResults() {
        var store = createRecordedStore(new());
        Assert.AreEqual(0, store.AnalyseRows("nope", IntervalType.Hour, _from, _to, false, true).Count());
        Assert.AreEqual(0, store.AnalyseCounts("test", "nope", IntervalType.Hour, _from, _to, false, true).Count());
        Assert.AreEqual(0, store.AnalyseGroupCounts("test", "nope", IntervalType.Hour, _from, _to, false, true).Count());
        Assert.IsFalse(store.AnalyseCombinedRows("nope", IntervalType.Hour, _from, _to).HasValue);
        Assert.IsFalse(store.AnalyseCombinedIntegerSums("test", "nope", IntervalType.Hour, _from, _to).HasValue);
        store.Dispose();
    }
    [TestMethod]
    public void MismatchedStatisticTypeReturnsEmpty() {
        var store = createRecordedStore(new());
        // pDouble has a DoubleSum, not an IntegerSum, and vice versa for pInt
        Assert.AreEqual(0, store.AnalyseIntegerSums("test", "pDouble", IntervalType.Hour, _from, _to, false, true).Count());
        Assert.AreEqual(0, store.AnalyseFloatSums("test", "pInt", IntervalType.Hour, _from, _to, false, true).Count());
        // pGroup only has group counts
        Assert.AreEqual(0, store.AnalyseUniqueCounts("test", "pGroup", IntervalType.Hour, _from, _to, false, true).Count());
        store.Dispose();
    }
    [TestMethod]
    public void StatisticsPersistAcrossRestart() {
        var io = new IOProviderMemory();
        var store = createRecordedStore(io);
        store.SaveStatistics();
        store.Dispose();
        var store2 = H.Store(io, H.RichSettings());
        Assert.AreEqual(6, store2.AnalyseCombinedRows("test", IntervalType.Hour, _from, _to).Value);
        Assert.AreEqual(40, store2.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Hour, _from, _to).Value);
        Assert.AreEqual(11.0, store2.AnalyseCombinedFloatSums("test", "pDouble", IntervalType.Hour, _from, _to).Value, 1e-9);
        var groups = store2.AnalyseCombinedGroupCounts("test", "pGroup", IntervalType.Hour, _from, _to).Value;
        Assert.AreEqual(3, groups["a"]);
        var uniques = store2.AnalyseUniqueCounts("test", "pUnique", IntervalType.Hour, _from, _to, false, true).ToList();
        CollectionAssert.AreEqual(new[] { 3, 1 }, uniques.Select(v => v.Value).ToArray());
        store2.Dispose();
    }
    [TestMethod]
    public void StatisticsPersistViaDisposeWithoutExplicitSave() {
        var io = new IOProviderMemory();
        var store = createRecordedStore(io);
        store.Dispose(); // dispose must save dirty statistics
        var store2 = H.Store(io, H.RichSettings());
        Assert.AreEqual(6, store2.AnalyseCombinedRows("test", IntervalType.Hour, _from, _to).Value);
        store2.Dispose();
    }
    [TestMethod]
    public void RebuildStatisticsRecreatesFromLogIncludingLastRecord() {
        var store = createRecordedStore(new());
        Assert.AreEqual(40, store.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Hour, _from, _to).Value);
        store.DeleteStatistics("test");
        Assert.AreEqual(0, store.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Hour, _from, _to).Value);
        store.RebuildStatistics("test");
        Assert.AreEqual(6, store.AnalyseCombinedRows("test", IntervalType.Hour, _from, _to).Value);
        // sum 40 proves every record was replayed, including the one at the exact last timestamp
        Assert.AreEqual(40, store.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Hour, _from, _to).Value);
        var groups = store.AnalyseCombinedGroupCounts("test", "pGroup", IntervalType.Hour, _from, _to).Value;
        Assert.AreEqual(3, groups["a"]);
        store.Dispose();
    }
}
