using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Querying;

#region geo test datamodel
[Node]
public class Place {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    [GeoCoordinateProperty(Indexed = true)]
    public GeoCoordinate Location { get; set; }
    [GeoCoordinateProperty] // intentionally not indexed: exercises the row-evaluation fallback
    public GeoCoordinate SecondaryLocation { get; set; }
}
#endregion

[TestClass]
public class GeoCoordinateQueryTests {

    static readonly GeoCoordinate Oslo = new(59.9139, 10.7522);
    static readonly GeoCoordinate Auckland = new(-36.8485, 174.7633); // near the antimeridian

    static NodeStore OpenPlaceStore(out List<Place> all, IIOProvider? io = null) {
        var dm = new Datamodel();
        dm.Add<Place>();
        var store = new NodeStore(DataStoreLocal.Open(dm, null, io));
        var rnd = new Random(1234);
        all = new List<Place>();
        for (var i = 0; i < 1500; i++) {
            GeoCoordinate loc;
            var bucket = i % 10;
            if (bucket < 4) { // clustered around Oslo, 0..~50 km
                loc = new GeoCoordinate(Oslo.Latitude + (rnd.NextDouble() - 0.5), Oslo.Longitude + (rnd.NextDouble() - 0.5) * 2);
            } else if (bucket < 6) { // straddling the antimeridian (179.5..180.5 wraps to the -180 side)
                loc = new GeoCoordinate(Auckland.Latitude + (rnd.NextDouble() - 0.5), 179.5 + rnd.NextDouble());
            } else if (bucket < 7) { // polar
                loc = new GeoCoordinate(88 + rnd.NextDouble() * 2, rnd.NextDouble() * 360 - 180);
            } else if (bucket < 9) { // uniform worldwide
                loc = new GeoCoordinate(rnd.NextDouble() * 180 - 90, rnd.NextDouble() * 360 - 180);
            } else { // no location
                loc = GeoCoordinate.Empty;
            }
            all.Add(new Place { Name = "P" + i, Location = loc, SecondaryLocation = loc });
        }
        store.Insert(all);
        return store;
    }

    [TestMethod]
    public void IndexedRadius_MatchesBruteForce() {
        var store = OpenPlaceStore(out var all);
        foreach (var (center, radius) in centersAndRadii()) {
            var expected = all.Count(p => p.Location.IsWithin(center, radius));
            var actual = store.Query<Place>().Where(p => p.Location.IsWithin(center, radius)).Count();
            Assert.AreEqual(expected, actual, $"center {center}, radius {radius}");
        }
        store.Dispose();
    }

    static IEnumerable<(GeoCoordinate center, double radius)> centersAndRadii() {
        yield return (Oslo, 5_000);
        yield return (Oslo, 50_000);
        yield return (Oslo, 500_000);
        yield return (new GeoCoordinate(-36.8, 179.99), 100_000); // antimeridian crossing
        yield return (new GeoCoordinate(89.5, 0), 200_000); // polar cap
        yield return (new GeoCoordinate(0, 0), 1_000); // null island: only real (0,0) matches, empties never
        yield return (Oslo, 0);
        yield return (Oslo, 25_000_000); // everything with a location
    }

    [TestMethod]
    public void NonIndexedRadius_UsesRowEvaluation_AndMatches() {
        var store = OpenPlaceStore(out var all);
        var expected = all.Count(p => p.SecondaryLocation.IsWithin(Oslo, 50_000));
        var actual = store.Query<Place>().Where(p => p.SecondaryLocation.IsWithin(Oslo, 50_000)).Count();
        Assert.AreEqual(expected, actual);
        store.Dispose();
    }

    [TestMethod]
    public void EmptyLocations_NeverMatchRadius_AndEqualityFindsThem() {
        var store = OpenPlaceStore(out var all);
        var emptyCount = all.Count(p => p.Location.IsEmpty);
        Assert.IsTrue(emptyCount > 0);
        // even a planet-sized radius excludes nodes without a location
        var withLocation = store.Query<Place>().Where(p => p.Location.IsWithin(Oslo, 25_000_000)).Count();
        Assert.AreEqual(all.Count - emptyCount, withLocation);
        // "== Empty" means "has no location" - on the indexed and the non-indexed property alike
        var d = GeoCoordinate.Empty;
        Assert.AreEqual(emptyCount, store.Query<Place>().Where(p => p.Location == d).Count(), "indexed");
        Assert.AreEqual(emptyCount, store.Query<Place>().Where(p => p.SecondaryLocation == d).Count(), "row evaluated");
        Assert.AreEqual(all.Count - emptyCount, store.Query<Place>().Where(p => p.Location != d).Count(), "indexed not-equal");
        store.Dispose();
    }

    [TestMethod]
    public void Equality_MatchesExactCell() {
        var store = OpenPlaceStore(out var all);
        var target = all.First(p => !p.Location.IsEmpty).Location;
        var expected = all.Count(p => p.Location == target);
        Assert.AreEqual(expected, store.Query<Place>().Where(p => p.Location == target).Count());
        store.Dispose();
    }

