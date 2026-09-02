using System.Linq.Expressions;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region all-scalar-datatypes test datamodel
// One property pair per scalar datatype the engine supports: the "Indexed" member goes through the
// value index, the "Plain" twin holds the exact same value but is evaluated row by row. Every
// filter must answer identically through both paths.
[Node]
public class ScalarNode {
    [InternalIdProperty]
    public int Id { get; set; }

    [BooleanProperty(Indexed = true)]
    public bool BoolIndexed { get; set; }
    [BooleanProperty(Indexed = false)]
    public bool BoolPlain { get; set; }

    [IntegerProperty(Indexed = true)]
    public int IntIndexed { get; set; }
    [IntegerProperty(Indexed = false)]
    public int IntPlain { get; set; }

    [LongProperty(Indexed = true)]
    public long LongIndexed { get; set; }
    [LongProperty(Indexed = false)]
    public long LongPlain { get; set; }

    [FloatProperty(Indexed = true)]
    public float FloatIndexed { get; set; }
    [FloatProperty(Indexed = false)]
    public float FloatPlain { get; set; }

    [DoubleProperty(Indexed = true)]
    public double DoubleIndexed { get; set; }
    [DoubleProperty(Indexed = false)]
    public double DoublePlain { get; set; }

    [DecimalProperty(Indexed = true)]
    public decimal DecimalIndexed { get; set; }
    [DecimalProperty(Indexed = false)]
    public decimal DecimalPlain { get; set; }

    [StringProperty(Indexed = true)]
    public string StringIndexed { get; set; } = "";
    [StringProperty(Indexed = false)]
    public string StringPlain { get; set; } = "";

    [DateTimeProperty(Indexed = true)]
    public DateTime DateTimeIndexed { get; set; }
    [DateTimeProperty(Indexed = false)]
    public DateTime DateTimePlain { get; set; }

    [DateTimeOffsetProperty(Indexed = true)]
    public DateTimeOffset DateTimeOffsetIndexed { get; set; }
    [DateTimeOffsetProperty(Indexed = false)]
    public DateTimeOffset DateTimeOffsetPlain { get; set; }

    [TimeSpanProperty(Indexed = true)]
    public TimeSpan TimeSpanIndexed { get; set; }
    [TimeSpanProperty(Indexed = false)]
    public TimeSpan TimeSpanPlain { get; set; }

    [GuidProperty(Indexed = true)]
    public Guid GuidIndexed { get; set; }
    [GuidProperty(Indexed = false)]
    public Guid GuidPlain { get; set; }

    [IntegerProperty(Indexed = true)]
    public Sizes EnumIndexed { get; set; }
    [IntegerProperty(Indexed = false)]
    public Sizes EnumPlain { get; set; }
}
#endregion

internal static class ScalarData {
    internal const int Count = 90;

    internal static readonly Guid GuidA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid GuidB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid GuidC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    internal static readonly Guid GuidD = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    internal static readonly Guid GuidE = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    internal static readonly Guid GuidUnused = Guid.Parse("99999999-9999-9999-9999-999999999999");
    internal static readonly Guid[] GuidPalette = [Guid.Empty, GuidA, GuidB, GuidC, GuidD, GuidE];

    // duplicates, both casings, empty, embedded/leading/trailing whitespace, a digit prefix and a non-ASCII letter
    internal static readonly string[] StringPalette =
        ["", "alpha", "Alpha", "beta", "beta beta", "Ωmega", "zzz", "0numeric", " lead", "trail "];

