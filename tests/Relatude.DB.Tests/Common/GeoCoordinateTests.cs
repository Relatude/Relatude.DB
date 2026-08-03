using Relatude.DB.Common;
using System.Text.Json;

namespace Relatude.Common;

[TestClass]
public class GeoCoordinateTests {

    const double MaxSnapErrorMeters = 0.02; // grid cells are ~9mm x ~19mm, so snap error stays below ~2cm

    [TestMethod]
    public void Ctor_SnapsWithinTolerance_AndIsIdempotent() {
        var rnd = new Random(42);
        for (var i = 0; i < 10_000; i++) {
            var lat = rnd.NextDouble() * 180 - 90;
            var lon = rnd.NextDouble() * 360 - 180;
            var g = new GeoCoordinate(lat, lon);
            Assert.IsFalse(g.IsEmpty);
            Assert.IsTrue(Math.Abs(g.Latitude - lat) < 1e-7, $"lat snap too far: {lat} -> {g.Latitude}");
            Assert.IsTrue(Math.Abs(g.Longitude - lon) < 2e-7, $"lon snap too far: {lon} -> {g.Longitude}");
            var again = new GeoCoordinate(g.Latitude, g.Longitude); // re-constructing from snapped values changes nothing
            Assert.AreEqual(g, again);
            Assert.AreEqual(g.StorageValue, again.StorageValue);
        }
    }

