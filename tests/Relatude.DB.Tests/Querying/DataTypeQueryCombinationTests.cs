using System.Linq.Expressions;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

// Combinations across the full datatype matrix of ScalarNode: ordering per type, predicates that
// mix several datatypes, chained Where calls, paging over ordered results, aggregates, storage
// round-trip fidelity, index maintenance on update/delete, and restart persistence.
[TestClass]
public class DataTypeQueryCombinationTests {

    static void runForBothEngines(Action<NodeStore, List<ScalarNode>> battery) {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = ScalarData.OpenStore(out var all, persistedIndexes);
            try {
                battery(store, all);
            } catch (Exception e) {
                throw new AssertFailedException("persistedIndexes: " + persistedIndexes + " — " + e.Message, e);
            } finally {
                store.Dispose();
            }
        }
    }

    // Ordered value sequences must match LINQ. Ties leave the node order unspecified, so the
    // comparison is on the projected value sequence, which is deterministic either way.
    static void assertSameOrder<TValue>(NodeStore store, List<ScalarNode> all,
        Expression<Func<ScalarNode, object>> storeSelector, Func<ScalarNode, TValue> value,
        IComparer<TValue>? comparer = null, string? label = null) {
        comparer ??= Comparer<TValue>.Default;
        var asc = store.Query<ScalarNode>().OrderBy(storeSelector).Execute().Select(value).ToList();
        CollectionAssert.AreEqual(all.Select(value).OrderBy(v => v, comparer).ToList(), asc, "ascending " + (label ?? storeSelector.ToString()));
        var desc = store.Query<ScalarNode>().OrderByDescending(storeSelector).Execute().Select(value).ToList();
        CollectionAssert.AreEqual(all.Select(value).OrderByDescending(v => v, comparer).ToList(), desc, "descending " + (label ?? storeSelector.ToString()));
    }

    [TestMethod]
    public void OrderBy_EveryDataType_MatchesLinq() {
        runForBothEngines((store, all) => {
            assertSameOrder(store, all, x => x.BoolIndexed, x => x.BoolIndexed);
            assertSameOrder(store, all, x => x.IntIndexed, x => x.IntIndexed);
            assertSameOrder(store, all, x => x.LongIndexed, x => x.LongIndexed);
            assertSameOrder(store, all, x => x.FloatIndexed, x => x.FloatIndexed);
            assertSameOrder(store, all, x => x.DoubleIndexed, x => x.DoubleIndexed);
            assertSameOrder(store, all, x => x.DecimalIndexed, x => x.DecimalIndexed);
            assertSameOrder(store, all, x => x.DecimalPlain, x => x.DecimalPlain); // and via the materialized fallback sort
            // string ordering is ordinal in the engine, so compare against ordinal LINQ
            assertSameOrder(store, all, x => x.StringIndexed, x => x.StringIndexed, StringComparer.Ordinal);
            assertSameOrder(store, all, x => x.DateTimeIndexed, x => x.DateTimeIndexed);
            assertSameOrder(store, all, x => x.DateTimeOffsetIndexed, x => x.DateTimeOffsetIndexed);
            assertSameOrder(store, all, x => x.TimeSpanIndexed, x => x.TimeSpanIndexed);
            assertSameOrder(store, all, x => x.GuidIndexed, x => x.GuidIndexed);
            assertSameOrder(store, all, x => x.EnumIndexed, x => x.EnumIndexed);
        });
    }

    [TestMethod]
    public void OrderBy_AfterFilter_EveryDataType_MatchesLinq() {
        runForBothEngines((store, all) => {
            { // filter on one type, order by another
                var fromStore = store.Query<ScalarNode>()
                    .Where(x => x.IntIndexed > 0)
                    .OrderBy(x => x.DateTimeIndexed)
                    .Execute().Select(x => x.DateTimeIndexed).ToList();
                var fromLinq = all.Where(x => x.IntIndexed > 0).OrderBy(x => x.DateTimeIndexed).Select(x => x.DateTimeIndexed).ToList();
                CollectionAssert.AreEqual(fromLinq, fromStore);
            }
            { // filter on guid, order by decimal descending
                var fromStore = store.Query<ScalarNode>()
                    .Where(x => x.GuidIndexed != Guid.Empty)
                    .OrderByDescending(x => x.DecimalIndexed)
                    .Execute().Select(x => x.DecimalIndexed).ToList();
                var fromLinq = all.Where(x => x.GuidIndexed != Guid.Empty).OrderByDescending(x => x.DecimalIndexed).Select(x => x.DecimalIndexed).ToList();
                CollectionAssert.AreEqual(fromLinq, fromStore);
            }
            { // ordered paging: page 2 of the timespan ordering
                var fromStore = store.Query<ScalarNode>()
                    .OrderBy(x => x.TimeSpanIndexed)
                    .Page(1, 10)
                    .Execute().Select(x => x.TimeSpanIndexed).ToList();
                var fromLinq = all.OrderBy(x => x.TimeSpanIndexed).Skip(10).Take(10).Select(x => x.TimeSpanIndexed).ToList();
                CollectionAssert.AreEqual(fromLinq, fromStore);
            }
        });
    }

    [TestMethod]
    public void CrossType_ComplexPredicates_MatchLinq() {
        runForBothEngines((store, all) => {
            var cutoff = ScalarData.DtBase.AddDays(24);
            ScalarData.AssertSame(store, all, x => x.BoolIndexed && x.IntIndexed > 0 || x.StringIndexed == "alpha");
            ScalarData.AssertSame(store, all, x => (x.EnumIndexed == Sizes.Large || x.DoubleIndexed < 0) && x.DateTimeIndexed > cutoff);
            ScalarData.AssertSame(store, all, x => !(x.GuidIndexed == ScalarData.GuidA) && x.TimeSpanIndexed >= TimeSpan.Zero && x.LongIndexed <= 0);
            ScalarData.AssertSame(store, all, x => ((x.IntIndexed > -3 && x.IntIndexed < 3) || (x.FloatIndexed >= 0f && x.FloatIndexed <= 2.5f)) && !x.BoolIndexed);
            ScalarData.AssertSame(store, all, x => x.DecimalPlain > 0m == x.BoolIndexed); // bool-valued comparison against a bool property
            // an indexed and a non-indexed property in one predicate forces an index/row split
            ScalarData.AssertSame(store, all, x => x.IntIndexed > 0 && x.StringPlain != "");
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetPlain > new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(35)) || x.GuidIndexed == ScalarData.GuidB);
        });
    }

    [TestMethod]
    public void ChainedWhere_AcrossDataTypes_MatchesLinq() {
        runForBothEngines((store, all) => {
            var cutoff = ScalarData.DtBase.AddDays(15);
            var fromStore = store.Query<ScalarNode>()
                .Where(x => x.IntIndexed > -4)
                .Where(x => x.StringIndexed != "")
                .Where(x => x.DateTimeIndexed > cutoff)
                .Where(x => x.TimeSpanIndexed >= TimeSpan.FromHours(-3))
                .Count();
            var fromLinq = all.Count(x =>
                x.IntIndexed > -4 &&
                x.StringIndexed != "" &&
                x.DateTimeIndexed > cutoff &&
                x.TimeSpanIndexed >= TimeSpan.FromHours(-3));
            Assert.AreEqual(fromLinq, fromStore);
            Assert.IsTrue(fromStore > 0 && fromStore < all.Count, "Chained filter does not discriminate: " + fromStore);

            // Count() on the builder must agree with Execute().Count()
            var q = store.Query<ScalarNode>().Where(x => x.DecimalIndexed > 0m).Where(x => x.EnumIndexed != Sizes.Small);
            Assert.AreEqual(q.Execute().Count(), q.Count());
        });
    }

    [TestMethod]
    public void Aggregates_EveryNumericType_MatchLinq() {
        runForBothEngines((store, all) => {
            // extremes are excluded through the filter: LINQ Sum is checked arithmetic, and
            // float/double summation including ±MaxValue is order dependent
            { // int
                var fromStore = store.Query<ScalarNode>().Where(x => x.IntIndexed >= -5 && x.IntIndexed <= 5).Sum(x => x.IntIndexed);
                var fromLinq = all.Where(x => x.IntIndexed >= -5 && x.IntIndexed <= 5).Sum(x => x.IntIndexed);
                Assert.AreEqual(fromLinq, fromStore, "int sum");
            }
            { // int with arithmetic in the selector
                var fromStore = store.Query<ScalarNode>().Where(x => x.IntIndexed >= -5 && x.IntIndexed <= 5).Sum(x => x.IntIndexed * 2 + 1);
                var fromLinq = all.Where(x => x.IntIndexed >= -5 && x.IntIndexed <= 5).Sum(x => x.IntIndexed * 2 + 1);
                Assert.AreEqual(fromLinq, fromStore, "int sum with arithmetic");
            }
            { // long
                var fromStore = store.Query<ScalarNode>().Where(x => x.LongIndexed >= -12_000_000_000L && x.LongIndexed <= 12_000_000_000L).Sum(x => x.LongIndexed);
                var fromLinq = all.Where(x => x.LongIndexed >= -12_000_000_000L && x.LongIndexed <= 12_000_000_000L).Sum(x => x.LongIndexed);
                Assert.AreEqual(fromLinq, fromStore, "long sum");
            }
            { // float: values are exact multiples of 0.25, so the sum is exact in any order
                var fromStore = store.Query<ScalarNode>().Where(x => x.FloatIndexed >= -5f && x.FloatIndexed <= 5f).Sum(x => x.FloatIndexed);
                var fromLinq = all.Where(x => x.FloatIndexed >= -5f && x.FloatIndexed <= 5f).Sum(x => x.FloatIndexed);
                Assert.AreEqual(fromLinq, fromStore, "float sum");
            }
            { // double: values are exact multiples of 0.5
                var fromStore = store.Query<ScalarNode>().Where(x => x.DoubleIndexed >= -5.0 && x.DoubleIndexed <= 5.0).Sum(x => x.DoubleIndexed);
                var fromLinq = all.Where(x => x.DoubleIndexed >= -5.0 && x.DoubleIndexed <= 5.0).Sum(x => x.DoubleIndexed);
                Assert.AreEqual(fromLinq, fromStore, "double sum");
            }
            { // decimal
                var fromStore = store.Query<ScalarNode>().Where(x => x.DecimalIndexed >= -5m && x.DecimalIndexed <= 5m).Sum(x => x.DecimalIndexed);
                var fromLinq = all.Where(x => x.DecimalIndexed >= -5m && x.DecimalIndexed <= 5m).Sum(x => x.DecimalIndexed);
                Assert.AreEqual(fromLinq, fromStore, "decimal sum");
            }
        });
    }

    [TestMethod]
    public void RoundTrip_PreservesEveryDataType() {
        var inserted = ScalarData.Generate();
        var store = ScalarData.OpenStore(out var all, persistedIndexes: false);
        try {
            Assert.AreEqual(inserted.Count, all.Count);
            var byId = all.ToDictionary(x => x.Id);
            foreach (var src in inserted) {
                var read = byId[src.Id];
                Assert.AreEqual(src.BoolIndexed, read.BoolIndexed, "bool, id " + src.Id);
                Assert.AreEqual(src.IntIndexed, read.IntIndexed, "int, id " + src.Id);
                Assert.AreEqual(src.LongIndexed, read.LongIndexed, "long, id " + src.Id);
                Assert.AreEqual(src.FloatIndexed, read.FloatIndexed, "float, id " + src.Id);
                Assert.AreEqual(src.DoubleIndexed, read.DoubleIndexed, "double, id " + src.Id);
                Assert.AreEqual(src.DecimalIndexed, read.DecimalIndexed, "decimal, id " + src.Id);
                Assert.AreEqual(src.StringIndexed, read.StringIndexed, "string, id " + src.Id);
                Assert.AreEqual(src.DateTimeIndexed, read.DateTimeIndexed, "datetime, id " + src.Id);
                Assert.AreEqual(src.DateTimeOffsetIndexed, read.DateTimeOffsetIndexed, "datetimeoffset instant, id " + src.Id);
                Assert.AreEqual(src.DateTimeOffsetIndexed.Offset, read.DateTimeOffsetIndexed.Offset, "datetimeoffset offset, id " + src.Id);
                Assert.AreEqual(src.TimeSpanIndexed, read.TimeSpanIndexed, "timespan, id " + src.Id);
                Assert.AreEqual(src.GuidIndexed, read.GuidIndexed, "guid, id " + src.Id);
                Assert.AreEqual(src.EnumIndexed, read.EnumIndexed, "enum, id " + src.Id);
                // and the non-indexed twins, which travel through the same node payload
                Assert.AreEqual(src.StringPlain, read.StringPlain, "plain string, id " + src.Id);
                Assert.AreEqual(src.DecimalPlain, read.DecimalPlain, "plain decimal, id " + src.Id);
                Assert.AreEqual(src.DateTimeOffsetPlain, read.DateTimeOffsetPlain, "plain datetimeoffset, id " + src.Id);
            }
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void UpdatesAndDeletes_ReflectInFilters_EveryDataType() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = ScalarData.OpenStore(out var all, persistedIndexes);
            try {
                // move a handful of nodes to brand-new values, one property per datatype
                var targets = all.Where(x => x.Id <= 5).ToList();
                foreach (var n in targets) {
                    store.UpdateProperty<ScalarNode, int>(n.Id, x => x.IntIndexed, 7777);
                    store.UpdateProperty<ScalarNode, string>(n.Id, x => x.StringIndexed, "updated");
                    store.UpdateProperty<ScalarNode, Guid>(n.Id, x => x.GuidIndexed, ScalarData.GuidUnused);
                    store.UpdateProperty<ScalarNode, decimal>(n.Id, x => x.DecimalIndexed, 555.55m);
                    store.UpdateProperty<ScalarNode, DateTime>(n.Id, x => x.DateTimeIndexed, ScalarData.DtBase.AddYears(30));
                    store.UpdateProperty<ScalarNode, TimeSpan>(n.Id, x => x.TimeSpanIndexed, TimeSpan.FromDays(365));
                    store.UpdateProperty<ScalarNode, bool>(n.Id, x => x.BoolIndexed, true);
                    n.IntIndexed = 7777; n.StringIndexed = "updated"; n.GuidIndexed = ScalarData.GuidUnused;
                    n.DecimalIndexed = 555.55m; n.DateTimeIndexed = ScalarData.DtBase.AddYears(30);
                    n.TimeSpanIndexed = TimeSpan.FromDays(365); n.BoolIndexed = true;
                }
                store.Delete(all[5].Id); // and one node disappears entirely
                var remaining = all.Where(x => x.Id != all[5].Id).ToList();

                var suffix = " (persistedIndexes: " + persistedIndexes + ")";
                Assert.AreEqual(remaining.Count(x => x.IntIndexed == 7777),
                    store.Query<ScalarNode>().Where(x => x.IntIndexed == 7777).Count(), "updated int" + suffix);
                Assert.AreEqual(remaining.Count(x => x.StringIndexed == "updated"),
                    store.Query<ScalarNode>().Where(x => x.StringIndexed == "updated").Count(), "updated string" + suffix);
                Assert.AreEqual(remaining.Count(x => x.GuidIndexed == ScalarData.GuidUnused),
                    store.Query<ScalarNode>().Where(x => x.GuidIndexed == ScalarData.GuidUnused).Count(), "updated guid" + suffix);
                Assert.AreEqual(remaining.Count(x => x.DecimalIndexed == 555.55m),
                    store.Query<ScalarNode>().Where(x => x.DecimalIndexed == 555.55m).Count(), "updated decimal" + suffix);
                Assert.AreEqual(remaining.Count(x => x.DateTimeIndexed > ScalarData.DtBase.AddYears(10)),
                    store.Query<ScalarNode>().Where(x => x.DateTimeIndexed > ScalarData.DtBase.AddYears(10)).Count(), "updated datetime" + suffix);
                Assert.AreEqual(remaining.Count(x => x.TimeSpanIndexed == TimeSpan.FromDays(365)),
                    store.Query<ScalarNode>().Where(x => x.TimeSpanIndexed == TimeSpan.FromDays(365)).Count(), "updated timespan" + suffix);
                Assert.AreEqual(remaining.Count(x => x.BoolIndexed),
                    store.Query<ScalarNode>().Where(x => x.BoolIndexed).Count(), "updated bool" + suffix);
                // the old values must be gone from the indexes
                Assert.AreEqual(remaining.Count(x => x.GuidIndexed == ScalarData.GuidPalette[1]),
                    store.Query<ScalarNode>().Where(x => x.GuidIndexed == ScalarData.GuidA).Count(), "old guid gone" + suffix);
                Assert.AreEqual(remaining.Count(x => x.IntIndexed == 2),
                    store.Query<ScalarNode>().Where(x => x.IntIndexed == 2).Count(), "old int gone" + suffix);
            } finally {
                store.Dispose();
            }
        }
    }

    [TestMethod]
    public void Filters_SurviveRestart_EveryDataType() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var dir = Path.Combine(Path.GetTempPath(), "relatude-typematrix-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var store = openStoreOnDisk(dir, persistedIndexes);
                store.Insert(ScalarData.Generate());
                var truth = store.Query<ScalarNode>().ToList();
                store.Dispose();

                store = openStoreOnDisk(dir, persistedIndexes);
                try {
                    var suffix = " (persistedIndexes: " + persistedIndexes + ")";
                    Assert.AreEqual(truth.Count, store.Query<ScalarNode>().Count(), "total" + suffix);
                    Assert.AreEqual(truth.Count(x => x.BoolIndexed),
                        store.Query<ScalarNode>().Where(x => x.BoolIndexed).Count(), "bool" + suffix);
                    Assert.AreEqual(truth.Count(x => x.IntIndexed >= -2 && x.IntIndexed <= 3),
                        store.Query<ScalarNode>().Where(x => x.IntIndexed >= -2 && x.IntIndexed <= 3).Count(), "int range" + suffix);
                    Assert.AreEqual(truth.Count(x => x.LongIndexed > 0),
                        store.Query<ScalarNode>().Where(x => x.LongIndexed > 0).Count(), "long" + suffix);
                    Assert.AreEqual(truth.Count(x => x.FloatIndexed <= 0f),
                        store.Query<ScalarNode>().Where(x => x.FloatIndexed <= 0f).Count(), "float" + suffix);
                    Assert.AreEqual(truth.Count(x => x.DoubleIndexed < 0),
                        store.Query<ScalarNode>().Where(x => x.DoubleIndexed < 0).Count(), "double" + suffix);
                    Assert.AreEqual(truth.Count(x => x.DecimalIndexed > 0m),
                        store.Query<ScalarNode>().Where(x => x.DecimalIndexed > 0m).Count(), "decimal" + suffix);
                    Assert.AreEqual(truth.Count(x => x.StringIndexed == "alpha"),
                        store.Query<ScalarNode>().Where(x => x.StringIndexed == "alpha").Count(), "string" + suffix);
                    var from = ScalarData.DtBase.AddDays(9); var to = ScalarData.DtBase.AddDays(36);
                    Assert.AreEqual(truth.Count(x => x.DateTimeIndexed >= from && x.DateTimeIndexed <= to),
                        store.Query<ScalarNode>().Where(x => x.DateTimeIndexed >= from && x.DateTimeIndexed <= to).Count(), "datetime range" + suffix);
                    Assert.AreEqual(truth.Count(x => x.DateTimeIndexed >= from && x.DateTimeIndexed <= to),
                        store.Query<ScalarNode>().Where(x => x.DateTimeIndexed.InRange(from, to)).Count(), "datetime InRange" + suffix);
                    var instant = new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(35));
                    Assert.AreEqual(truth.Count(x => x.DateTimeOffsetIndexed == instant),
                        store.Query<ScalarNode>().Where(x => x.DateTimeOffsetIndexed == instant).Count(), "datetimeoffset" + suffix);
                    Assert.AreEqual(truth.Count(x => x.TimeSpanIndexed > TimeSpan.Zero),
                        store.Query<ScalarNode>().Where(x => x.TimeSpanIndexed > TimeSpan.Zero).Count(), "timespan" + suffix);
                    Assert.AreEqual(truth.Count(x => x.GuidIndexed == ScalarData.GuidB),
                        store.Query<ScalarNode>().Where(x => x.GuidIndexed == ScalarData.GuidB).Count(), "guid" + suffix);
                    Assert.AreEqual(truth.Count(x => x.EnumIndexed == Sizes.Medium),
                        store.Query<ScalarNode>().Where(x => x.EnumIndexed == Sizes.Medium).Count(), "enum" + suffix);
                } finally {
                    store.Dispose();
                }
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    static NodeStore openStoreOnDisk(string dir, bool persistedIndexes) {
        var dm = new Datamodel();
        dm.Add<ScalarNode>();
        if (persistedIndexes) {
            var settings = new SettingsLocal {
                UsePersistedValueIndexesByDefault = true,
                PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            };
            return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
                () => new IndexEngines(new NativeKvIndexStore(dir))));
        }
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), new IOProviderDisk(dir), null, null, null, null, null));
    }
}