    internal static readonly DateTime DtBase = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime DtoBaseUtc = new(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    // Deterministic values with duplicates, negatives, zero and per-type extremes. The extreme
    // values sit on distinct ids so they never coincide, keeping every combination predictable.
    internal static List<ScalarNode> Generate() {
        var list = new List<ScalarNode>();
        for (var i = 1; i <= Count; i++) {
            var n = new ScalarNode {
                Id = i,
                BoolIndexed = i % 3 == 0,
                IntIndexed = i switch { 7 => int.MaxValue, 14 => int.MinValue, _ => (i % 11) - 5 },
                LongIndexed = i switch { 8 => long.MaxValue, 16 => long.MinValue, _ => ((i % 9) - 4) * 3_000_000_000L },
                FloatIndexed = i switch { 10 => float.MaxValue, 20 => float.MinValue, _ => ((i % 7) - 3) * 1.25f },
                DoubleIndexed = i switch { 11 => double.MaxValue, 22 => double.MinValue, _ => ((i % 13) - 6) * 0.5 },
                DecimalIndexed = DecimalValueFor(i),
                StringIndexed = StringPalette[i % 10],
                DateTimeIndexed = i switch { 9 => DateTime.MinValue, 18 => DateTime.MaxValue, _ => DtBase.AddDays((i % 17) * 3) },
                DateTimeOffsetIndexed = i switch {
                    13 => DateTimeOffset.MinValue,
                    26 => DateTimeOffset.MaxValue,
                    // varying offsets over repeating instants: equality and ordering follow the instant
                    _ => new DateTimeOffset(DtoBaseUtc.AddHours((i % 15) * 7)).ToOffset(TimeSpan.FromHours((i % 5) - 2)),
                },
                TimeSpanIndexed = i switch { 15 => TimeSpan.MaxValue, 30 => TimeSpan.MinValue, _ => TimeSpan.FromMinutes(((i % 10) - 4) * 90) },
                GuidIndexed = GuidPalette[i % 6],
                EnumIndexed = (Sizes)(i % 3),
            };
            n.BoolPlain = n.BoolIndexed;
            n.IntPlain = n.IntIndexed;
            n.LongPlain = n.LongIndexed;
            n.FloatPlain = n.FloatIndexed;
            n.DoublePlain = n.DoubleIndexed;
            n.DecimalPlain = n.DecimalIndexed;
            n.StringPlain = n.StringIndexed;
            n.DateTimePlain = n.DateTimeIndexed;
            n.DateTimeOffsetPlain = n.DateTimeOffsetIndexed;
            n.TimeSpanPlain = n.TimeSpanIndexed;
            n.GuidPlain = n.GuidIndexed;
            n.EnumPlain = n.EnumIndexed;
            list.Add(n);
        }
        return list;
    }

    internal static decimal DecimalValueFor(int i) => i switch {
        12 => decimal.MaxValue,
        24 => decimal.MinValue,
        36 => 0.0000000001m,                            // high precision, tests scale preservation
        48 => 1.0100m,                                  // same value as 1.01m in a different scale representation
        60 => 0.0000000000000000000000000001m,          // 1e-28, the smallest positive decimal
        _ => ((i % 8) - 4) * 1.01m,
    };

    internal static NodeStore OpenStore(out List<ScalarNode> all, bool persistedIndexes = false) {
        var dm = new Datamodel();
        dm.Add<ScalarNode>();
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            }, null, null, null, null, null, () => IndexEngines.Single(TestEngines.ValueId, new NativeKvIndexStore(null))))
            : new NodeStore(DataStoreLocal.Open(dm));
        store.Insert(Generate());
        all = store.Query<ScalarNode>().ToList();
        return store;
    }

    // The store must agree with compiled LINQ over the same predicate, on the full id set.
    internal static void AssertSame(NodeStore store, List<ScalarNode> all,
        Expression<Func<ScalarNode, bool>> predicate, bool mustDiscriminate = true) {
        var fromStore = store.Query<ScalarNode>().Where(predicate).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var fromLinq = all.Where(predicate.Compile()).Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(fromLinq, fromStore, "Store and LINQ disagree for: " + predicate);
        if (mustDiscriminate) // guard against a parse bug reducing the predicate to constant true/false
            Assert.IsTrue(fromLinq.Count > 0 && fromLinq.Count < all.Count,
                "Predicate does not discriminate (matched " + fromLinq.Count + " of " + all.Count + "): " + predicate);
    }

    // InRange cannot be compiled (it exists only for building query expressions), so the store's
    // InRange result is checked against the store's own >=/<= form and against LINQ's range.
    internal static void AssertInRangeAgrees(NodeStore store, List<ScalarNode> all,
        Expression<Func<ScalarNode, bool>> inRangeForStore, Expression<Func<ScalarNode, bool>> rangeAsComparisons) {
        var viaInRange = store.Query<ScalarNode>().Where(inRangeForStore).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var viaComparisons = store.Query<ScalarNode>().Where(rangeAsComparisons).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var fromLinq = all.Where(rangeAsComparisons.Compile()).Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(fromLinq, viaComparisons, "Store and LINQ disagree for: " + rangeAsComparisons);
        CollectionAssert.AreEqual(fromLinq, viaInRange, "InRange disagrees with the equivalent comparisons for: " + inRangeForStore);
        Assert.IsTrue(fromLinq.Count > 0 && fromLinq.Count < all.Count,
            "Range does not discriminate (matched " + fromLinq.Count + " of " + all.Count + "): " + rangeAsComparisons);
    }

    // WhereIn must answer like LINQ Contains over the same values.
    internal static void AssertSameIn<TProperty>(NodeStore store, List<ScalarNode> all,
        Expression<Func<ScalarNode, TProperty>> property, TProperty[] values, bool mustDiscriminate = true) {
        var fromStore = store.Query<ScalarNode>().WhereIn(property, values).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var get = property.Compile();
        var fromLinq = all.Where(c => values.Contains(get(c))).Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(fromLinq, fromStore, "Store and LINQ disagree for WhereIn on: " + property);
        if (mustDiscriminate)
            Assert.IsTrue(fromLinq.Count > 0 && fromLinq.Count < all.Count,
                "WhereIn does not discriminate (matched " + fromLinq.Count + " of " + all.Count + "): " + property);
    }
}

