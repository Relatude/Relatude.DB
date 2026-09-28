using Relatude.DB.Common;

namespace Relatude.Common;

[TestClass]
public class GeoMomentsTests {
    const double R = GeoMoments.EarthRadiusMeters;

    // positions round a centre with a known spread: east and north offsets from a correlated normal,
    // placed with the small-angle offsets a local plane has (good to far under a metre at these sizes)
    static List<(double Lat, double Lon)> sample(Random rnd, double lat0, double lon0, double sigmaEast, double sigmaNorth, double rho, int n) {
        var list = new List<(double, double)>(n);
        for (var i = 0; i < n; i++) {
            var a = gaussian(rnd);
            var b = gaussian(rnd);
            var east = sigmaEast * a;
            var north = sigmaNorth * (rho * a + Math.Sqrt(1 - rho * rho) * b);
            var lat = lat0 + north / R * 180 / Math.PI;
            var lon = lon0 + east / (R * Math.Cos(lat0 * Math.PI / 180)) * 180 / Math.PI;
            if (lon >= 180) lon -= 360;
            if (lon < -180) lon += 360;
            list.Add((lat, lon));
        }
        return list;
    }
    static double gaussian(Random rnd) => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());

    // the same numbers worked out the long way: every distance measured to the centre found
    static (double StdDistance, double Major, double Minor, double Bearing) bruteForce(List<(double Lat, double Lon)> points, GeoCoordinate center) {
        double sum = 0, sxx = 0, syy = 0, sxy = 0;
        var clat = center.Latitude;
        var clon = center.Longitude;
        foreach (var (lat, lon) in points) {
            var d = new GeoCoordinate(lat, lon).DistanceTo(center);
            sum += d * d;
            var dLon = lon - clon;
            if (dLon > 180) dLon -= 360;
            if (dLon < -180) dLon += 360;
            var x = dLon * Math.PI / 180 * R * Math.Cos(clat * Math.PI / 180);
            var y = (lat - clat) * Math.PI / 180 * R;
            sxx += x * x;
            syy += y * y;
            sxy += x * y;
        }
        var n = points.Count;
        double a = sxx / n, d2 = syy / n, b = sxy / n;
        var half = (a + d2) / 2;
        var disc = Math.Sqrt((a - d2) * (a - d2) / 4 + b * b);
        var theta = 0.5 * Math.Atan2(2 * b, a - d2);
        var bearing = (90 - theta * 180 / Math.PI) % 180;
        if (bearing < 0) bearing += 180;
        return (Math.Sqrt(sum / n), Math.Sqrt(half + disc), Math.Sqrt(Math.Max(0, half - disc)), bearing);
    }
    static GeoMoments momentsOf(IEnumerable<(double Lat, double Lon)> points) {
        var m = new GeoMoments();
        foreach (var (lat, lon) in points) m.Add(lat, lon);
        return m;
    }

    [TestMethod]
    public void CentreSpreadAndEllipseMatchTheLongWayRound() {
        var points = sample(new Random(7), 59.91, 10.75, 5000, 2000, 0.5, 100_000);
        var s = momentsOf(points).Summarize();
        Assert.AreEqual(100_000, s.Count);
        Assert.AreEqual(59.91, s.Center.Latitude, 0.001);
        Assert.AreEqual(10.75, s.Center.Longitude, 0.001);
        var b = bruteForce(points, s.Center);
        Assert.AreEqual(b.StdDistance, s.StandardDistanceMeters, b.StdDistance * 0.001);
        Assert.AreEqual(b.Major, s.MajorAxisMeters, b.Major * 0.002);
        Assert.AreEqual(b.Minor, s.MinorAxisMeters, b.Minor * 0.002);
        Assert.AreEqual(b.Bearing, s.MajorAxisBearingDegrees, 0.5);
        Assert.IsTrue(s.Concentration > 0.99999 && s.Concentration <= 1);
        Assert.IsTrue(s.South < 59.91 && s.North > 59.91 && s.West < 10.75 && s.East > 10.75);
    }

    [TestMethod]
    public void TheCentreOfAClusterAcrossTheAntimeridianIsOnIt() {
        var points = sample(new Random(8), -17.7, 179.95, 40000, 40000, 0, 50_000);
        var s = momentsOf(points).Summarize();
        Assert.AreEqual(-17.7, s.Center.Latitude, 0.01);
        // an average of the longitudes would put it in Africa
        Assert.IsTrue(Math.Abs(s.Center.Longitude) > 179.8, "centre at " + s.Center);
        var b = bruteForce(points, s.Center);
        Assert.AreEqual(b.StdDistance, s.StandardDistanceMeters, b.StdDistance * 0.002);
        // the box is the narrow one, crossing the antimeridian: west of it is east of the other edge
        Assert.IsTrue(s.West > s.East, $"box {s.West}..{s.East}");
        Assert.IsTrue(s.West > 170 && s.East < -170);
    }

    [TestMethod]
    public void AMetreOfSpreadAmongMillionsOfPositionsIsStillAMetre() {
        // the precision trap of raw sums: a square of one metre, far from the origin of the vectors
        var points = sample(new Random(9), 59.91, 10.75, 1.0, 0.4, 0.6, 1_000_000);
        var s = momentsOf(points).Summarize();
        var b = bruteForce(points, s.Center);
        Assert.AreEqual(b.StdDistance, s.StandardDistanceMeters, 0.05);
        Assert.AreEqual(b.Minor, s.MinorAxisMeters, 0.05);
    }

    [TestMethod]
    public void TheSamePositionOverAndOverHasNoSpread() {
        var m = new GeoMoments();
        for (var i = 0; i < 100_000; i++) m.Add(new GeoCoordinate(63.43, 10.39));
        var s = m.Summarize();
        Assert.AreEqual(0, s.StandardDistanceMeters, 1e-6);
        Assert.AreEqual(63.43, s.Center.Latitude, 1e-7);
        Assert.IsTrue(double.IsNaN(s.MajorAxisBearingDegrees)); // no direction to a spread that is not there
    }

    [TestMethod]
    public void HalvesMergedAreTheWhole() {
        var points = sample(new Random(10), 48.85, 2.35, 3000, 3000, 0, 20_000);
        var whole = momentsOf(points);
        var first = momentsOf(points.Take(7_000));
        var second = momentsOf(points.Skip(7_000));
        var merged = new GeoMoments();
        merged.Add(first);
        merged.Add(second);
        var a = whole.Summarize();
        var c = merged.Summarize();
        Assert.AreEqual(a.Count, c.Count);
        Assert.AreEqual(a.StandardDistanceMeters, c.StandardDistanceMeters, 1e-6);
        Assert.AreEqual(a.MajorAxisMeters, c.MajorAxisMeters, 1e-6);
        Assert.AreEqual(a.Center, c.Center);
        Assert.AreEqual(a.South, c.South);
        Assert.AreEqual(a.East, c.East);
        // merging into an empty one copies, and leaves the original alone
        var copy = new GeoMoments();
        copy.Add(first);
        copy.Add(new GeoCoordinate(0, 0));
        Assert.AreEqual(7_000, first.Count);
    }

    [TestMethod]
    public void BytesRoundTrip() {
        var m = momentsOf(sample(new Random(11), 35.68, 139.69, 8000, 2000, -0.3, 5_000));
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) m.Write(w);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var back = GeoMoments.Read(r);
        Assert.AreEqual(m.Summarize(), back.Summarize());
        var empty = new MemoryStream();
        using (var w = new BinaryWriter(empty, System.Text.Encoding.UTF8, true)) new GeoMoments().Write(w);
        empty.Position = 0;
        Assert.AreEqual(0, GeoMoments.Read(new BinaryReader(empty)).Count);
    }

    [TestMethod]
    public void NothingAndOneAndOppositesOfThePlanet() {
        Assert.AreEqual(GeoSpread.None, new GeoMoments().Summarize());
        var one = new GeoMoments();
        one.Add(GeoCoordinate.Empty); // no position
        one.Add(new GeoCoordinate(10, 20));
        Assert.AreEqual(1, one.Count);
        Assert.AreEqual(0, one.Summarize().StandardDistanceMeters);
        var opposite = new GeoMoments();
        opposite.Add(0, 0);
        opposite.Add(0, 180);
        var s = opposite.Summarize();
        Assert.IsFalse(s.HasCenter); // they cancel out: there is no middle of two antipodes
        Assert.AreEqual(2, s.Count);
    }
}