    [TestMethod]
    public void OrderByDistance_SortsAscending_EmptiesLast() {
        var store = OpenPlaceStore(out var all);
        var result = store.Query<Place>().OrderBy(p => p.Location.DistanceTo(Oslo)).Execute().ToList();
        Assert.AreEqual(all.Count, result.Count);
        double last = -1;
        foreach (var p in result) {
            var dist = p.Location.DistanceTo(Oslo);
            Assert.IsTrue(dist >= last, "distances must be non-decreasing");
            last = dist;
        }
        Assert.IsTrue(result[^1].Location.IsEmpty, "empty locations (infinite distance) must sort last");
        store.Dispose();
    }

    [TestMethod]
    public void OrderByLocation_Throws_WithGuidance() {
        var store = OpenPlaceStore(out _);
        var ex = Assert.ThrowsException<Exception>(() => store.Query<Place>().OrderBy(p => p.Location).Execute());
        StringAssert.Contains(ex.Message, "DistanceTo");
        store.Dispose();
    }

    [TestMethod]
    public void CombinedFilters_AndInlineConstructor() {
        var store = OpenPlaceStore(out var all);
        var expected = all.Count(p => p.Location.IsWithin(Oslo, 100_000) && p.Name.Length > 0);
        var actual = store.Query<Place>()
            .Where(p => p.Location.IsWithin(new GeoCoordinate(59.9139, 10.7522), 100_000) && p.Name != "")
            .Count();
        Assert.AreEqual(expected, actual, "inline new GeoCoordinate(...) must fold into a parameter");
        store.Dispose();
    }

    [TestMethod]
    public void StringQuery_WithCoordinateLiteral() {
        var store = OpenPlaceStore(out var all);
        var expected = all.Count(p => p.Location.IsWithin(Oslo, 50_000));
        var actual = store.Query<Place>().Where("p => p.Location.IsWithin(\"59.9139, 10.7522\", 50000)").Count();
        Assert.AreEqual(expected, actual);
        store.Dispose();
    }

    [TestMethod]
    public void PersistedNativeIndex_RadiusAndEmptyQueries_MatchBruteForce() {
        // same checks as the memory-index tests, but against the persisted NativeKv value index
        // (exercises the GeoCoordinate KeyCodec and B-tree range scans)
        var io = new IOProviderMemory();
        var dm = new Datamodel();
        dm.Add<Place>();
        var settings = new SettingsLocal {
        };
        var store = new NodeStore(DataStoreLocal.Open(dm, settings, io));
        var all = new List<Place>();
        var rnd = new Random(99);
        for (var i = 0; i < 500; i++) {
            var loc = i % 5 == 0 ? GeoCoordinate.Empty
                : new GeoCoordinate(Oslo.Latitude + (rnd.NextDouble() - 0.5), Oslo.Longitude + (rnd.NextDouble() - 0.5) * 2);
            all.Add(new Place { Name = "P" + i, Location = loc, SecondaryLocation = loc });
        }
        store.Insert(all);
        foreach (var (center, radius) in centersAndRadii()) {
            var expected = all.Count(p => p.Location.IsWithin(center, radius));
            Assert.AreEqual(expected, store.Query<Place>().Where(p => p.Location.IsWithin(center, radius)).Count(), $"center {center}, radius {radius}");
        }
        var d = GeoCoordinate.Empty;
        Assert.AreEqual(all.Count(p => p.Location.IsEmpty), store.Query<Place>().Where(p => p.Location == d).Count());
        store.Dispose();

        store = new NodeStore(DataStoreLocal.Open(dm, settings, io)); // reopen against the persisted index state
        var expectedAfter = all.Count(p => p.Location.IsWithin(Oslo, 50_000));
        Assert.AreEqual(expectedAfter, store.Query<Place>().Where(p => p.Location.IsWithin(Oslo, 50_000)).Count());
        store.Dispose();
    }

    [TestMethod]
    public void Persistence_RoundTrip_AndReplayedIndexStillFilters() {
        var io = new IOProviderMemory();
        var store = OpenPlaceStore(out var all, io);
        var expected = all.Count(p => p.Location.IsWithin(Oslo, 50_000));
        Assert.AreEqual(expected, store.Query<Place>().Where(p => p.Location.IsWithin(Oslo, 50_000)).Count());
        store.Maintenance(MaintenanceAction.ClearCache); // node segments re-read from the log
        var byName = store.Query<Place>().Execute().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var expectedByName = all.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < all.Count; i++) {
            Assert.AreEqual(expectedByName[i].Location, byName[i].Location, "coordinates must round-trip the node data codec losslessly");
        }
        store.Dispose();

        var dm = new Datamodel();
        dm.Add<Place>();
        store = new NodeStore(DataStoreLocal.Open(dm, null, io)); // reopen: log replay rebuilds indexes
        Assert.AreEqual(expected, store.Query<Place>().Where(p => p.Location.IsWithin(Oslo, 50_000)).Count());
        var emptyCount = all.Count(p => p.Location.IsEmpty);
        var d = GeoCoordinate.Empty;
        Assert.AreEqual(emptyCount, store.Query<Place>().Where(p => p.Location == d).Count(), "Empty must survive persistence");
        store.Dispose();
    }
}
