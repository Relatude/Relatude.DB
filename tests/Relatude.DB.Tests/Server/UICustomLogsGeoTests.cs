using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// A log of positions in the Logs section, the way the page drives it: defined from the form with
/// the parameters its statistics are measured against, filled with a made-up fleet, drawn as each
/// kind of series, analysed from its entries and recorded into by hand.
/// </summary>
[TestClass]
public class UICustomLogsGeoTests {
    static readonly object oslo = new { latitude = 59.9139, longitude = 10.7522 };

    static object definition() => new {
        key = "fleet",
        name = "Fleet",
        description = "Where the vans are",
        enableLog = true,
        enableStatistics = true,
        enableLogTextFormat = false,
        fileInterval = "Day",
        compressed = false,
        maxAgeOfLogFilesInDays = 30,
        maxTotalSizeOfLogFilesInMb = 100,
        resolutionRowStats = 3,
        firstDayOfWeek = "Monday",
        properties = new object[] {
            new {
                key = "position",
                name = "Position",
                dataType = "GeoCoordinate",
                statistics = new object[] {
                    new { statisticsType = "GeoSpread", resolution = 3 },
                    new { statisticsType = "GeoHeatmap", resolution = 3, level = 15 },
                    new { statisticsType = "GeoCoverage", resolution = 3 },
                    new { statisticsType = "GeoDistance", resolution = 3, reference = oslo },
                    new { statisticsType = "GeoDistanceBands", resolution = 3, reference = oslo, bands = new[] { 50_000.0, 5_000, 200_000 } },
                    new { statisticsType = "GeoZones", resolution = 3, zones = new object[] { new { name = "Oslo", center = oslo, radiusMeters = 30_000 } } },
                },
            },
            new { key = "vehicle", name = "Vehicle", dataType = "String", statistics = new object[] { new { statisticsType = "UniqueCountHashedValues", resolution = 3 } } },
            new { key = "event", name = "Event", dataType = "String", statistics = Array.Empty<object>() },
        },
    };

    [TestMethod]
    public async Task ALogOfPositionsFromTheFormToTheMap() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-geo-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Server.Settings.DefaultStoreId;
            var plan = await command(host, "custom-logs-plan", new { storeId, isNew = true, settings = definition() });
            Assert.IsTrue(prop(plan, "valid").GetBoolean(), plan.ToString());
            await command(host, "custom-logs-save", new { storeId, isNew = true, settings = definition() });

            // the definition comes back with what its statistics are measured against
            var saved = await command(host, "custom-logs-definition", new { storeId, logKey = "fleet" });
            var stats = prop(prop(saved, "settings"), "properties")[0].GetProperty("statistics").EnumerateArray().ToArray();
            var bands = stats.Single(s => prop(s, "statisticsType").GetString() == "GeoDistanceBands");
            Assert.AreEqual(59.9139, prop(prop(bands, "reference"), "latitude").GetDouble(), 1e-6);
            CollectionAssert.AreEqual(new[] { 5_000.0, 50_000, 200_000 }, prop(bands, "bands").EnumerateArray().Select(b => b.GetDouble()).ToArray());
            var zones = stats.Single(s => prop(s, "statisticsType").GetString() == "GeoZones");
            Assert.AreEqual("Oslo", prop(prop(zones, "zones")[0], "name").GetString());
            Assert.AreEqual(15, prop(stats.Single(s => prop(s, "statisticsType").GetString() == "GeoHeatmap"), "level").GetInt32());
            StringAssert.Contains(prop(saved, "json").GetString(), "\"GeoCoordinate\"");