// One test per datatype, each running the same battery of filter shapes — equality, inequality,
// all four ordering comparisons, ranges, OR, NOT, closures, extremes, and the non-indexed twin —
// against compiled LINQ, under both the in-memory and the persisted native KV value indexes.
[TestClass]
public class DataTypeQueryMatrixTests {

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

    [TestMethod]
    public void Bool_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.BoolIndexed); // naked bool member as predicate
            ScalarData.AssertSame(store, all, x => !x.BoolIndexed);
            ScalarData.AssertSame(store, all, x => x.BoolIndexed == true);
            ScalarData.AssertSame(store, all, x => x.BoolIndexed == false);
            ScalarData.AssertSame(store, all, x => x.BoolIndexed != true);
            ScalarData.AssertSame(store, all, x => x.BoolIndexed && x.IntIndexed > 0);
            ScalarData.AssertSame(store, all, x => x.BoolIndexed || x.IntIndexed > 3);
            // the twin path: row evaluation must answer identically
            ScalarData.AssertSame(store, all, x => x.BoolPlain);
            ScalarData.AssertSame(store, all, x => !x.BoolPlain);
            // twins hold identical values: == matches all, != matches none
            ScalarData.AssertSame(store, all, x => x.BoolIndexed == x.BoolPlain, mustDiscriminate: false);
            Assert.AreEqual(0, store.Query<ScalarNode>().Where(x => x.BoolIndexed != x.BoolPlain).Count());
        });
    }

    [TestMethod]
    public void Int_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.IntIndexed == 2);
            ScalarData.AssertSame(store, all, x => x.IntIndexed != 2);
            ScalarData.AssertSame(store, all, x => x.IntIndexed > 2);
            ScalarData.AssertSame(store, all, x => x.IntIndexed >= 2);
            ScalarData.AssertSame(store, all, x => x.IntIndexed < 0);
            ScalarData.AssertSame(store, all, x => x.IntIndexed <= -1);
            ScalarData.AssertSame(store, all, x => x.IntIndexed >= -2 && x.IntIndexed <= 3);
            ScalarData.AssertSame(store, all, x => x.IntIndexed == -5 || x.IntIndexed == 5);
            ScalarData.AssertSame(store, all, x => !(x.IntIndexed > 2));
            ScalarData.AssertSame(store, all, x => (x.IntIndexed > 2 && x.IntIndexed < 5) || x.IntIndexed == -4);
            var min = 1;
            ScalarData.AssertSame(store, all, x => x.IntIndexed > min);
            ScalarData.AssertSame(store, all, x => x.IntIndexed > min + 1);
            // extremes: bounds arithmetic must not overflow
            ScalarData.AssertSame(store, all, x => x.IntIndexed == int.MaxValue);
            ScalarData.AssertSame(store, all, x => x.IntIndexed != int.MaxValue);
            ScalarData.AssertSame(store, all, x => x.IntIndexed > int.MinValue);
            ScalarData.AssertSame(store, all, x => x.IntIndexed < int.MaxValue);
            ScalarData.AssertSame(store, all, x => x.IntIndexed <= int.MaxValue, mustDiscriminate: false); // matches all
            ScalarData.AssertSame(store, all, x => x.IntIndexed >= int.MinValue, mustDiscriminate: false); // matches all
            Assert.AreEqual(0, store.Query<ScalarNode>().Where(x => x.IntIndexed > int.MaxValue).Count());
            Assert.AreEqual(0, store.Query<ScalarNode>().Where(x => x.IntIndexed < int.MinValue).Count());
            // twin
            ScalarData.AssertSame(store, all, x => x.IntPlain == 2);
            ScalarData.AssertSame(store, all, x => x.IntPlain > 2);
            ScalarData.AssertSame(store, all, x => x.IntPlain >= -2 && x.IntPlain <= 3);
            ScalarData.AssertSame(store, all, x => x.IntIndexed > 0 && x.IntPlain < 4); // forces an index/row split
        });
    }

    [TestMethod]
    public void Long_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.LongIndexed == 0L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed != 3_000_000_000L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed > 0L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed >= 6_000_000_000L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed < 0L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed <= -6_000_000_000L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed >= -6_000_000_000L && x.LongIndexed <= 6_000_000_000L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed == -12_000_000_000L || x.LongIndexed == 12_000_000_000L);
            ScalarData.AssertSame(store, all, x => !(x.LongIndexed > 0L));
            var cutoff = 3_000_000_000L;
            ScalarData.AssertSame(store, all, x => x.LongIndexed > cutoff);
            // extremes
            ScalarData.AssertSame(store, all, x => x.LongIndexed == long.MaxValue);
            ScalarData.AssertSame(store, all, x => x.LongIndexed > long.MinValue);
            ScalarData.AssertSame(store, all, x => x.LongIndexed < long.MaxValue);
            ScalarData.AssertSame(store, all, x => x.LongIndexed >= long.MinValue, mustDiscriminate: false); // matches all
            Assert.AreEqual(0, store.Query<ScalarNode>().Where(x => x.LongIndexed > long.MaxValue).Count());
            // twin
            ScalarData.AssertSame(store, all, x => x.LongPlain == 0L);
            ScalarData.AssertSame(store, all, x => x.LongPlain > 0L);
            ScalarData.AssertSame(store, all, x => x.LongIndexed < 0L || x.LongPlain > 6_000_000_000L);
            // int/long promotion across properties
            ScalarData.AssertSame(store, all, x => x.LongIndexed > x.IntIndexed);
        });
    }

    [TestMethod]
    public void Float_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.FloatIndexed == 0f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed != 1.25f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed > 0f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed >= 2.5f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed < -1.25f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed <= 0f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed >= -2.5f && x.FloatIndexed <= 1.25f);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed == -3.75f || x.FloatIndexed == 3.75f);
            ScalarData.AssertSame(store, all, x => !(x.FloatIndexed >= 0f));
            var limit = 1.25f;
            ScalarData.AssertSame(store, all, x => x.FloatIndexed > limit);
            // extremes
            ScalarData.AssertSame(store, all, x => x.FloatIndexed == float.MaxValue);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed > float.MinValue);
            ScalarData.AssertSame(store, all, x => x.FloatIndexed < float.MaxValue);
            // twin
            ScalarData.AssertSame(store, all, x => x.FloatPlain == 0f);
            ScalarData.AssertSame(store, all, x => x.FloatPlain <= 0f);
            // float/double promotion across properties
            ScalarData.AssertSame(store, all, x => x.FloatIndexed < x.DoubleIndexed);
        });
    }

    [TestMethod]
    public void Double_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed == -0.5);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed != -0.5);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed > 1.0);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed >= 1.5);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed < 0.0);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed <= -1.5);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed >= -2.0 && x.DoubleIndexed <= 2.0);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed == -3.0 || x.DoubleIndexed == 3.0);
            ScalarData.AssertSame(store, all, x => !(x.DoubleIndexed < 0.0));
            var limit = 0.5;
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed >= limit);
            // extremes
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed == double.MaxValue);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed > double.MinValue);
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed < double.MaxValue);
            // twin
            ScalarData.AssertSame(store, all, x => x.DoublePlain == -0.5);
            ScalarData.AssertSame(store, all, x => x.DoublePlain > 1.0);
            // double/int promotion across properties
            ScalarData.AssertSame(store, all, x => x.DoubleIndexed > x.IntIndexed);
        });
    }

    [TestMethod]
    public void Decimal_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed == 1.01m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed != -1.01m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed > 0m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed >= 2.02m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed < 0m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed <= -2.02m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed >= -2.02m && x.DecimalIndexed <= 2.02m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed == -4.04m || x.DecimalIndexed == 3.03m);
            ScalarData.AssertSame(store, all, x => !(x.DecimalIndexed > 0m));
            var limit = 1.01m;
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed > limit);
            // scale representations of the same value are equal: 1.0100m must match every 1.01m
            // and the other way around, exactly as decimal == does
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed == 1.0100m);
            // precision: 10 and 28 decimal places must survive storage and compare exactly
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed == 0.0000000001m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed == 0.0000000000000000000000000001m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed > 0m && x.DecimalIndexed < 1m);
            // extremes
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed == decimal.MaxValue);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed > 1_000_000m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed < -1_000_000m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed > decimal.MinValue);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed < decimal.MaxValue);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed >= decimal.MinValue, mustDiscriminate: false); // matches all
            // twin: row evaluation must answer identically
            ScalarData.AssertSame(store, all, x => x.DecimalPlain == 1.01m);
            ScalarData.AssertSame(store, all, x => x.DecimalPlain < 0m);
            ScalarData.AssertSame(store, all, x => x.DecimalIndexed > 0m && x.DecimalPlain < 3m);
        });
    }

    [TestMethod]
    public void String_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "alpha");
            ScalarData.AssertSame(store, all, x => x.StringIndexed != "alpha");
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "Alpha"); // equality is case sensitive
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "");
            ScalarData.AssertSame(store, all, x => x.StringIndexed != "");
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "Ωmega");     // non-ASCII
            ScalarData.AssertSame(store, all, x => x.StringIndexed == " lead");     // leading space preserved
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "trail ");    // trailing space preserved
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "beta beta"); // embedded space
            ScalarData.AssertSame(store, all, x => x.StringIndexed == "alpha" || x.StringIndexed == "zzz");
            ScalarData.AssertSame(store, all, x => !(x.StringIndexed == "alpha"));
            var name = "0numeric";
            ScalarData.AssertSame(store, all, x => x.StringIndexed == name);
            // twin
            ScalarData.AssertSame(store, all, x => x.StringPlain == "alpha");
            ScalarData.AssertSame(store, all, x => x.StringPlain != "");
            ScalarData.AssertSame(store, all, x => x.StringIndexed != "" && x.StringPlain != "zzz");
        });
    }

    [TestMethod]
    public void DateTime_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            var mid = ScalarData.DtBase.AddDays(30);
            var from = ScalarData.DtBase.AddDays(9);
            var to = ScalarData.DtBase.AddDays(36);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed == mid);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed != mid);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed > mid);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed >= mid);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed < mid);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed <= mid);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed >= from && x.DateTimeIndexed <= to);
            ScalarData.AssertInRangeAgrees(store, all, // InRange is inclusive both ends, like the && form
                x => x.DateTimeIndexed.InRange(from, to),
                x => x.DateTimeIndexed >= from && x.DateTimeIndexed <= to);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed == from || x.DateTimeIndexed == to);
            ScalarData.AssertSame(store, all, x => !(x.DateTimeIndexed > mid));
            // extremes
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed == DateTime.MinValue);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed == DateTime.MaxValue);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed > DateTime.MinValue);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed < DateTime.MaxValue);
            ScalarData.AssertSame(store, all, x => x.DateTimeIndexed <= DateTime.MaxValue, mustDiscriminate: false); // matches all
            // twin
            ScalarData.AssertSame(store, all, x => x.DateTimePlain == mid);
            ScalarData.AssertSame(store, all, x => x.DateTimePlain > mid);
            ScalarData.AssertInRangeAgrees(store, all,
                x => x.DateTimePlain.InRange(from, to),
                x => x.DateTimePlain >= from && x.DateTimePlain <= to);
        });
    }

    [TestMethod]
    public void DateTimeOffset_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            // stored offsets vary; DateTimeOffset comparisons follow the instant, so a constant
            // with a different offset but the same instant must match
            var instant = new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(35)).ToOffset(TimeSpan.FromHours(5));
            var from = new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(14));
            var to = new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(70));
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed == instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed != instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed > instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed >= instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed < instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed <= instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed >= from && x.DateTimeOffsetIndexed <= to);
            ScalarData.AssertInRangeAgrees(store, all,
                x => x.DateTimeOffsetIndexed.InRange(from, to),
                x => x.DateTimeOffsetIndexed >= from && x.DateTimeOffsetIndexed <= to);
            ScalarData.AssertSame(store, all, x => !(x.DateTimeOffsetIndexed < instant));
            // extremes
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed == DateTimeOffset.MinValue);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed == DateTimeOffset.MaxValue);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetIndexed > DateTimeOffset.MinValue);
            // twin
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetPlain == instant);
            ScalarData.AssertSame(store, all, x => x.DateTimeOffsetPlain > instant);
        });
    }

    [TestMethod]
    public void TimeSpan_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed == TimeSpan.Zero);
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed != TimeSpan.Zero);
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed > TimeSpan.Zero);
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed >= TimeSpan.FromHours(3));
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed < TimeSpan.Zero); // negative durations
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed <= TimeSpan.FromHours(-3));
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed >= TimeSpan.FromHours(-3) && x.TimeSpanIndexed <= TimeSpan.FromHours(3));
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed == TimeSpan.FromMinutes(-360) || x.TimeSpanIndexed == TimeSpan.FromMinutes(450));
            ScalarData.AssertSame(store, all, x => !(x.TimeSpanIndexed > TimeSpan.Zero));
            var cutoff = TimeSpan.FromMinutes(90);
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed >= cutoff);
            // extremes
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed == TimeSpan.MaxValue);
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed > TimeSpan.MinValue);
            ScalarData.AssertSame(store, all, x => x.TimeSpanIndexed < TimeSpan.MaxValue);
            // twin
            ScalarData.AssertSame(store, all, x => x.TimeSpanPlain == TimeSpan.Zero);
            ScalarData.AssertSame(store, all, x => x.TimeSpanPlain < TimeSpan.Zero);
        });
    }

    [TestMethod]
    public void Guid_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.GuidIndexed == ScalarData.GuidB);
            ScalarData.AssertSame(store, all, x => x.GuidIndexed != ScalarData.GuidB);
            ScalarData.AssertSame(store, all, x => x.GuidIndexed == Guid.Empty); // empty guid is a value, not "unset"
            ScalarData.AssertSame(store, all, x => x.GuidIndexed != Guid.Empty);
            ScalarData.AssertSame(store, all, x => x.GuidIndexed == ScalarData.GuidA || x.GuidIndexed == ScalarData.GuidC);
            ScalarData.AssertSame(store, all, x => !(x.GuidIndexed == ScalarData.GuidA));
            var captured = ScalarData.GuidD;
            ScalarData.AssertSame(store, all, x => x.GuidIndexed == captured);
            Assert.AreEqual(0, store.Query<ScalarNode>().Where(x => x.GuidIndexed == ScalarData.GuidUnused).Count());
            // twin
            ScalarData.AssertSame(store, all, x => x.GuidPlain == ScalarData.GuidB);
            ScalarData.AssertSame(store, all, x => x.GuidPlain != Guid.Empty);
            ScalarData.AssertSame(store, all, x => x.GuidIndexed == ScalarData.GuidB && x.IntIndexed > 0);
        });
    }

    [TestMethod]
    public void Enum_FilterBattery_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSame(store, all, x => x.EnumIndexed == Sizes.Medium);
            ScalarData.AssertSame(store, all, x => x.EnumIndexed != Sizes.Small);
            ScalarData.AssertSame(store, all, x => x.EnumIndexed == Sizes.Small || x.EnumIndexed == Sizes.Large);
            ScalarData.AssertSame(store, all, x => !(x.EnumIndexed == Sizes.Medium));
            var captured = Sizes.Large;
            ScalarData.AssertSame(store, all, x => x.EnumIndexed == captured);
            // enums are ordered by their numeric value
            ScalarData.AssertSame(store, all, x => x.EnumIndexed >= Sizes.Medium);
            ScalarData.AssertSame(store, all, x => x.EnumIndexed < Sizes.Large);
            // twin
            ScalarData.AssertSame(store, all, x => x.EnumPlain == Sizes.Medium);
            ScalarData.AssertSame(store, all, x => x.EnumPlain != Sizes.Small);
            ScalarData.AssertSame(store, all, x => x.EnumIndexed == Sizes.Large && x.StringIndexed != "");
        });
    }

    [TestMethod]
    public void WhereIn_EveryDataType_MatchesLinq() {
        runForBothEngines((store, all) => {
            ScalarData.AssertSameIn(store, all, x => x.IntIndexed, [1, 3, -5, 999]);
            ScalarData.AssertSameIn(store, all, x => x.LongIndexed, [0L, 6_000_000_000L, 123L]);
            ScalarData.AssertSameIn(store, all, x => x.FloatIndexed, [0f, 2.5f, 99f]);
            ScalarData.AssertSameIn(store, all, x => x.DoubleIndexed, [-0.5, 1.5, 99.0]);
            ScalarData.AssertSameIn(store, all, x => x.DecimalIndexed, [1.01m, -2.02m, 99m]); // 1.01m must also match the 1.0100m representation
            ScalarData.AssertSameIn(store, all, x => x.StringIndexed, ["alpha", "zzz", "no such value"]);
            ScalarData.AssertSameIn(store, all, x => x.DateTimeIndexed, [ScalarData.DtBase.AddDays(30), ScalarData.DtBase.AddDays(9), new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)]);
            ScalarData.AssertSameIn(store, all, x => x.DateTimeOffsetIndexed, [new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(35)), new DateTimeOffset(ScalarData.DtoBaseUtc.AddHours(14))]);
            ScalarData.AssertSameIn(store, all, x => x.TimeSpanIndexed, [TimeSpan.Zero, TimeSpan.FromMinutes(90), TimeSpan.FromDays(400)]);
            ScalarData.AssertSameIn(store, all, x => x.GuidIndexed, [ScalarData.GuidA, ScalarData.GuidC, ScalarData.GuidUnused]);
            ScalarData.AssertSameIn(store, all, x => x.EnumIndexed, [Sizes.Small, Sizes.Large]);
            ScalarData.AssertSameIn(store, all, x => x.BoolIndexed, [true], mustDiscriminate: true);
            // non-indexed twins answer through row evaluation
            ScalarData.AssertSameIn(store, all, x => x.IntPlain, [1, 3, -5]);
            ScalarData.AssertSameIn(store, all, x => x.StringPlain, ["alpha", "zzz"]);
            ScalarData.AssertSameIn(store, all, x => x.GuidPlain, [ScalarData.GuidA, ScalarData.GuidC]);
            ScalarData.AssertSameIn(store, all, x => x.DecimalPlain, [1.01m, -2.02m]);
            // empty list matches nothing
            Assert.AreEqual(0, store.Query<ScalarNode>().WhereIn(x => x.IntIndexed, Array.Empty<int>()).Count());
            Assert.AreEqual(0, store.Query<ScalarNode>().WhereIn(x => x.StringIndexed, Array.Empty<string>()).Count());
            Assert.AreEqual(0, store.Query<ScalarNode>().WhereIn(x => x.GuidIndexed, Array.Empty<Guid>()).Count());
        });
    }
}
