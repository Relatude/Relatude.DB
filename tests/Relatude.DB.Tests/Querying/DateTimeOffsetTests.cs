using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Querying;

#region datetimeoffset test datamodel
[Node]
public class Meeting {
    [InternalIdProperty]
    public int Id { get; set; }
    [DateTimeProperty(Indexed = true)]
    public DateTime StartUtc { get; set; }
    [DateTimeOffsetProperty(Indexed = true)]
    public DateTimeOffset Start { get; set; }
}
#endregion

[TestClass]
public class DateTimeOffsetTests {

    static NodeStore OpenMeetingStore(out List<Meeting> all) {
        var dm = new Datamodel();
        dm.Add<Meeting>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        all = new List<Meeting>();
        for (var i = 1; i <= 70; i++) {
            var utc = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i * 11);
            all.Add(new Meeting {
                StartUtc = utc,
                Start = new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(i % 5 - 2)), // varying offsets, same instants
            });
        }
        store.Insert(all);
        return store;
    }

    [TestMethod]
    public void InRange_DateTimeBaseline() {
        var store = OpenMeetingStore(out var all);
        var from = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var count = store.Query<Meeting>().Where(m => m.StartUtc.InRange(from, to)).Count();
        Assert.AreEqual(all.Count(m => m.StartUtc >= from && m.StartUtc <= to), count);
        store.Dispose();
    }

    [TestMethod]
    public void Where_Comparisons_MatchLinq_ForDateTimeAndDateTimeOffset() {
        var store = OpenMeetingStore(out var all);
        var cutoffUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cutoff = new DateTimeOffset(cutoffUtc);
        Assert.AreEqual(all.Count(m => m.StartUtc > cutoffUtc), store.Query<Meeting>().Where(m => m.StartUtc > cutoffUtc).Count(), "DateTime baseline");
        Assert.AreEqual(all.Count(m => m.Start > cutoff), store.Query<Meeting>().Where(m => m.Start > cutoff).Count(), "DateTimeOffset must filter like DateTime");
        store.Dispose();
    }

    [TestMethod]
    public void InRange_DateTimeOffset_MatchesLinq() {
        var store = OpenMeetingStore(out var all);
        var from = new DateTimeOffset(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var to = new DateTimeOffset(new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var count = store.Query<Meeting>().Where(m => m.Start.InRange(from, to)).Count();
        Assert.AreEqual(all.Count(m => m.Start >= from && m.Start <= to), count);
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_DateTimeOffset_UsesLinearCalendarBuckets() {
        // like DateTime: many distinct instants auto-bucket into uniform calendar ranges,
        // aligned on UTC (values keep their offsets; bucketing uses the instant)
        var store = OpenMeetingStore(out var all);
        var facet = FacetOf(store.Query<Meeting>().Facets().AddFacet("Start").Execute(), "Start");
        Assert.IsTrue(facet.IsRangeFacet == true, "70 distinct instants must auto-bucket into ranges");
        Assert.AreEqual(all.Count, facet.Values.Sum(v => v.Count), "Contiguous buckets must cover every value");
        var bounds = facet.Values.Skip(1).Select(v => (DateTimeOffset)v.Value!).ToList(); // interior bucket starts (first bucket starts at the real min)
        Assert.IsTrue(bounds.Count > 1, "Expected several range buckets, got " + facet.Values.Count);
        Assert.IsTrue(bounds.All(b => b.Offset == TimeSpan.Zero && b.UtcDateTime.Day == 1 && b.UtcDateTime.TimeOfDay == TimeSpan.Zero),
            "Interior boundaries must be UTC-aligned month starts");
        var monthIndex = bounds.Select(b => b.UtcDateTime.Year * 12 + b.UtcDateTime.Month).ToList();
        var step = monthIndex[1] - monthIndex[0];
        for (var i = 1; i < monthIndex.Count; i++) Assert.AreEqual(step, monthIndex[i] - monthIndex[i - 1], "Bucket strides must be uniform");
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_DateTimeOffset_BucketSelectionFiltersByInstant() {
        var store = OpenMeetingStore(out var all);
        var first = store.Query<Meeting>().Facets().AddRangeFacet("Start").Execute();
        var bucket = FacetOf(first, "Start").Values[1]; // an interior half-open bucket
        var res = store.Query<Meeting>().Facets()
            .AddRangeFacet("Start")
            .SetFacetRangeValue("Start", bucket.Value!, bucket.Value2!)
            .Execute();
        var from = (DateTimeOffset)bucket.Value!;
        var to = (DateTimeOffset)bucket.Value2!;
        var expected = all.Count(m => (bucket.FromInclusive ? m.Start >= from : m.Start > from) && (bucket.ToInclusive ? m.Start <= to : m.Start < to));
        Assert.IsTrue(expected > 0, "Test bucket must not be empty");
        Assert.AreEqual(expected, res.Count());
        store.Dispose();
    }

    static Facets FacetOf<T>(ResultSetFacets<T> res, string codeName)
        => res.Facets.First(f => f.CodeName == codeName);

    [TestMethod]
    public void NodeData_PersistenceRoundTrip_PreservesInstantAndOffset() {
        // a store WITHOUT an IO provider never serializes node data, so only this test (not the
        // ones above) exercises the ToBytes/FromBytes node-data codec for DateTimeOffset
        var io = new IOProviderMemory();
        var dm = new Datamodel();
        dm.Add<Meeting>();
        var offsets = new[] { 0.0, 5.5, -8, 14, -14, 1 }; // includes the ±14h extremes and a half-hour offset
        var all = offsets.Select((h, i) => new Meeting {
            StartUtc = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i),
            Start = new DateTimeOffset(new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i)).ToOffset(TimeSpan.FromHours(h)),
        }).ToList();

        void verify(NodeStore s) {
            var stored = s.Query<Meeting>().Execute().OrderBy(m => m.StartUtc).ToList();
            Assert.AreEqual(all.Count, stored.Count);
            for (var i = 0; i < all.Count; i++) {
                Assert.AreEqual(all[i].Start, stored[i].Start, "Instant must survive the round trip");
                Assert.AreEqual(all[i].Start.Offset, stored[i].Start.Offset, "Offset must survive the round trip");
            }
        }

        var store = new NodeStore(DataStoreLocal.Open(dm, null, io));
        store.Insert(all);
        store.Maintenance(MaintenanceAction.ClearCache); // forces node segments to be re-read (deserialized) from the log
        verify(store);
        store.Dispose();

        store = new NodeStore(DataStoreLocal.Open(dm, null, io)); // reopen: full log replay
        verify(store);
        store.Dispose();
    }
}
