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
}