            // The definition goes back to the server the way the page received it - with a null for
            // every parameter a statistic has no use for - which is what editing an existing log does.
            var form = JsonNode.Parse(prop(saved, "settings").GetRawText())!.AsObject();
            var same = await command(host, "custom-logs-plan", new { storeId, isNew = false, settings = form });
            Assert.IsTrue(prop(same, "valid").GetBoolean(), same.ToString());
            Assert.IsFalse(prop(same, "changed").GetBoolean(), "sent back as it came, it is the same definition: " + same);
            var distanceStatistic = form["properties"]![0]!["statistics"]!.AsArray().First(s => s!["statisticsType"]!.GetValue<string>() == "GeoDistance")!;
            distanceStatistic["reference"] = new JsonObject { ["latitude"] = 60.3913, ["longitude"] = 5.3221 };
            var moved = await command(host, "custom-logs-plan", new { storeId, isNew = false, settings = form });
            Assert.IsTrue(prop(moved, "valid").GetBoolean(), moved.ToString());
            Assert.IsTrue(prop(moved, "notes").EnumerateArray().Any(n => n.GetString()!.Contains("measured from another point")), moved.ToString());
            await command(host, "custom-logs-save", new { storeId, isNew = false, settings = form });
            var resaved = await command(host, "custom-logs-definition", new { storeId, logKey = "fleet" });
            var reference = prop(prop(resaved, "settings"), "properties")[0].GetProperty("statistics").EnumerateArray()
                .Single(s => prop(s, "statisticsType").GetString() == "GeoDistance").GetProperty("reference");
            Assert.AreEqual(60.3913, prop(reference, "latitude").GetDouble(), 1e-6);
            // ...and back to Oslo, which the rest of this test measures from
            distanceStatistic["reference"] = JsonNode.Parse(JsonSerializer.Serialize(oslo));
            await command(host, "custom-logs-save", new { storeId, isNew = false, settings = form });

            // a made-up fleet over the last day
            var sample = await command(host, "custom-logs-sample", new { storeId, logKey = "fleet", count = 4000, spanMs = 86_400_000 });
            Assert.AreEqual(4000, prop(sample, "recorded").GetInt32());

            // the info describes a series per statistic, with the unit the distances are in
            var info = await command(host, "custom-logs-info", new { storeId });
            var series = prop(prop(info, "logs")[0], "series").EnumerateArray().ToArray();
            string kindOf(string statistic) => prop(series.Single(s => prop(s, "statistic").GetString() == statistic), "kind").GetString()!;
            Assert.AreEqual("geo", kindOf("GeoSpread"));
            Assert.AreEqual("heatmap", kindOf("GeoHeatmap"));
            Assert.AreEqual("count", kindOf("GeoCoverage"));
            Assert.AreEqual("full", kindOf("GeoDistance"));
            Assert.AreEqual("groups", kindOf("GeoDistanceBands"));
            Assert.AreEqual("groups", kindOf("GeoZones"));
            Assert.AreEqual("meters", prop(series.Single(s => prop(s, "statistic").GetString() == "GeoDistance"), "unit").GetString());

            async Task<JsonElement> seriesOf(string statistic, string interval = "Hour") =>
                await command(host, "custom-logs-series", new { storeId, logKey = "fleet", property = "position", statistic, interval, lastMs = 2 * 86_400_000L });
            var spread = await seriesOf("GeoSpread");
            Assert.AreEqual(4000, prop(prop(spread, "summary"), "count").GetInt32());
            Assert.IsTrue(prop(spread, "points").EnumerateArray().Any(p => prop(p, "hasValue").GetBoolean() && prop(p, "value").GetDouble() > 0));
            var heatmap = await seriesOf("GeoHeatmap", "Day");
            var summary = prop(heatmap, "summary");
            Assert.AreEqual(4000, prop(summary, "total").GetInt64());
            var cells = prop(summary, "cells");
            var cellCount = prop(cells, "count").GetInt32();
            Assert.IsTrue(cellCount > 10 && cellCount <= 4096, cellCount + " cells");
            Assert.AreEqual(cellCount * 8, Convert.FromBase64String(prop(cells, "centres").GetString()!).Length);
            var counts = Convert.FromBase64String(prop(cells, "counts").GetString()!);
            Assert.AreEqual(4000, Enumerable.Range(0, cellCount).Sum(i => BitConverter.ToDouble(counts, i * 8)), 1e-9);
            Assert.IsTrue(prop(summary, "hotspots").GetArrayLength() > 0);
            var distance = await seriesOf("GeoDistance");
            Assert.AreEqual(4000, prop(prop(distance, "summary"), "count").GetInt32());
            var banded = await seriesOf("GeoDistanceBands");
            var groups = prop(banded, "groups").EnumerateArray().Select(g => g.GetString()!).ToArray();
            // nearest first, as declared, whatever the counts are
            var order = new[] { "under 5 km", "5–50 km", "50–200 km", "over 200 km" };
            CollectionAssert.AreEqual(order.Where(groups.Contains).ToArray(), groups);
            var zoned = await seriesOf("GeoZones");
            CollectionAssert.IsSubsetOf(prop(zoned, "groups").EnumerateArray().Select(g => g.GetString()).ToArray(), new[] { "Oslo", "Outside" });
            var coverage = await seriesOf("GeoCoverage");
            Assert.IsTrue(prop(coverage, "points").EnumerateArray().Any(p => prop(p, "hasValue").GetBoolean() && prop(p, "value").GetInt32() > 1));