[TestClass]
public class GeoCellTests {
    [TestMethod]
    public void ACellIsAPrefixOfTheStorageValue() {
        var rnd = new Random(1);
        for (var i = 0; i < 2000; i++) {
            var g = new GeoCoordinate(rnd.NextDouble() * 180 - 90, rnd.NextDouble() * 360 - 180);
            var level = rnd.Next(0, GeoCell.MaxLevel + 1);
            var cell = GeoCell.Of(g, level);
            Assert.AreEqual(level, cell.Level);
            Assert.IsTrue(cell.Contains(g));
            Assert.IsTrue(g.Latitude >= cell.South && g.Latitude <= cell.North, $"{g} not in {cell.South}..{cell.North}");
            Assert.IsTrue(g.Longitude >= cell.West && g.Longitude <= cell.East, $"{g} not in {cell.West}..{cell.East}");
            Assert.IsTrue(g.StorageValue - 1 >= cell.FirstCode && g.StorageValue - 1 <= cell.LastCode);
            if (level > 0) {
                Assert.AreEqual(GeoCell.Of(g, level - 1), cell.Parent);
                Assert.IsTrue(cell.Parent.Contains(cell));
                Assert.IsFalse(cell.Contains(cell.Parent));
            }
            Assert.AreEqual(GeoCell.Of(g, level / 2), cell.AncestorAt(level / 2));
            Assert.AreEqual(cell, GeoCell.FromKey(cell.Key));
            Assert.IsTrue(GeoCell.TryParse(cell.ToString(), out var parsed));
            Assert.AreEqual(cell, parsed);
        }
    }

