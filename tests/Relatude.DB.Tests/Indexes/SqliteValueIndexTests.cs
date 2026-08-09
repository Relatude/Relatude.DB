using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace Relatude.Indexes;

/// <summary>
/// The sqlite value index across every property type it declares a column type for
/// (SqliteIndexStore.getSqlType). Two invariants are covered per type: values round-trip exactly
/// through the TEXT/INTEGER/REAL encoding, and - for the types that support range queries - the
/// order sqlite compares the stored representation in is the order Comparer&lt;T&gt; puts the
/// values in, which is what MIN/MAX, RangeSearch and the facet "missing value" bucket rely on.
/// </summary>
[TestClass]
public class SqliteValueIndexTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Fills a fresh single-index store with <paramref name="values"/> (node id = position
    /// + 1), runs <paramref name="assert"/> against it, and cleans up.</summary>
    static void withIndex<T>(PropertyType type, T[] values, Action<IValueIndex<T>, T[]> assert) where T : notnull {
        var dir = tempDir();
        try {
            using var store = new SqliteIndexStore(dir);
            var index = store.OpenValueIndex<T>(new SetRegister(100), Guid.NewGuid().ToString(), type + " test", type);
            store.BeginTransaction();
            for (var i = 0; i < values.Length; i++) index.Add(i + 1, values[i]);
            store.CommitTransaction(1);
            assert(index, values);
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Round-trip and lookup by value: everything a type needs to be usable at all.</summary>
    static void assertRoundTrip<T>(PropertyType type, params T[] values) where T : notnull {
        withIndex<T>(type, values, (index, vs) => {
            Assert.AreEqual(vs.Length, index.IdCount, "id count");
            for (var i = 0; i < vs.Length; i++) {
                Assert.IsTrue(index.TryGetValue(i + 1, out var read), $"{type}: no value for id {i + 1}");
                Assert.AreEqual(vs[i], read, $"{type}: value {i} did not round-trip");
                Assert.IsTrue(index.ContainsValue(vs[i]), $"{type}: ContainsValue({vs[i]})");
                CollectionAssert.Contains(index.GetIds(vs[i]).ToArray(), i + 1, $"{type}: GetIds({vs[i]})");
                // the query planner's estimate must not rule out a value that is in the index
                Assert.IsTrue(index.MaxCount(IndexOperator.Equal, vs[i]) > 0, $"{type}: MaxCount says {vs[i]} cannot be present");
            }
            CollectionAssert.AreEquivalent(vs.Distinct().ToArray(), index.UniqueValues.ToArray(), $"{type}: unique values");
            // the facet "missing value" bucket asks for everything between MIN and MAX, so a
            // MIN/MAX fed back as a bound has to still match the rows it came from
            var all = index.RangeSearch(index.MinValue()!, index.MaxValue()!, true, true).ToArray();
            Assert.AreEqual(vs.Length, all.Length, $"{type}: MIN..MAX range did not cover every id");
        });
    }

    /// <summary>Sqlite's ordering of the stored representation matches Comparer&lt;T&gt;.Default.</summary>
    static void assertOrdering<T>(PropertyType type, params T[] values) where T : notnull {
        withIndex<T>(type, values, (index, vs) => {
            var sorted = vs.OrderBy(v => v, Comparer<T>.Default).ToArray();
            Assert.AreEqual(sorted[0], index.MinValue(), $"{type}: MinValue");
            Assert.AreEqual(sorted[^1], index.MaxValue(), $"{type}: MaxValue");
            foreach (var v in vs) {
                var expectedBelow = vs.Count(o => Comparer<T>.Default.Compare(o, v) < 0);
                var expectedAbove = vs.Count(o => Comparer<T>.Default.Compare(o, v) > 0);
                Assert.AreEqual(expectedBelow, index.CountLessThan(v, false), $"{type}: count below {v}");
                Assert.AreEqual(expectedAbove, index.CountGreaterThan(v, false), $"{type}: count above {v}");
                var expectedIds = Enumerable.Range(1, vs.Length).Where(id => Comparer<T>.Default.Compare(vs[id - 1], v) <= 0);
                CollectionAssert.AreEquivalent(expectedIds.ToArray(), index.LessThan(v, true).ToArray(), $"{type}: ids up to {v}");
            }
            // an exclusive range between the two extremes drops exactly the extremes
            var inner = index.RangeSearch(sorted[0], sorted[^1], false, false).ToArray();
            var expectedInner = Enumerable.Range(1, vs.Length)
                .Where(id => Comparer<T>.Default.Compare(vs[id - 1], sorted[0]) > 0 && Comparer<T>.Default.Compare(vs[id - 1], sorted[^1]) < 0);
            CollectionAssert.AreEquivalent(expectedInner.ToArray(), inner, $"{type}: exclusive range");
        });
    }

    // ---- types that were already supported (regression cover for the encoding changes) ----

    [TestMethod]
    public void Boolean() => assertRoundTrip(PropertyType.Boolean, true, false, true);

    [TestMethod]
    public void Integer() {
        int[] values = [0, -1, 7, int.MinValue, int.MaxValue, -1000];
        assertRoundTrip(PropertyType.Integer, values);
        assertOrdering(PropertyType.Integer, values);
    }

    [TestMethod]
    public void Double() {
        double[] values = [0d, -0.5d, 3.25d, double.MinValue, double.MaxValue];
        assertRoundTrip(PropertyType.Double, values);
        assertOrdering(PropertyType.Double, values);
    }

    [TestMethod]
    public void Float() {
        float[] values = [0f, -0.5f, 3.25f, float.MinValue, float.MaxValue];
        assertRoundTrip(PropertyType.Float, values);
        assertOrdering(PropertyType.Float, values);
    }

    [TestMethod]
    public void String() {
        // sqlite's BINARY collation is a byte-wise memcmp, which Comparer<string> is not, so only
        // the round-trip half of the contract holds for strings. The pair below is the case that
        // makes the difference visible: culture order is a < B, byte order is B < a, so a MaxCount
        // that compared the index MAX ("a") with Comparer<string> would rule out "B" - which is in
        // the index - as being past the end of it.
        assertRoundTrip(PropertyType.String, "", "a", "B", "æøå", "a longer sentence");
        assertRoundTrip(PropertyType.String, "a", "B");
    }

    [TestMethod]
    public void DateTime_() {
        // one kind at a time: "O" sorts chronologically, and mixed kinds are compared by
        // wall-clock ticks in sqlite exactly as Comparer<DateTime> compares them
        DateTime[] values = [
            new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc),
            new(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1),
        ];
        assertRoundTrip(PropertyType.DateTime, values);
        assertOrdering(PropertyType.DateTime, values);
        // the kind survives, so re-encoding a value read back from the index yields the same text
        withIndex<DateTime>(PropertyType.DateTime, values, (index, vs) => {
            Assert.AreEqual(DateTimeKind.Utc, index.GetValue(1).Kind);
        });
    }

    [TestMethod]
    public void GeoCoordinate_() {
        GeoCoordinate[] values = [
            new(59.9139, 10.7522),   // oslo
            new(-33.8688, 151.2093), // sydney
            new(0, 0),
            new(90, 180),
            new(-90, -180),
        ];
        assertRoundTrip(PropertyType.GeoCoordinate, values);
        assertOrdering(PropertyType.GeoCoordinate, values);
    }

    // ---- newly supported types ----

    [TestMethod]
    public void Long() {
        long[] values = [0L, -1L, 7L, long.MinValue, long.MaxValue, -1_000_000_000_000L];
        assertRoundTrip(PropertyType.Long, values);
        assertOrdering(PropertyType.Long, values);
    }

    [TestMethod]
    public void TimeSpan_() {
        TimeSpan[] values = [
            System.TimeSpan.Zero,
            System.TimeSpan.FromMinutes(90),
            System.TimeSpan.FromTicks(-1),
            System.TimeSpan.MinValue,
            System.TimeSpan.MaxValue,
            new(1, 2, 3, 4, 5),
        ];
        assertRoundTrip(PropertyType.TimeSpan, values);
        assertOrdering(PropertyType.TimeSpan, values);
    }

    [TestMethod]
    public void Decimal() {
        decimal[] values = [
            0m, 1m, -1m, 0.5m, -0.5m,
            decimal.MinValue, decimal.MaxValue,
            0.0000000000000000000000000001m,  // smallest positive (scale 28)
            -0.0000000000000000000000000001m,
            9m, 10m,                          // lexical text order would put "10" before "9"
            -9m, -10m,
            1234567890.12345m,
        ];
        assertRoundTrip(PropertyType.Decimal, values);
        assertOrdering(PropertyType.Decimal, values);
    }

    [TestMethod]
    public void Decimal_ScaleIsNormalisedNotValue() {
        // the fixed-point encoding pads to 28 decimals, so trailing-zero scale is not preserved;
        // the value itself must be unchanged, and both spellings must land on the same row
        withIndex<decimal>(PropertyType.Decimal, [1.50m], (index, _) => {
            Assert.AreEqual(1.5m, index.GetValue(1));
            Assert.IsTrue(index.ContainsValue(1.500m));
            CollectionAssert.AreEqual(new[] { 1 }, index.GetIds(1.5m).ToArray());
        });
    }

    [TestMethod]
    public void DateTimeOffset_() {
        DateTimeOffset[] values = [
            new(2026, 8, 6, 12, 0, 0, TimeSpan.FromHours(2)),
            new(2026, 8, 6, 12, 0, 0, TimeSpan.FromHours(-8)), // later instant, same local time
            System.DateTimeOffset.MinValue,
            System.DateTimeOffset.MaxValue,
            new(2026, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(330)), // +05:30
        ];
        assertRoundTrip(PropertyType.DateTimeOffset, values);
        assertOrdering(PropertyType.DateTimeOffset, values);
        // only the instant is stored, so a value written at +02:00 reads back at zero offset - a
        // DateTimeOffset that is == to it - and the two spellings are one value to the index
        withIndex<DateTimeOffset>(PropertyType.DateTimeOffset, values, (index, vs) => {
            var written = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.FromHours(2));
            var sameInstant = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
            Assert.AreEqual(written, index.GetValue(1));
            Assert.AreEqual(TimeSpan.Zero, index.GetValue(1).Offset);
            Assert.IsTrue(index.ContainsValue(sameInstant));
            CollectionAssert.AreEqual(new[] { 1 }, index.GetIds(sameInstant).ToArray());
        });
    }

    [TestMethod]
    public void Guid_() {
        assertRoundTrip(PropertyType.Guid, guids());
        // Comparer<Guid> compares the leading fields as unsigned, which is the order the lowercase
        // "D" text is in too, so guids order the same way in sqlite as they do in .NET
        assertOrdering(PropertyType.Guid, guids());
    }

    [TestMethod]
    public void Reference() {
        // a reference is queried by equality only, but it is stored and ordered like any guid
        assertRoundTrip(PropertyType.Reference, guids());
        assertOrdering(PropertyType.Reference, guids());
    }

    /// <summary>Guids spanning the sign bit of every leading field, plus a realistic one.</summary>
    static Guid[] guids() => [
        Guid.Empty,
        new("ffffffff-ffff-ffff-ffff-ffffffffffff"),
        new("00000001-0000-0000-0000-000000000000"),
        new("7fffffff-ffff-ffff-ffff-ffffffffffff"),
        new("80000000-0000-0000-0000-000000000000"),
        new("0000ffff-8000-8000-8000-000000000001"),
        Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"),
    ];

    [TestMethod]
    public void UnsupportedTypeIsRejectedWithItsName() {
        var dir = tempDir();
        try {
            using var store = new SqliteIndexStore(dir);
            var ex = Assert.ThrowsException<NotSupportedException>(() =>
                store.OpenValueIndex<byte[]>(new SetRegister(100), Guid.NewGuid().ToString(), "bytes", PropertyType.ByteArray));
            StringAssert.Contains(ex.Message, "ByteArray");
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public void ResetAllRecreatesTablesForEveryType() {
        // ResetAllDataCore rebuilds the value tables from the same type map, so a type that is
        // only reachable through getSqlType must survive a reset too
        var dir = tempDir();
        try {
            using var store = new SqliteIndexStore(dir);
            var sets = new SetRegister(100);
            var reference = store.OpenValueIndex<Guid>(sets, "ref", "reference", PropertyType.Reference);
            var amount = store.OpenValueIndex<decimal>(sets, "amt", "amount", PropertyType.Decimal);
            var duration = store.OpenValueIndex<TimeSpan>(sets, "dur", "duration", PropertyType.TimeSpan);
            var target = Guid.NewGuid();
            store.BeginTransaction();
            reference.Add(1, target);
            amount.Add(1, 12.34m);
            duration.Add(1, System.TimeSpan.FromSeconds(90));
            store.CommitTransaction(1);

            store.ResetAll();
            Assert.AreEqual(0, reference.IdCount);
            Assert.AreEqual(0, store.GetTimestamp());

            store.BeginTransaction();
            reference.Add(2, target);
            amount.Add(2, -0.75m);
            duration.Add(2, System.TimeSpan.FromTicks(-1));
            store.CommitTransaction(2);
            Assert.AreEqual(target, reference.GetValue(2));
            Assert.AreEqual(-0.75m, amount.GetValue(2));
            Assert.AreEqual(System.TimeSpan.FromTicks(-1), duration.GetValue(2));
            Assert.AreEqual(2, store.GetTimestamp());
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
