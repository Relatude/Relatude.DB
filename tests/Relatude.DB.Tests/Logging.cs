using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Tests;
[TestClass]
public class Logging {
    [TestMethod]
    public void DateInterVals() {
        var dt = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(IntervalUtils.Floor(dt, IntervalType.Month, DayOfWeek.Monday), dt);
        Assert.AreEqual(IntervalUtils.AddOne(dt, IntervalType.Month).Subtract(dt).TotalDays, 31d);
    }
    [TestMethod]
    public void HyperLogLog() {
        var loglog = new HyperLogLog();
        var hash = new HashSet<string>();
        var i = 0;
        var r = new Random();
        while (i++ < 100) {
            var v = r.Next(1000).ToString();
            //var v = Guid.NewGuid().ToString();
            loglog.Add(v);
            hash.Add(v);
        }
        var estimated = loglog.EstimateCount();
        var bytes = loglog.Serialize();
        var loglog2 = new HyperLogLog(bytes);
        var estimated2 = loglog2.EstimateCount();
        Assert.AreEqual(estimated, estimated2);
        var exact = hash.Count();
        Assert.IsTrue(Math.Abs((double)(estimated - exact) / (double)exact) < 0.1);
    }
    [TestMethod]
    public void LogStore() {
        LogSettings log = new();
        log.Key = "test";
        log.FileInterval = FileInterval.Day;
        log.EnableLog = true;
        log.EnableStatistics = true;
        log.FirstDayOfWeek = DayOfWeek.Monday;
        log.Compressed = true;
        log.EnableLogTextFormat = true;
        {
            var p = new LogProperty();
            p.DataType = LogDataType.Integer;
            p.Statistics = new() {
                new(StatisticsType.Count),
                new(StatisticsType.Sum),
                new(StatisticsType.AvgMinMax),
            };
            log.Properties.Add("p1", p);
        }
        {
            var p = new LogProperty();
            p.DataType = LogDataType.String;
            p.Statistics = new() {
                new(StatisticsType.Count),
                new(StatisticsType.Sum),
                new(StatisticsType.UniqueCountEstimate),
                new(StatisticsType.UniqueCountHashedValues),
                new(StatisticsType.UniqueCountWithValues)
            };
            log.Properties.Add("p2", p);
        }
        IIOProvider io = new IOProviderMemory();
        var store = new LogStore(io, new[] { log }, new FileKeyUtility(null));
        long chk = 0;
        var noRecs = 10000;
        var rand = new Random();
        // fixed start date: the 10000 records span exactly 11d 14h 9m 4.2s, so the last timestamp lands
        // at minute 9 of an hour. Starting from UtcNow made the test fail whenever the last timestamp
        // landed in the final 10 minutes of an hour: the simulated now (n + 10min) then rolled into the
        // next hour, the last-interval estimation was skipped, and the strict < assert below failed.
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var n = now;
        for (var i = 0; i < noRecs; i++) {
            var e = new LogEntry();
            n = n.AddSeconds(100.13442);
            e.Timestamp = n;
            chk += e.Timestamp.Ticks;
            e.Values.Add("p1", 1);
            //e.Values.Add("p2", rand.Next(1000));
            e.Values.Add("p2", Guid.NewGuid());
            e.Values.Add("p3", "Hello!");
            e.Values.Add("p4", Guid.NewGuid());
            e.Values.Add("p5", Guid.NewGuid());
            store.Record("test", e);
        }
        n = n.AddTicks(1);
        store.Dispose();
        var store2 = new LogStore(io, new[] { log }, new (null));
        var d = store2.ExtractLog("test", now, n, 0, 10000, true, out _).ToList();
        var inv = IntervalType.Hour;
        var now2 = DateTime.UtcNow;

        // testing estimation of last now interval...
        var rowAnalysis2 = store2.AnalyseRows("test", inv, n.AddDays(-1), n, true, true, n.AddMinutes(10));
        var rowAnalysis3 = store2.AnalyseRows("test", inv, n.AddDays(-1), n, false, true, n.AddMinutes(10));
        Assert.IsTrue(rowAnalysis3.Last().Value < rowAnalysis2.Last().Value);

        var rowAnalysis = store2.AnalyseRows("test", inv, now, n, false, true);


        var estimatedCounts = store2.AnalyseEstimatedUniqueCounts("test", "p2", inv, now, n, false, true);
        var hashedCounts = store2.AnalyseUniqueCounts("test", "p2", inv, now, n, false, true);
        var exactCounts = store2.AnalyseGroupCounts("test", "p2", inv, now, n, false, true);

        var sumEstimated = estimatedCounts.Where(v => !v.HasValue).Count();
        var sumHashed = hashedCounts.Where(v => !v.HasValue).Count();
        var sumExact = exactCounts.Where(v => !v.HasValue).Count();

        Assert.AreEqual(sumEstimated, sumHashed);
        Assert.AreEqual(sumEstimated, sumExact);

        var avgEstimated = estimatedCounts.Where(v => v.HasValue).Average(v => v.Value);
        var avgHashed = hashedCounts.Where(v => v.HasValue).Average(v => v.Value);
        var avgExact = exactCounts.Where(v => v.HasValue).Average(v => v.Value.Count());

        Assert.AreEqual(avgHashed, avgExact);
        Assert.IsTrue(Math.Abs(1 - (double)avgEstimated / (double)avgExact) < 0.01);

        long chk2 = 0;
        foreach (var e in d) {
            chk2 += e.Timestamp.Ticks;
            Assert.AreEqual(e.Values["p1"], 1);
        }
        Assert.AreEqual(chk, chk2);
        store2.Dispose();

        var filesBefore = io.GetFiles();

        store2.DeleteLogOlderThan("test", n.AddDays(-2));

        //Assert.IsTrue(io.GetFiles().Count() < filesBefore.Count);

        store.DeleteAll();

        // Assert.IsTrue(io.GetFiles().Count() == 0);

    }
    static LogSettings createSimpleLog(string key) {
        LogSettings log = new() {
            Key = key,
            FileInterval = FileInterval.Day,
            EnableLog = true,
            EnableStatistics = true,
            FirstDayOfWeek = DayOfWeek.Monday,
        };
        log.Properties.Add("p1", new LogProperty {
            DataType = LogDataType.Integer,
            Statistics = new() { new(StatisticsType.Count), new(StatisticsType.Sum) },
        });
        return log;
    }
    [TestMethod]
    public void LogStoreExtractSkipTakeAndFullRange() {
        IIOProvider io = new IOProviderMemory();
        var store = new LogStore(io, new[] { createSimpleLog("test") }, new FileKeyUtility(null));
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 100; i++) {
            var e = new LogEntry { Timestamp = start.AddSeconds(i) };
            e.Values.Add("p1", i);
            store.Record("test", e);
        }
        var page = store.ExtractLog("test", start, start.AddSeconds(100), 10, 5, false, out var total).ToList();
        Assert.AreEqual(100, total);
        Assert.AreEqual(5, page.Count);
        Assert.AreEqual(start.AddSeconds(10), page[0].Timestamp);
        Assert.AreEqual(start.AddSeconds(14), page[4].Timestamp);
        // extreme date ranges must clamp to the existing files and still include everything
        var minDt = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var maxDt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        var all = store.ExtractLog("test", minDt, maxDt, 0, int.MaxValue, false, out var totalAll).ToList();
        Assert.AreEqual(100, totalAll);
        Assert.AreEqual(100, all.Count);
        Assert.AreEqual(start, store.GetTimestampOfFirstRecord("test"));
        Assert.AreEqual(start.AddSeconds(99), store.GetTimestampOfLastRecord("test"));
        store.Dispose();
    }
    [TestMethod]
    public void IntegerSumHandlesAllNumericTypes() {
        var stat = new StatisticsIntegerSum(new StatisticsInfo(StatisticsType.Sum), DayOfWeek.Monday, "k");
        var dt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        stat.RecordIfPossible(dt, 1);
        stat.RecordIfPossible(dt, 2L);
        stat.RecordIfPossible(dt, 3u);
        stat.RecordIfPossible(dt, 4ul);
        stat.RecordIfPossible(dt, 5.9);  // truncates to 5
        stat.RecordIfPossible(dt, 6.2f); // truncates to 6
        stat.RecordIfPossible(dt, "7");
        var v = stat.GetValues(IntervalType.Hour, dt, dt.AddHours(1), false, false, null).Single();
        Assert.AreEqual(1 + 2 + 3 + 4 + 5 + 6 + 7, v.Value);
    }
    [TestMethod]
    public void RecordAtExactIntervalBoundaryGoesToNextInterval() {
        var stat = new StatisticsCount(new StatisticsInfo(StatisticsType.Count), DayOfWeek.Monday, "k");
        var h10 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var h11 = h10.AddHours(1);
        stat.RecordIfPossible(h10.AddMinutes(30), true);
        stat.RecordIfPossible(h11, true); // exactly at the boundary, belongs to the 11:00 interval
        var values = stat.GetValues(IntervalType.Hour, h10, h10.AddHours(2), false, true, null).ToList();
        Assert.AreEqual(2, values.Count);
        Assert.AreEqual(1, values[0].Value);
        Assert.AreEqual(1, values[1].Value);
    }
    [TestMethod]
    public void RebuildStatisticsIncludesAllRecords() {
        IIOProvider io = new IOProviderMemory();
        var store = new LogStore(io, new[] { createSimpleLog("test") }, new FileKeyUtility(null));
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 50; i++) {
            var e = new LogEntry { Timestamp = start.AddSeconds(i) };
            e.Values.Add("p1", 1);
            store.Record("test", e);
        }
        var day = start.Date;
        var before = store.AnalyseCombinedRows("test", IntervalType.Day, day, day.AddDays(1)).Value;
        store.DeleteStatistics("test");
        store.RebuildStatistics("test");
        var after = store.AnalyseCombinedRows("test", IntervalType.Day, day, day.AddDays(1)).Value;
        Assert.AreEqual(50, before);
        Assert.AreEqual(50, after); // record at the exact last timestamp must be included
        store.Dispose();
    }
    [TestMethod]
    public void FileSizesSplitLogAndStatistics() {
        IIOProvider io = new IOProviderMemory();
        var store = new LogStore(io, new[] { createSimpleLog("test") }, new FileKeyUtility(null));
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 100; i++) {
            var e = new LogEntry { Timestamp = start.AddSeconds(i) };
            e.Values.Add("p1", 1);
            store.Record("test", e);
        }
        store.FlushToDiskNow();
        store.SaveStatistics();
        var totalSize = store.GetFileSize("test");
        var logSize = store.GetLogFileSize("test");
        var statSize = store.GetStatisticsFileSize("test");
        Assert.IsTrue(logSize > 0);
        Assert.IsTrue(statSize > 0);
        Assert.AreEqual(totalSize, logSize + statSize);
        store.Dispose();
    }
}
