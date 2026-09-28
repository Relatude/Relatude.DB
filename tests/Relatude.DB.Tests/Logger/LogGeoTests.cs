using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;

// Positions in a log: the GeoCoordinate column type, the search by distance, and the statistics a
// column of positions keeps.
[TestClass]
public class LogGeoTests {
    const double R = GeoMoments.EarthRadiusMeters;
    static readonly GeoCoordinate oslo = new(59.9139, 10.7522);
    static readonly GeoCoordinate bergen = new(60.3913, 5.3221);
    static readonly DateTime from = H.T0;
    static readonly DateTime to = H.T0.AddHours(3);

    static LogSettings settings(Action<LogSettings>? configure = null, GeoCoordinate? reference = null) => H.Settings("places", s => {
        s.Properties.Add("position", new LogProperty {
            Name = "Position",
            DataType = LogDataType.GeoCoordinate,
            Statistics = [
                new(StatisticsType.Count),
                new(StatisticsType.GeoSpread),
                new(StatisticsType.GeoDistance, reference: reference ?? oslo),
                new(StatisticsType.GeoDistanceBands, reference: reference ?? oslo, bands: [5000, 1000, 20000]), // in no order: they are sorted
                new(StatisticsType.GeoZones, zones: [new("Sentrum", oslo, 2000), new("Bergen", bergen, 10_000)]),
                new(StatisticsType.GeoCoverage, level: 14),
                new(StatisticsType.GeoHeatmap, level: 16),
            ],
        });
        s.Properties.Add("device", new LogProperty { Name = "Device", DataType = LogDataType.String });
        configure?.Invoke(s);
    });
    // a place this many meters east of another, along its latitude
    static GeoCoordinate east(GeoCoordinate of, double meters) =>
        new(of.Latitude, of.Longitude + meters / (R * Math.Cos(of.Latitude * Math.PI / 180)) * 180 / Math.PI);
    static List<LogEntry> all(LogStore store) =>
        [.. store.ExtractLog("places", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), 0, 1000, false, out _)];

    [TestMethod]
    public void PositionsAreKeptAsPositions() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        store.Record("places", H.Entry(from, ("position", oslo), ("other", bergen)));
        store.Record("places", H.Entry(from.AddMinutes(1), ("position", "60.3913, 5.3221")));
        store.Record("places", H.Entry(from.AddMinutes(2), ("position", (63.4305, 10.3951))));
        store.Record("places", H.Entry(from.AddMinutes(3), ("position", new[] { 69.6496, 18.9560 })));
        store.Record("places", H.Entry(from.AddMinutes(4), ("position", "{\"latitude\": 58.97, \"longitude\": 5.73}")));
        store.Record("places", H.Entry(from.AddMinutes(5), ("position", GeoCoordinate.Empty), ("device", "empty")));
        store.Record("places", H.Entry(from.AddMinutes(6), ("position", "95, 10"), ("device", "past the pole")));
        store.Record("places", H.Entry(from.AddMinutes(7), ("position", "not a place"), ("device", "text")));
        store.Record("places", H.Entry(from.AddMinutes(8), ("other", GeoCoordinate.Empty), ("device", "empty, undeclared")));
        store.Dispose(); // the memory provider shows what was appended once the stream is closed

        var again = H.Store(io, settings());
        var entries = all(again);
        Assert.AreEqual(9, entries.Count);
        Assert.AreEqual(oslo, entries[0].Values["position"]);
        Assert.AreEqual(bergen, entries[0].Values["other"]); // undeclared, and still a position
        Assert.AreEqual(bergen, entries[1].Values["position"]);
        Assert.AreEqual(new GeoCoordinate(63.4305, 10.3951), entries[2].Values["position"]);
        Assert.AreEqual(new GeoCoordinate(69.6496, 18.9560), entries[3].Values["position"]);
        Assert.AreEqual(new GeoCoordinate(58.97, 5.73), entries[4].Values["position"]);
        // none of these is a place, and none of them is recorded as one
        for (var i = 5; i <= 8; i++) {
            Assert.IsFalse(entries[i].Values.ContainsKey("position"), "entry " + i);
            Assert.IsFalse(entries[i].Values.ContainsKey("other"), "entry " + i);
        }
        // the count statistic counts the positions, not the entries
        Assert.AreEqual(5, again.AnalyseCombinedCounts("places", "position", IntervalType.Hour, from, to).Value);
        again.Dispose();
    }

    [TestMethod]
    public void ATermCanAskForPositionsNearAPlace() {
        var store = H.Store(new IOProviderMemory(), settings());
        store.Record("places", H.Entry(from, ("position", oslo), ("device", "a")));
        store.Record("places", H.Entry(from.AddMinutes(1), ("position", east(oslo, 1500)), ("device", "b")));
        store.Record("places", H.Entry(from.AddMinutes(2), ("position", bergen), ("device", "c")));
        store.Record("places", H.Entry(from.AddMinutes(3), ("device", "d")));
        int count(string search) {
            store.SearchLog("places", search, from, to, 0, 100, false, out var total);
            return total;
        }
        Assert.AreEqual(2, count("position:59.9139,10.7522~2km"));
        Assert.AreEqual(1, count("position:59.9139,10.7522~1000m"));
        Assert.AreEqual(1, count("position:59.9139,10.7522~500"));
        Assert.AreEqual(1, count("near:60.39,5.32~5km"));
        Assert.AreEqual(3, count("near:60,8~500km"));
        Assert.AreEqual(2, count("-position:59.9139,10.7522~2km")); // Bergen, and the entry with no position
        Assert.AreEqual(2, count("Position:59.9139,10.7522~2KM device:*")); // by the column's name, any case
        // text still finds text: a position reads as "latitude, longitude"
        Assert.AreEqual(1, count("\"60.3913, 5.3221\""));
        store.Dispose();
    }

    [TestMethod]
    public void CentreAndSpreadPerIntervalAndForTheRange() {
        var store = H.Store(new IOProviderMemory(), settings());
        var rnd = new Random(3);
        var expected = new GeoMoments();
        for (var i = 0; i < 400; i++) {
            var place = i < 300 ? oslo : bergen;
            var p = new GeoCoordinate(place.Latitude + (rnd.NextDouble() - 0.5) * 0.02, place.Longitude + (rnd.NextDouble() - 0.5) * 0.04);
            // the first hour in Oslo, the second in Bergen
            store.Record("places", H.Entry(from.AddMinutes(i < 300 ? i * 0.1 : 60 + (i - 300) * 0.1), ("position", p)));
            expected.Add(p);
        }
        var hours = store.AnalyseGeoSpread("places", "position", IntervalType.Hour, from, to, false, true).ToList();
        Assert.AreEqual(3, hours.Count);
        Assert.AreEqual(300, hours[0].Value.Count);
        Assert.IsTrue(hours[0].Value.Center.DistanceTo(oslo) < 500, "hour 1 centred at " + hours[0].Value.Center);
        Assert.IsTrue(hours[1].Value.Center.DistanceTo(bergen) < 500, "hour 2 centred at " + hours[1].Value.Center);
        Assert.IsFalse(hours[2].HasValue);
        var combined = store.AnalyseCombinedGeoSpread("places", "position", IntervalType.Hour, from, to);
        var direct = expected.Summarize();
        Assert.AreEqual(400, combined.Value.Count);
        Assert.AreEqual(direct.StandardDistanceMeters, combined.Value.StandardDistanceMeters, 1e-3);
        Assert.IsTrue(combined.Value.Center.DistanceTo(direct.Center) < 0.05);
        // three quarters of the way to Oslo, roughly: the centre of the whole lies between the two
        var toOslo = combined.Value.Center.DistanceTo(oslo);
        var toBergen = combined.Value.Center.DistanceTo(bergen);
        Assert.IsTrue(toOslo < toBergen);
        store.Dispose();
    }

    [TestMethod]
    public void DistancesBandsAndZones() {
        var store = H.Store(new IOProviderMemory(), settings());
        GeoCoordinate[] places = [oslo, east(oslo, 3000), east(oslo, 12_000), east(oslo, 50_000), bergen];
        for (var i = 0; i < places.Length; i++) store.Record("places", H.Entry(from.AddMinutes(i), ("position", places[i])));

        var distance = store.AnalyseCombinedCountSumAvgMinMax("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoDistance).Value;
        Assert.AreEqual(5, distance.Count);
        Assert.AreEqual(0, distance.Min!.Value, 0.05);
        Assert.AreEqual(oslo.DistanceTo(bergen), distance.Max!.Value, 1);
        Assert.AreEqual(places.Sum(p => p.DistanceTo(oslo)), distance.Sum, 1);

        var bands = store.AnalyseCombinedGroupCounts("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoDistanceBands).Value;
        var labels = StatisticsGeoBands.LabelsOf(settings().Properties["position"].Statistics.First(s => s.StatisticsType == StatisticsType.GeoDistanceBands));
        CollectionAssert.AreEqual(new[] { "under 1 km", "1–5 km", "5–20 km", "over 20 km" }, labels);
        Assert.AreEqual(1, bands["under 1 km"]);
        Assert.AreEqual(1, bands["1–5 km"]);
        Assert.AreEqual(1, bands["5–20 km"]);
        Assert.AreEqual(2, bands["over 20 km"]);

        var zones = store.AnalyseCombinedGroupCounts("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoZones).Value;
        Assert.AreEqual(1, zones["Sentrum"]);
        Assert.AreEqual(1, zones["Bergen"]);
        Assert.AreEqual(3, zones[GeoZone.OutsideName]);
        store.Dispose();
    }

    [TestMethod]
    public void APlainCountPerValueIsStillTheOneAskedForWithoutAName() {
        var s = settings(x => x.Properties["position"].Statistics.Add(new(StatisticsType.UniqueCountWithValues)));
        var store = H.Store(new IOProviderMemory(), s);
        store.Record("places", H.Entry(from, ("position", oslo)));
        store.Record("places", H.Entry(from.AddMinutes(1), ("position", oslo)));
        var plain = store.AnalyseCombinedGroupCounts("places", "position", IntervalType.Hour, from, to).Value;
        Assert.AreEqual(2, plain[oslo.ToString()]); // the position itself, as text
        var zones = store.AnalyseCombinedGroupCounts("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoZones).Value;
        Assert.AreEqual(2, zones["Sentrum"]);
        store.Dispose();
    }

    [TestMethod]
    public void CoverageCountsTheCellsWithAnythingInThem() {
        var store = H.Store(new IOProviderMemory(), settings());
        GeoCoordinate[] spots = [oslo, bergen, new(63.4305, 10.3951)];
        for (var i = 0; i < 900; i++) store.Record("places", H.Entry(from.AddSeconds(i), ("position", spots[i % 3])));
        var hours = store.AnalyseEstimatedUniqueCounts("places", "position", IntervalType.Hour, from, to, false, true, statistic: StatisticsType.GeoCoverage).ToList();
        Assert.AreEqual(3, hours[0].Value);
        store.Dispose();
    }

    [TestMethod]
    public void TheHeatmapKeepsEveryPositionAndSurvivesARestart() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        var rnd = new Random(4);
        var n = 0;
        for (var hour = 0; hour < 3; hour++) {
            for (var i = 0; i < 3000; i++) {
                // mostly the middle of Oslo, some of the rest of the country
                var p = i % 10 == 0
                    ? new GeoCoordinate(58 + rnd.NextDouble() * 12, 5 + rnd.NextDouble() * 20)
                    : new GeoCoordinate(oslo.Latitude + (rnd.NextDouble() - 0.5) * 0.01, oslo.Longitude + (rnd.NextDouble() - 0.5) * 0.02);
                store.Record("places", H.Entry(from.AddHours(hour).AddSeconds(i), ("position", p)));
                n++;
            }
        }
        var hours = store.AnalyseGeoHeatmap("places", "position", IntervalType.Hour, from, to, false, true).ToList();
        CollectionAssert.AreEqual(new long[] { 3000, 3000, 3000 }, hours.Select(h => h.Value.Total).ToArray());
        foreach (var h in hours) Assert.AreEqual(3000, h.Value.Cells.Sum(c => c.Count));
        var combined = store.AnalyseCombinedGeoHeatmap("places", "position", IntervalType.Hour, from, to, 256).Value;
        Assert.AreEqual(n, combined.Total);
        Assert.AreEqual(n, combined.Cells.Sum(c => c.Count));
        Assert.IsTrue(combined.Cells.Count <= 256);
        var densest = combined.Cells.OrderByDescending(c => c.Count / c.Cell.AreaSquareMeters).First();
        Assert.IsTrue(densest.Cell.Center.DistanceTo(oslo) < 2000, "the densest cell is at " + densest.Cell.Center);
        store.SaveStatistics();
        store.Dispose();

        var again = H.Store(io, settings());
        var reread = again.AnalyseCombinedGeoHeatmap("places", "position", IntervalType.Hour, from, to, 256).Value;
        Assert.AreEqual(combined.Total, reread.Total);
        CollectionAssert.AreEqual(combined.Cells.ToArray(), reread.Cells.ToArray());
        var spread = again.AnalyseCombinedGeoSpread("places", "position", IntervalType.Hour, from, to).Value;
        Assert.AreEqual(n, spread.Count);
        again.Dispose();
    }

    [TestMethod]
    public void RebuildingCountsThePositionsAgainFromTheEntries() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        for (var i = 0; i < 50; i++) store.Record("places", H.Entry(from.AddMinutes(i), ("position", east(oslo, i * 100))), forceStatistics: false);
        Assert.AreEqual(0L, store.AnalyseCombinedGeoSpread("places", "position", IntervalType.Hour, from, to).Value.Count);
        store.Dispose();
        store = H.Store(io, settings());
        store.RebuildStatistics("places");
        Assert.AreEqual(50, store.AnalyseCombinedGeoSpread("places", "position", IntervalType.Hour, from, to).Value.Count);
        Assert.AreEqual(50, store.AnalyseCombinedGeoHeatmap("places", "position", IntervalType.Hour, from, to).Value.Total);
        Assert.AreEqual(50, store.AnalyseCombinedCountSumAvgMinMax("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoDistance).Value.Count);
        store.Dispose();
    }

    [TestMethod]
    public void DistancesFromAnotherPointAreAnotherStatistic() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        store.Record("places", H.Entry(from, ("position", east(oslo, 1000))));
        store.SaveStatistics();
        store.Dispose();

        // measured from Bergen now: what was measured from Oslo is not read as if it were
        var moved = H.Store(io, settings(reference: bergen));
        Assert.AreEqual(0, moved.AnalyseCombinedCountSumAvgMinMax("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoDistance).Value.Count);
        // the zones and the heatmap did not change, and are there as they were
        Assert.AreEqual(1, moved.AnalyseCombinedGeoHeatmap("places", "position", IntervalType.Hour, from, to).Value.Total);
        moved.Dispose();

        var back = H.Store(io, settings());
        Assert.AreEqual(1000, back.AnalyseCombinedCountSumAvgMinMax("places", "position", IntervalType.Hour, from, to, StatisticsType.GeoDistance).Value.Max!.Value, 1);
        back.Dispose();
    }

    [TestMethod]
    public void TheParametersRoundTripThroughTheSettingsFile() {
        var s = settings();
        var json = s.ToJson();
        StringAssert.Contains(json, "\"Reference\"");
        StringAssert.Contains(json, "\"latitude\"");
        StringAssert.Contains(json, "\"GeoCoordinate\""); // the data type by name
        var back = LogSettings.FromJson(json);
        var stats = back.Properties["position"].Statistics;
        var bands = stats.Single(x => x.StatisticsType == StatisticsType.GeoDistanceBands);
        Assert.AreEqual(oslo, bands.Reference);
        CollectionAssert.AreEqual(new double[] { 1000, 5000, 20000 }, bands.Bands);
        var zones = stats.Single(x => x.StatisticsType == StatisticsType.GeoZones).Zones!;
        Assert.AreEqual("Bergen", zones[1].Name);
        Assert.AreEqual(bergen, zones[1].Center);
        Assert.AreEqual(10_000, zones[1].RadiusMeters);
        Assert.AreEqual(14, stats.Single(x => x.StatisticsType == StatisticsType.GeoCoverage).Level);
        Assert.AreEqual(back.ToJson(), json);
        // a statistic that has no parameters writes none of them
        var plain = H.Settings("plain", x => x.Properties.Add("n", new LogProperty { DataType = LogDataType.Integer, Statistics = [new(StatisticsType.Count)] })).ToJson();
        foreach (var name in new[] { "Reference", "Bands", "Zones", "Level" }) Assert.IsFalse(plain.Contains("\"" + name + "\""), name);
        // written by hand: a string for the point, the default level
        var hand = LogSettings.FromJson("""
            { "Key": "h", "Properties": { "p": { "DataType": "GeoCoordinate", "Statistics": [
                { "StatisticsType": "GeoDistance", "Reference": "59.9139, 10.7522" },
                { "StatisticsType": "GeoHeatmap" } ] } } }
            """);
        Assert.AreEqual(oslo, hand.Properties["p"].Statistics[0].Reference);
        Assert.AreEqual(StatisticsInfo.DefaultGeoLevel, hand.Properties["p"].Statistics[1].EffectiveLevel);
    }

    [TestMethod]
    public void AStatisticWithoutWhatItMeasuresAgainstIsRefused() {
        void refused(StatisticsInfo stat, string says) {
            var s = H.Settings("bad", x => x.Properties.Add("p", new LogProperty { DataType = LogDataType.GeoCoordinate, Statistics = [stat] }));
            var error = Assert.ThrowsExactly<ArgumentException>(() => s.Validate());
            StringAssert.Contains(error.Message, says);
        }
        refused(new(StatisticsType.GeoDistance), "reference point");
        refused(new(StatisticsType.GeoDistanceBands, reference: oslo), "at least one distance");
        refused(new(StatisticsType.GeoDistanceBands, reference: oslo, bands: [-5, double.NaN]), "at least one distance");
        refused(new(StatisticsType.GeoZones), "at least one zone");
        refused(new(StatisticsType.GeoZones, zones: [new("A", oslo, 10), new("a", bergen, 10)]), "two zones");
        refused(new(StatisticsType.GeoZones, zones: [new("Outside", oslo, 10)]), "no zone can have that name");
        refused(new(StatisticsType.GeoZones, zones: [new("A", oslo, 0)]), "radius");
        refused(new(StatisticsType.GeoZones, zones: [new("A", GeoCoordinate.Empty, 10)]), "centre");
        refused(new(StatisticsType.GeoHeatmap, level: 40), "cell level");
    }

    [TestMethod]
    public void PositionsRecordedAheadOfTheirColumnAreReadAsIt() {
        // what the web request example does to a log defined before it recorded positions: the
        // positions go into the entries under a key the log does not declare, as positions
        var io = new IOProviderMemory();
        var logs = new CustomLogs(io, ["system"]);
        var plain = H.Settings("requests", s => s.Properties.Add("path", new LogProperty { Name = "Path", DataType = LogDataType.String }));
        logs.Create(plain);
        var t = DateTime.UtcNow.AddMinutes(-10);
        logs.Record("requests", new LogEntry { Timestamp = t, Values = { ["path"] = "/", ["Position"] = oslo } });
        logs.Record("requests", new LogEntry { Timestamp = t.AddSeconds(1), Values = { ["path"] = "/", ["Position"] = bergen } });
        logs.FlushToDiskNow();

        var withPosition = plain.Clone();
        withPosition.Properties.Add("position", new LogProperty { Name = "Position", DataType = LogDataType.GeoCoordinate, Statistics = [new(StatisticsType.GeoSpread)] });
        var plan = logs.PlanUpdate(withPosition);
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("recorded with one under its key before it was declared")), string.Join(" | ", plan.Notes));
        var changes = logs.Update(withPosition, rebuildStatistics: true);
        Assert.IsTrue(changes.StatisticsRebuilt);

        var entries = logs.LogStore.ExtractLog("requests", t.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1), 0, 10, false, out _).ToList();
        Assert.AreEqual(oslo, entries[0].Values["position"]); // under the column's own spelling now
        Assert.IsFalse(entries[0].Values.ContainsKey("Position"));
        var spread = logs.LogStore.AnalyseCombinedGeoSpread("requests", "position", IntervalType.Hour, t.AddHours(-1), DateTime.UtcNow.AddHours(1)).Value;
        Assert.AreEqual(2, spread.Count);
        logs.Dispose();
    }

    [TestMethod]
    public void ANewReferencePointIsSaidBeforeItIsMade() {
        var io = new IOProviderMemory();
        var logs = new CustomLogs(io, ["system"]);
        logs.Create(settings());
        logs.Record("places", new LogEntry { Timestamp = DateTime.UtcNow.AddMinutes(-1), Values = { ["position"] = oslo } });
        logs.FlushToDiskNow();
        var plan = logs.PlanUpdate(settings(reference: bergen));
        Assert.IsTrue(plan.Changed);
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("measured from another point")), string.Join(" | ", plan.Notes));
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("with other bands, or from another point")), string.Join(" | ", plan.Notes));
        Assert.IsTrue(plan.CanRebuildStatistics);
        var none = logs.PlanUpdate(settings());
        Assert.IsFalse(none.Changed);
        logs.Dispose();
    }
}