    [TestMethod]
    public void StorageValue_RoundTripsExactly() {
        var rnd = new Random(43);
        for (var i = 0; i < 10_000; i++) {
            var g = new GeoCoordinate(rnd.NextDouble() * 180 - 90, rnd.NextDouble() * 360 - 180);
            var back = GeoCoordinate.FromStorageValue(g.StorageValue);
            Assert.AreEqual(g, back);
            Assert.AreEqual(g.Latitude, back.Latitude);
            Assert.AreEqual(g.Longitude, back.Longitude);
        }
        Assert.AreEqual(GeoCoordinate.Empty, GeoCoordinate.FromStorageValue(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => GeoCoordinate.FromStorageValue(ulong.MaxValue));
    }

    [TestMethod]
    public void EdgeCoordinates_NormalizeAndRoundTrip() {
        // poles clamp, +180 wraps to -180 (same meridian), -180 stays
        Assert.AreEqual(new GeoCoordinate(90, 0), new GeoCoordinate(95, 0));
        Assert.AreEqual(new GeoCoordinate(-90, 0), new GeoCoordinate(-95, 0));
        Assert.AreEqual(new GeoCoordinate(0, -180), new GeoCoordinate(0, 180));
        Assert.AreEqual(new GeoCoordinate(0, -170), new GeoCoordinate(0, 190));
        Assert.AreEqual(new GeoCoordinate(0, 170), new GeoCoordinate(0, -190));
        Assert.ThrowsException<ArgumentException>(() => new GeoCoordinate(double.NaN, 0));
        Assert.ThrowsException<ArgumentException>(() => new GeoCoordinate(0, double.PositiveInfinity));
        // extreme but finite values must not throw or corrupt
        var g = new GeoCoordinate(1e300, -1e300);
        Assert.IsTrue(g.Latitude is >= -90 and <= 90);
        Assert.IsTrue(g.Longitude is >= -180 and < 180);
    }

    [TestMethod]
    public void Empty_Semantics() {
        var empty = GeoCoordinate.Empty;
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsTrue(default(GeoCoordinate).IsEmpty);
        Assert.AreEqual(0ul, empty.StorageValue);
        Assert.IsTrue(double.IsNaN(empty.Latitude));
        Assert.IsTrue(double.IsPositiveInfinity(empty.DistanceTo(new GeoCoordinate(0, 0))));
        Assert.IsTrue(double.IsPositiveInfinity(new GeoCoordinate(0, 0).DistanceTo(empty)));
        Assert.IsFalse(empty.IsWithin(new GeoCoordinate(0, 0), double.MaxValue));
        Assert.AreEqual("", empty.ToString());
        Assert.IsTrue(GeoCoordinate.TryParse("", out var parsed) && parsed.IsEmpty);
        Assert.AreEqual(0, GeoSpatial.CoverRadius(empty, 1000).Count);
    }

    [TestMethod]
    public void ToString_TryParse_RoundTrips() {
        var rnd = new Random(44);
        for (var i = 0; i < 1000; i++) {
            var g = new GeoCoordinate(rnd.NextDouble() * 180 - 90, rnd.NextDouble() * 360 - 180);
            Assert.IsTrue(GeoCoordinate.TryParse(g.ToString(), out var parsed));
            Assert.AreEqual(g, parsed, "ToString/TryParse must land in the same grid cell: " + g);
        }
        Assert.IsFalse(GeoCoordinate.TryParse("59.91", out _));
        Assert.IsFalse(GeoCoordinate.TryParse("a, b", out _));
    }

    [TestMethod]
    public void Json_RoundTrips_IncludingEmpty() {
        var g = new GeoCoordinate(59.9127, 10.7461);
        var json = JsonSerializer.Serialize(g);
        StringAssert.Contains(json, "latitude");
        Assert.AreEqual(g, JsonSerializer.Deserialize<GeoCoordinate>(json));
        Assert.AreEqual("null", JsonSerializer.Serialize(GeoCoordinate.Empty));
        Assert.IsTrue(JsonSerializer.Deserialize<GeoCoordinate>("null").IsEmpty);
        Assert.AreEqual(g, JsonSerializer.Deserialize<GeoCoordinate>("{\"lat\":59.9127,\"lng\":10.7461}")); // aliases
        Assert.AreEqual(g, JsonSerializer.Deserialize<GeoCoordinate>("\"59.9127, 10.7461\"")); // string form
    }

    [TestMethod]
    public void Distance_KnownValues() {
        // Oslo -> Bergen, ~305 km (great-circle, mean-radius sphere)
        var oslo = new GeoCoordinate(59.9139, 10.7522);
        var bergen = new GeoCoordinate(60.3913, 5.3221);
        var d = oslo.DistanceTo(bergen);
        Assert.IsTrue(d > 295_000 && d < 315_000, $"Oslo-Bergen was {d} m");
        // one degree of longitude at the equator ~ 111.19 km
        var d2 = new GeoCoordinate(0, 0).DistanceTo(new GeoCoordinate(0, 1));
        Assert.IsTrue(Math.Abs(d2 - 111_195) < 100, $"1 deg lon at equator was {d2} m");
        // symmetric, and zero to self
        Assert.AreEqual(oslo.DistanceTo(bergen), bergen.DistanceTo(oslo), 1e-6);
        Assert.AreEqual(0, oslo.DistanceTo(oslo));
        // antimeridian shortcut: 179.9W to 179.9E is ~22 km, not ~40000 km
        var d3 = new GeoCoordinate(0, 179.9).DistanceTo(new GeoCoordinate(0, -179.9));
        Assert.IsTrue(d3 < 25_000, $"antimeridian distance was {d3} m");
    }

    [TestMethod]
    public void Ordering_MatchesStorageValue() {
        var rnd = new Random(45);
        var coords = Enumerable.Range(0, 1000).Select(_ => new GeoCoordinate(rnd.NextDouble() * 180 - 90, rnd.NextDouble() * 360 - 180)).ToList();
        var byCompare = coords.OrderBy(c => c).ToList();
        var byCode = coords.OrderBy(c => c.StorageValue).ToList();
        CollectionAssert.AreEqual(byCode, byCompare);
    }

    // The load-bearing test: no coordinate within the radius may escape the cover.
    [TestMethod]
    public void CoverRadius_NeverMissesAPointInRadius() {
        var rnd = new Random(46);
        var configs = new List<(GeoCoordinate center, double radius)>();
        for (var i = 0; i < 60; i++) { // random centers incl. high latitudes, radii from 1 m to 5000 km
            var center = new GeoCoordinate(rnd.NextDouble() * 180 - 90, rnd.NextDouble() * 360 - 180);
            var radius = Math.Pow(10, rnd.NextDouble() * 6.7); // 1 m .. ~5,000 km
            configs.Add((center, radius));
        }
        // targeted edge cases: poles, antimeridian, null island, tiny and huge radii
        configs.Add((new GeoCoordinate(89.99, 45), 5000));
        configs.Add((new GeoCoordinate(-89.99, -45), 5000));
        configs.Add((new GeoCoordinate(90, 0), 1000));
        configs.Add((new GeoCoordinate(0, 179.999), 50_000));
        configs.Add((new GeoCoordinate(0, -179.999), 50_000));
        configs.Add((new GeoCoordinate(0, 0), 0));
        configs.Add((new GeoCoordinate(59.91, 10.75), 25_000_000)); // > half the circumference: everything
        configs.Add((new GeoCoordinate(65, 179.9999), 10));

        foreach (var (center, radius) in configs) {
            var cover = GeoSpatial.CoverRadius(center, radius);
            Assert.IsTrue(cover.Count > 0, "cover must never be empty for a valid center");
            Assert.IsTrue(cover.Count < 200, $"cover unexpectedly large: {cover.Count} ranges");
            for (var i = 1; i < cover.Count; i++)
                Assert.IsTrue(cover[i].From.StorageValue > cover[i - 1].To.StorageValue, "ranges must be ascending and non-overlapping");
            // points sampled inside the radius must always fall in a cover range
            for (var i = 0; i < 400; i++) {
                var p = randomPointNear(center, radius, rnd);
                if (p.DistanceTo(center) > radius) continue; // only test true positives
                Assert.IsTrue(inCover(cover, p), $"point {p} at {p.DistanceTo(center)} m escaped cover(center: {center}, radius: {radius})");
            }
        }
    }

    [TestMethod]
    public void CoverRadius_OverScanIsBounded() {
        // sanity guard against covers that scan the whole planet for city-sized queries: all
        // cover cells decoded back to coordinates must lie within a few radii of the center
        var rnd = new Random(47);
        for (var i = 0; i < 40; i++) {
            var center = new GeoCoordinate(rnd.NextDouble() * 160 - 80, rnd.NextDouble() * 360 - 180);
            var radius = Math.Pow(10, 1 + rnd.NextDouble() * 4); // 10 m .. 100 km
            foreach (var (from, to) in GeoSpatial.CoverRadius(center, radius)) {
                Assert.IsTrue(from.DistanceTo(center) < radius * 8 + 100, $"range start {from} too far from {center} (r={radius})");
            }
        }
    }

    static bool inCover(List<(GeoCoordinate From, GeoCoordinate To)> cover, GeoCoordinate p) {
        foreach (var (from, to) in cover)
            if (p.StorageValue >= from.StorageValue && p.StorageValue <= to.StorageValue) return true;
        return false;
    }

    // samples points at up to ~1.05 * radius from the center (some in, some just outside)
    static GeoCoordinate randomPointNear(GeoCoordinate center, double radius, Random rnd) {
        if (radius == 0) return center;
        var distance = rnd.NextDouble() * radius * 1.05;
        var bearing = rnd.NextDouble() * 2 * Math.PI;
        const double R = 6371000.0;
        var lat1 = center.Latitude * Math.PI / 180;
        var lon1 = center.Longitude * Math.PI / 180;
        var dR = distance / R;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(dR) + Math.Cos(lat1) * Math.Sin(dR) * Math.Cos(bearing));
        var lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(dR) * Math.Cos(lat1), Math.Cos(dR) - Math.Sin(lat1) * Math.Sin(lat2));
        return new GeoCoordinate(lat2 * 180 / Math.PI, lon2 * 180 / Math.PI);
    }
}