            // read from the entries: the centre, the distances, the clusters, the tracks and the map
            var analysis = await command(host, "custom-logs-analyse", new { storeId, logKey = "fleet", property = "position", lastMs = 2 * 86_400_000L, trackBy = "vehicle", colorBy = "vehicle" });
            var geo = prop(analysis, "geo");
            Assert.AreEqual(4000, prop(geo, "count").GetInt32());
            Assert.IsTrue(prop(prop(geo, "spread"), "standardDistance").GetDouble() > 0);
            Assert.AreEqual(JsonValueKind.Object, prop(geo, "median").ValueKind);
            Assert.AreEqual(7, prop(prop(geo, "distances"), "percentiles").GetArrayLength());
            var tracks = prop(geo, "tracks");
            Assert.IsTrue(prop(tracks, "tracks").GetInt32() >= 8);
            Assert.IsTrue(prop(prop(tracks, "top")[0], "distance").GetDouble() > 0);
            var points = prop(geo, "points");
            Assert.AreEqual(4000, prop(points, "count").GetInt32());
            Assert.AreEqual(4000 * 8, Convert.FromBase64String(prop(points, "coordinates").GetString()!).Length);
            Assert.AreEqual(4000 * 2, Convert.FromBase64String(prop(prop(points, "colour"), "assignment").GetString()!).Length);

            // recorded by hand: a position as its json, as two numbers, and as text
            await command(host, "custom-logs-record", new { storeId, logKey = "fleet", values = new Dictionary<string, object> { ["position"] = oslo, ["event"] = "json" } });
            await command(host, "custom-logs-record", new { storeId, logKey = "fleet", values = new Dictionary<string, object> { ["position"] = new[] { 60.3913, 5.3221 }, ["event"] = "array" } });
            await command(host, "custom-logs-record", new { storeId, logKey = "fleet", values = new Dictionary<string, object> { ["position"] = "63.4305, 10.3951", ["event"] = "text" } });
            foreach (var how in new[] { "json", "array", "text" }) {
                var page = await command(host, "custom-logs-extract", new { storeId, logKey = "fleet", lastMs = 3_600_000, take = 10, search = "event:" + how });
                var entry = prop(page, "entries").EnumerateArray().Single();
                Assert.AreEqual(JsonValueKind.Object, prop(prop(entry, "values"), "position").ValueKind, how + " was recorded as a position");
            }
            var near = await command(host, "custom-logs-extract", new { storeId, logKey = "fleet", lastMs = 3_600_000, take = 10, search = "position:60.3913,5.3221~100m" });
            var found = prop(near, "entries").EnumerateArray().ToArray();
            Assert.IsTrue(found.Any(e => prop(prop(e, "values"), "event").GetString() == "array"));
            var position = prop(prop(found.First(e => prop(prop(e, "values"), "event").GetString() == "array"), "values"), "position");
            Assert.AreEqual(60.3913, prop(position, "latitude").GetDouble(), 1e-6);
            Assert.AreEqual(5.3221, prop(position, "longitude").GetDouble(), 1e-6);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static TestServerHost start(string root) {
        var host = TestServerHost.Start(root);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }
    static async Task<(int Status, JsonElement Json)> post(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }
    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var (status, json) = await post(host, type, payload);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
}