    [TestMethod]
    public void SizesAndAreas() {
        var oslo = GeoCell.Of(new GeoCoordinate(59.91, 10.75), 16);
        Assert.AreEqual(305, oslo.HeightMeters, 1);
        // at 60 degrees a cell is as wide on the ground as it is tall
        var widthMeters = oslo.WidthDegrees * Math.PI / 180 * GeoMoments.EarthRadiusMeters * Math.Cos(oslo.CenterLatitude * Math.PI / 180);
        Assert.AreEqual(oslo.HeightMeters, widthMeters, oslo.HeightMeters * 0.01);
        // the four children cover the parent
        var parent = oslo.Parent;
        var children = Enumerable.Range(0, 4).Select(q => new GeoCell(parent.Level + 1, (parent.Prefix << 2) | (ulong)q)).ToArray();
        Assert.AreEqual(parent.AreaSquareMeters, children.Sum(c => c.AreaSquareMeters), parent.AreaSquareMeters * 1e-9);
        Assert.IsTrue(children.Contains(oslo));
        // the whole world
        var world = new GeoCell(0, 0);
        Assert.AreEqual(4 * Math.PI * GeoMoments.EarthRadiusMeters * GeoMoments.EarthRadiusMeters, world.AreaSquareMeters, 1e6);
        Assert.AreEqual(16, GeoCell.LevelForMeters(305.5));
        Assert.AreEqual(15, GeoCell.LevelForMeters(611));
        Assert.ThrowsExactly<ArgumentException>(() => GeoCell.Of(GeoCoordinate.Empty, 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GeoCell(2, 16));
    }

    static List<GeoCellCount> cellsOf(IEnumerable<GeoCoordinate> positions, int level) =>
        [.. positions.GroupBy(p => GeoCell.Of(p, level)).Select(g => new GeoCellCount(g.Key, g.Count()))];

    static void assertDisjoint(List<GeoCellCount> cells) {
        for (var i = 1; i < cells.Count; i++) Assert.IsTrue(cells[i].Cell.FirstCode > cells[i - 1].Cell.LastCode, "overlapping cells");
    }

    [TestMethod]
    public void CompressingKeepsEveryCountAndTheBudget() {
        var rnd = new Random(2);
        var positions = new List<GeoCoordinate>();
        // a dense city and a sparse country round it
        for (var i = 0; i < 20_000; i++) positions.Add(new GeoCoordinate(59.91 + gaussianish(rnd) * 0.02, 10.75 + gaussianish(rnd) * 0.04));
        for (var i = 0; i < 2_000; i++) positions.Add(new GeoCoordinate(58 + rnd.NextDouble() * 12, 5 + rnd.NextDouble() * 25));
        var fine = cellsOf(positions, 18);
        Assert.IsTrue(fine.Count > 2000);
        foreach (var budget in new[] { 1, 7, 64, 512 }) {
            var compressed = GeoCellCounts.Compress(fine, budget);
            Assert.IsTrue(compressed.Count <= budget, $"{compressed.Count} cells for a budget of {budget}");
            Assert.AreEqual(positions.Count, compressed.Sum(c => c.Count));
            assertDisjoint(compressed);
            // every position is inside the cell that holds its count
            foreach (var p in positions.Take(500)) Assert.IsTrue(compressed.Any(c => c.Cell.Contains(p)));
        }
        // where it is crowded the cells stay small
        var kept = GeoCellCounts.Compress(fine, 512);
        var city = kept.Where(c => c.Cell.Contains(new GeoCoordinate(59.91, 10.75))).Single();
        var countryside = kept.Where(c => c.Cell.Contains(new GeoCoordinate(68.0, 25.0))).SingleOrDefault();
        Assert.IsTrue(city.Cell.Level >= 14, "the city cell is level " + city.Cell.Level);
        if (countryside.Count > 0) Assert.IsTrue(countryside.Cell.Level < city.Cell.Level);
        // with room for one, it is the smallest cell holding every position
        var one = GeoCellCounts.Compress(fine, 1).Single();
        Assert.IsTrue(positions.All(p => one.Cell.Contains(p)));
        Assert.IsFalse(one.Cell.Children().Any(child => positions.All(p => child.Contains(p))));
    }
    static double gaussianish(Random rnd) => rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5;

    [TestMethod]
    public void ALonelySpotGoesAllTheWayDown() {
        var spot = new GeoCoordinate(69.65, 18.96);
        var cells = GeoCellCounts.Compress([new GeoCellCount(GeoCell.Of(spot, 20), 42)], 4);
        Assert.AreEqual(1, cells.Count);
        Assert.AreEqual(20, cells[0].Cell.Level);
        Assert.AreEqual(42, cells[0].Count);
    }

    [TestMethod]
    public void ACoarseCountIsSharedTheWayTheFineOnesLie() {
        var a = new GeoCoordinate(60.10, 10.10);
        var b = new GeoCoordinate(60.30, 10.60);
        var fineA = GeoCell.Of(a, 16);
        var fineB = GeoCell.Of(b, 16);
        var coarse = GeoCell.Of(a, 8); // covers both
        Assert.IsTrue(coarse.Contains(fineB));
        var cells = GeoCellCounts.Compress([new(fineA, 30), new(fineB, 10), new(coarse, 40)], 100);
        assertDisjoint(cells);
        Assert.AreEqual(80, cells.Sum(c => c.Count));
        // three quarters of the coarse count went where three quarters of the rest were
        Assert.AreEqual(60, cells.Single(c => c.Cell.Contains(a)).Count);
        Assert.AreEqual(20, cells.Single(c => c.Cell.Contains(b)).Count);
        // the same cell given twice is added up, and nothing or less than nothing is left out
        var same = GeoCellCounts.Compress([new(fineA, 1), new(fineA, 2), new(fineB, 0), new(fineB, -5)], 10);
        Assert.AreEqual(1, same.Count);
        Assert.AreEqual(3, same[0].Count);
        Assert.AreEqual(0, GeoCellCounts.Compress([], 10).Count);
    }
}
