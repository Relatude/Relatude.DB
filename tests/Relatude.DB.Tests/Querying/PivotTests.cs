using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;
using Relatude.Utils;

namespace Relatude.Querying;

#region pivot test datamodel
public enum PivotChannel { Web = 0, Store = 1, Phone = 2 }
[Node]
public class PivotOrder {
    [InternalIdProperty]
    public int Id { get; set; }
    [PublicIdProperty]
    public Guid PId { get; set; } // relations need the same id kind on both ends: a Guid, like the customer
    [StringProperty(Indexed = true)]
    public string Region { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public PivotChannel Channel { get; set; }
    [DecimalProperty(Indexed = true)]
    public decimal Amount { get; set; }
    [IntegerProperty(Indexed = true)]
    public int Quantity { get; set; }
    [DateTimeProperty(Indexed = true)]
    public DateTime OrderDate { get; set; }
    [StringArrayProperty(Indexed = true)]
    public string[] Tags { get; set; } = [];
    [StringProperty(Indexed = true)]
    public string Note { get; set; } = "";
    [BooleanProperty(Indexed = true)]
    public bool Express { get; set; }
    [RelationProperty(Facet = true)]
    public PivotCustomer? Customer { get; set; }
}
[Node]
public class PivotBigOrder : PivotOrder {
    [IntegerProperty(Indexed = true)]
    public int Pallets { get; set; }
}
[Node]
public class PivotCustomer {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)]
    public string Name { get; set; } = "";
    [RelationProperty(Facet = true)]
    public IEnumerable<PivotOrder>? Orders { get; set; }
}
#endregion

[TestClass]
public class PivotTests {

    static readonly string[] _regions = ["North", "South", "East", "West"];

    // every order knows its customer through this map (null = no customer), so LINQ truths can be computed
    static NodeStore OpenStore(out List<PivotOrder> all, out List<PivotCustomer> customers, out Dictionary<Guid, PivotCustomer?> customerOf, bool persistedIndexes = false) {
        var dm = new Datamodel();
        dm.Add<PivotOrder>(autoDeduceRelations: true);
        dm.Add<PivotBigOrder>(autoDeduceRelations: true);
        dm.Add<PivotCustomer>(autoDeduceRelations: true);
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            }, null, null, null, null, null, () => DB.DataStores.Indexes.IndexEngines.Single(TestEngines.ValueId, new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(null))))
            : new NodeStore(DataStoreLocal.Open(dm));
        customers = [
            new PivotCustomer { Id = Guid.NewGuid(), Name = "Acme" },
            new PivotCustomer { Id = Guid.NewGuid(), Name = "Globex" },
            new PivotCustomer { Id = Guid.NewGuid(), Name = "Initech" },
        ];
        store.Insert(customers);
        all = [];
        for (var i = 1; i <= 90; i++) {
            var order = i <= 80 ? new PivotOrder() : new PivotBigOrder { Pallets = i % 3 + 1 };
            order.PId = Guid.NewGuid();
            order.Region = _regions[i % 4];
            order.Channel = (PivotChannel)(i % 3);
            order.Amount = (i % 13) * 10.5m + 1; // 1 .. 127, boundaries in the tests avoid these values
            order.Quantity = i % 7 + 1;
            order.OrderDate = new DateTime(2024, 1, 15).AddDays(i * 9); // spans a bit more than two years
            order.Tags = i % 5 == 0 ? ["gift", "bulk"] : i % 2 == 0 ? ["gift"] : ["plain"];
            order.Note = "note " + i;
            order.Express = i % 4 == 0;
            all.Add(order);
        }
        store.Insert(all);
        customerOf = new Dictionary<Guid, PivotCustomer?>();
        for (var i = 1; i <= 90; i++) {
            var order = all[i - 1];
            if (i % 10 == 0) { customerOf[order.PId] = null; continue; } // every 10th order has no customer
            var customer = customers[i % 3];
            store.AddRelation(order, o => o.Customer, customer);
            customerOf[order.PId] = customer;
        }
        return store;
    }
    static PivotGroup GroupOf(PivotAxisResult axis, string displayName) {
        var g = axis.Groups.FirstOrDefault(g => g.DisplayName == displayName);
        Assert.IsNotNull(g, "Missing group " + displayName + ". Groups: " + string.Join(", ", axis.Groups.Select(x => x.DisplayName)));
        return g;
    }
    static int IndexOf(PivotAxisResult axis, string displayName) => Array.IndexOf(axis.Groups, GroupOf(axis, displayName));

    [TestMethod]
    public void Count_RowsByColumns_MatchesLinq() {
        var store = OpenStore(out var all, out _, out _);
        var pivot = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Region)
            .AddColumn(o => o.Channel)
            .AddCount()
            .Execute();
        Assert.AreEqual(all.Count, pivot.SourceCount);
        Assert.AreEqual(1, pivot.Rows.Levels.Length);
        Assert.AreEqual("Region", pivot.Rows.Levels[0].CodeName);
        CollectionAssert.AreEquivalent(_regions, pivot.Rows.Groups.Select(g => g.DisplayName).ToArray());
        // enum buckets show their names, and carry the int value
        CollectionAssert.AreEquivalent(new[] { "Web", "Store", "Phone" }, pivot.Columns.Groups.Select(g => g.DisplayName).ToArray());
        Assert.IsTrue(pivot.Columns.Groups.All(g => g.Values[0] is int));
        foreach (var region in _regions) {
            foreach (var channel in Enum.GetValues<PivotChannel>()) {
                var expected = all.Count(o => o.Region == region && o.Channel == channel);
                var cell = pivot[IndexOf(pivot.Rows, region), IndexOf(pivot.Columns, channel.ToString())];
                if (expected == 0) { Assert.IsNull(cell); continue; }
                Assert.IsNotNull(cell);
                Assert.AreEqual(expected, cell.Count);
                Assert.AreEqual(expected, cell.Get("Count"));
            }
        }
        // rows come back in natural (alphabetical) order when no sort is asked for
        CollectionAssert.AreEqual(_regions.OrderBy(r => r, StringComparer.Ordinal).ToArray(), pivot.Rows.Groups.Select(g => g.DisplayName).ToArray());
        Assert.AreEqual(all.Count, pivot.GrandTotal.Count);
        Assert.AreEqual(all.Count, pivot.RowTotals.Sum(t => t.Count));
        Assert.AreEqual(all.Count, pivot.ColumnTotals.Sum(t => t.Count));
        Assert.IsFalse(pivot.Capped);
    }

    [TestMethod]
    public void Measures_SumAverageMinMaxDistinct_MatchLinq() {
        foreach (var persisted in new[] { false, true }) {
            var store = OpenStore(out var all, out _, out _, persistedIndexes: persisted);
            var pivot = store.Query<PivotOrder>().Pivot()
                .AddRow(o => o.Region)
                .AddSum(o => o.Amount, "revenue")
                .AddAverage(o => o.Amount)
                .AddMin(o => o.Quantity)
                .AddMax(o => o.Quantity)
                .AddCountDistinct(o => o.Channel, "channels")
                .Execute();
            CollectionAssert.AreEqual(new[] { "revenue", "Amount.Average", "Quantity.Min", "Quantity.Max", "channels" }, pivot.Measures.Select(m => m.Name).ToArray());
            // no column grouping: one "(all)" column, so every row has exactly one cell equal to its total
            Assert.AreEqual(1, pivot.Columns.Groups.Length);
            Assert.AreEqual(0, pivot.Columns.Groups[0].Depth);
            Assert.AreEqual(0, pivot.Columns.Levels.Length);
            foreach (var g in all.GroupBy(o => o.Region)) {
                var r = IndexOf(pivot.Rows, g.Key);
                var cell = pivot[r, 0];
                Assert.IsNotNull(cell, "persisted=" + persisted);
                Assert.AreEqual(g.Count(), cell.Count);
                Assert.AreEqual((double)g.Sum(o => o.Amount), cell.Get("revenue")!.Value, 1e-9);
                Assert.AreEqual((double)g.Average(o => o.Amount), cell.Get("Amount.Average")!.Value, 1e-9);
                Assert.AreEqual(g.Min(o => o.Quantity), cell.Get("Quantity.Min"));
                Assert.AreEqual(g.Max(o => o.Quantity), cell.Get("Quantity.Max"));
                Assert.AreEqual(g.Select(o => o.Channel).Distinct().Count(), cell.Get("channels"));
                var total = pivot.RowTotals[r];
                CollectionAssert.AreEqual(cell.Values, total.Values);
            }
            Assert.AreEqual((double)all.Sum(o => o.Amount), pivot.GrandTotal.Get("revenue")!.Value, 1e-9);
            Assert.AreEqual((double)all.Average(o => o.Amount), pivot.GrandTotal.Get("Amount.Average")!.Value, 1e-9);
            store.Dispose();
        }
    }

    [TestMethod]
    public void Totals_AreAggregatedOverSets_NotAddedFromCells() {
        var store = OpenStore(out var all, out _, out _);
        // an array-valued row property puts a node with two tags in two rows: the cells over-count, the totals must not
        var pivot = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Tags)
            .AddColumn(o => o.Channel)
            .AddCount()
            .AddAverage(o => o.Amount, "avg")
            .Execute();
        Assert.IsTrue(pivot.Cells.Sum(c => c.Count) > all.Count, "a node in two tag groups must be counted in both cells");
        Assert.AreEqual(all.Count, pivot.GrandTotal.Count);
        Assert.AreEqual((double)all.Average(o => o.Amount), pivot.GrandTotal.Get("avg")!.Value, 1e-9);
        foreach (var tag in new[] { "gift", "bulk", "plain" }) {
            var withTag = all.Where(o => o.Tags.Contains(tag)).ToList();
            var total = pivot.RowTotals[IndexOf(pivot.Rows, tag)];
            Assert.AreEqual(withTag.Count, total.Count);
            Assert.AreEqual((double)withTag.Average(o => o.Amount), total.Get("avg")!.Value, 1e-9);
        }
        foreach (var channel in Enum.GetValues<PivotChannel>()) {
            var inChannel = all.Where(o => o.Channel == channel).ToList();
            var total = pivot.ColumnTotals[IndexOf(pivot.Columns, channel.ToString())];
            Assert.AreEqual(inChannel.Count, total.Count);
            Assert.AreEqual((double)inChannel.Average(o => o.Amount), total.Get("avg")!.Value, 1e-9);
        }
        // totals can be switched off
        var noTotals = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddCount().SetTotals(rows: false, columns: false).Execute();
        Assert.AreEqual(0, noTotals.RowTotals.Length);
        Assert.AreEqual(0, noTotals.ColumnTotals.Length);
        Assert.AreEqual(all.Count, noTotals.GrandTotal.Count); // the grand total is always there
    }

    [TestMethod]
    public void NestedRows_LeavesAndSubTotals() {
        var store = OpenStore(out var all, out _, out _);
        var pivot = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Region)
            .AddRow(o => o.Express)
            .AddColumn(o => o.Channel)
            .AddSum(o => o.Quantity, "qty")
            .SetTotals(subTotals: true)
            .Execute();
        var expectedLeaves = all.GroupBy(o => (o.Region, o.Express)).ToList();
        Assert.AreEqual(expectedLeaves.Count, pivot.Rows.Groups.Length);
        Assert.AreEqual(2, pivot.Rows.Levels.Length);
        foreach (var leaf in expectedLeaves) {
            var g = pivot.Rows.Groups.Single(g => Equals(g.Values[0], leaf.Key.Region) && Equals(g.Values[1], leaf.Key.Express));
            Assert.AreEqual(2, g.Depth);
            Assert.AreEqual(leaf.Count(), g.Count);
            Assert.AreEqual(leaf.Key.Region + " / " + leaf.Key.Express, g.DisplayName);
            var r = Array.IndexOf(pivot.Rows.Groups, g);
            foreach (var channel in Enum.GetValues<PivotChannel>()) {
                var inCell = leaf.Where(o => o.Channel == channel).ToList();
                var cell = pivot[r, IndexOf(pivot.Columns, channel.ToString())];
                if (inCell.Count == 0) { Assert.IsNull(cell); continue; }
                Assert.AreEqual(inCell.Sum(o => o.Quantity), cell!.Get("qty"));
            }
        }
        // leaves are grouped under their parent, parents in natural order
        var parentOrder = pivot.Rows.Groups.Select(g => (string)g.Values[0]!).Distinct().ToArray();
        CollectionAssert.AreEqual(_regions.OrderBy(r => r, StringComparer.Ordinal).ToArray(), parentOrder);
        // one sub-total per region, with cells per channel and a total over the region
        Assert.AreEqual(_regions.Length, pivot.RowSubTotals.Length);
        foreach (var region in _regions) {
            var sub = pivot.RowSubTotals.Single(s => s.Group.DisplayName == region);
            Assert.AreEqual(1, sub.Group.Depth);
            var inRegion = all.Where(o => o.Region == region).ToList();
            Assert.AreEqual(inRegion.Count, sub.Total.Count);
            Assert.AreEqual(inRegion.Sum(o => o.Quantity), sub.Total.Get("qty"));
            Assert.AreEqual(pivot.Columns.Groups.Length, sub.Cells.Length);
            foreach (var channel in Enum.GetValues<PivotChannel>()) {
                var expected = inRegion.Where(o => o.Channel == channel).Sum(o => o.Quantity);
                Assert.AreEqual(expected, sub.Cells[IndexOf(pivot.Columns, channel.ToString())]!.Get("qty"));
            }
        }
        Assert.AreEqual(0, pivot.ColumnSubTotals.Length); // one column level: nothing to sub-total
    }

    [TestMethod]
    public void DateIntervals_BucketByCalendar() {
        var store = OpenStore(out var all, out _, out _);
        var byYear = store.Query<PivotOrder>().Pivot().AddRow(o => o.OrderDate, DateInterval.Year).AddCount().Execute();
        var years = all.GroupBy(o => o.OrderDate.Year).OrderBy(g => g.Key).ToList();
        CollectionAssert.AreEqual(years.Select(g => g.Key.ToString()).ToArray(), byYear.Rows.Groups.Select(g => g.DisplayName).ToArray(),
            "years: " + string.Join(", ", byYear.Rows.Groups.Select(g => g.DisplayName + "=" + g.Count + " [" + g.Values[0] + " .. " + g.Values2[0] + "]")));
        CollectionAssert.AreEqual(years.Select(g => g.Count()).ToArray(), byYear.Rows.Groups.Select(g => g.Count).ToArray());
        Assert.IsTrue(byYear.Rows.Levels[0].IsRange);
        Assert.AreEqual(DateInterval.Year, byYear.Rows.Levels[0].Interval);
        Assert.AreEqual(new DateTime(years[0].Key, 1, 1), byYear.Rows.Groups[0].Values[0]);   // bucket start
        Assert.AreEqual(new DateTime(years[0].Key + 1, 1, 1), byYear.Rows.Groups[0].Values2[0]); // exclusive end

        var byQuarter = store.Query<PivotOrder>().Pivot().AddRow(o => o.OrderDate, DateInterval.Quarter).AddCount().Execute();
        var quarters = all.GroupBy(o => (o.OrderDate.Year, Q: (o.OrderDate.Month - 1) / 3 + 1)).OrderBy(g => g.Key).ToList();
        CollectionAssert.AreEqual(quarters.Select(g => g.Key.Year + " Q" + g.Key.Q).ToArray(), byQuarter.Rows.Groups.Select(g => g.DisplayName).ToArray());
        CollectionAssert.AreEqual(quarters.Select(g => g.Count()).ToArray(), byQuarter.Rows.Groups.Select(g => g.Count).ToArray());

        var byMonth = store.Query<PivotOrder>().Pivot().AddColumn(o => o.OrderDate, DateInterval.Month).AddSum(o => o.Amount).Execute();
        var months = all.GroupBy(o => new DateTime(o.OrderDate.Year, o.OrderDate.Month, 1)).OrderBy(g => g.Key).ToList();
        CollectionAssert.AreEqual(months.Select(g => g.Key.ToString("yyyy-MM")).ToArray(), byMonth.Columns.Groups.Select(g => g.DisplayName).ToArray());
        for (var i = 0; i < months.Count; i++) Assert.AreEqual((double)months[i].Sum(o => o.Amount), byMonth[0, i]!.Get("Amount.Sum")!.Value, 1e-9);

        var byWeek = store.Query<PivotOrder>().Pivot().AddRow(o => o.OrderDate, DateInterval.Week).AddCount().Execute();
        var weeks = all.GroupBy(o => (System.Globalization.ISOWeek.GetYear(o.OrderDate), System.Globalization.ISOWeek.GetWeekOfYear(o.OrderDate))).ToList();
        Assert.AreEqual(weeks.Count, byWeek.Rows.Groups.Length);
        Assert.IsTrue(byWeek.Rows.Groups.All(g => ((DateTime)g.Values[0]!).DayOfWeek == DayOfWeek.Monday), "ISO weeks start on Monday");

        Assert.ThrowsException<ArgumentException>(() => store.Query<PivotOrder>().Pivot().AddRow(o => o.Region, DateInterval.Month));
    }

    [TestMethod]
    public void Ranges_ExplicitAndAutomatic() {
        var store = OpenStore(out var all, out _, out _);
        var explicitRanges = store.Query<PivotOrder>().Pivot()
            .AddRowRange(o => o.Amount, 0m, 60m, "low")
            .AddRowRange(o => o.Amount, 60m, 200m, "high")
            .AddColumn(o => o.Region)
            .AddCount()
            .Execute();
        Assert.AreEqual(1, explicitRanges.Rows.Levels.Length, "consecutive ranges on one property form one level");
        Assert.IsTrue(explicitRanges.Rows.Levels[0].IsRange);
        CollectionAssert.AreEqual(new[] { "low", "high" }, explicitRanges.Rows.Groups.Select(g => g.DisplayName).ToArray());
        Assert.AreEqual(all.Count(o => o.Amount <= 60m), explicitRanges.Rows.Groups[0].Count);
        Assert.AreEqual(all.Count(o => o.Amount > 60m), explicitRanges.Rows.Groups[1].Count);
        foreach (var region in _regions) {
            var c = explicitRanges[0, IndexOf(explicitRanges.Columns, region)];
            Assert.AreEqual(all.Count(o => o.Amount <= 60m && o.Region == region), c?.Count ?? 0);
        }

        var auto = store.Query<PivotOrder>().Pivot().AddRowRanges(o => o.Amount, 4).AddCount().Execute();
        Assert.IsTrue(auto.Rows.Levels[0].IsRange);
        Assert.IsTrue(auto.Rows.Groups.Length is >= 2 and <= 5, "about 4 buckets expected, got " + auto.Rows.Groups.Length);
        Assert.AreEqual(all.Count, auto.Rows.Groups.Sum(g => g.Count), "ranges cover every value exactly once");
        Assert.IsTrue(auto.Rows.Groups.All(g => g.Values2[0] != null));

        // AddRow on a numeric property with few distinct values gives value buckets, on many distinct values ranges
        var quantity = store.Query<PivotOrder>().Pivot().AddRow(o => o.Quantity).AddCount().Execute();
        Assert.IsFalse(quantity.Rows.Levels[0].IsRange);
        Assert.AreEqual(7, quantity.Rows.Groups.Length);
        var forcedValues = store.Query<PivotOrder>().Pivot().AddRowValues(o => o.Amount).AddCount().Execute();
        Assert.IsFalse(forcedValues.Rows.Levels[0].IsRange);
        Assert.AreEqual(all.Select(o => o.Amount).Distinct().Count(), forcedValues.Rows.Groups.Length);
    }

    [TestMethod]
    public void Options_SortTrimAndOther() {
        var store = OpenStore(out var all, out _, out _);
        var byRevenue = all.GroupBy(o => o.Region).OrderByDescending(g => g.Sum(o => o.Amount)).Select(g => g.Key).ToArray();
        var sorted = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Region)
            .AddSum(o => o.Amount, "revenue")
            .SetRowOptions(o => o.Region, sortByMeasure: "revenue")
            .Execute();
        CollectionAssert.AreEqual(byRevenue, sorted.Rows.Groups.Select(g => g.DisplayName).ToArray());

        var ascending = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Region)
            .AddSum(o => o.Amount, "revenue")
            .SetRowOptions(o => o.Region, sortByMeasure: "revenue", descending: false)
            .Execute();
        CollectionAssert.AreEqual(byRevenue.Reverse().ToArray(), ascending.Rows.Groups.Select(g => g.DisplayName).ToArray());

        // "Count" sorts by the node count even without a Count measure
        var byCount = all.GroupBy(o => o.Region).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToArray();
        var countSorted = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddSum(o => o.Amount).SetRowOptions(o => o.Region, sortByMeasure: "Count").Execute();
        CollectionAssert.AreEqual(byCount, countSorted.Rows.Groups.Select(g => g.DisplayName).ToArray());

        // top 2 by revenue, the rest in one "(other)" group aggregated over the union of the trimmed groups
        var trimmed = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Region)
            .AddColumn(o => o.Channel)
            .AddSum(o => o.Amount, "revenue")
            .SetRowOptions(o => o.Region, maxGroups: 2, sortByMeasure: "revenue", otherGroup: true)
            .Execute();
        Assert.AreEqual(3, trimmed.Rows.Groups.Length);
        CollectionAssert.AreEqual(byRevenue.Take(2).ToArray(), trimmed.Rows.Groups.Take(2).Select(g => g.DisplayName).ToArray());
        var other = trimmed.Rows.Groups[2];
        Assert.IsTrue(other.IsOther);
        Assert.AreEqual("(other)", other.DisplayName);
        var rest = all.Where(o => !byRevenue.Take(2).Contains(o.Region)).ToList();
        Assert.AreEqual(rest.Count, other.Count);
        Assert.AreEqual((double)rest.Sum(o => o.Amount), trimmed.RowTotals[2].Get("revenue")!.Value, 1e-9);
        Assert.AreEqual((double)rest.Where(o => o.Channel == PivotChannel.Web).Sum(o => o.Amount), trimmed[2, IndexOf(trimmed.Columns, "Web")]!.Get("revenue")!.Value, 1e-9);

        // without otherGroup the trimmed groups are simply gone
        var top2 = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddCount().SetRowOptions(o => o.Region, maxGroups: 2).Execute();
        Assert.AreEqual(2, top2.Rows.Groups.Length);
        Assert.AreEqual(all.Count, top2.GrandTotal.Count, "the grand total is over the source, not the shown groups");

        // minCount
        var minCount = store.Query<PivotOrder>().Pivot().AddRow(o => o.Quantity).AddCount().SetRowOptions(o => o.Quantity, minCount: 13).Execute();
        var bigEnough = all.GroupBy(o => o.Quantity).Where(g => g.Count() >= 13).Count();
        Assert.AreEqual(bigEnough, minCount.Rows.Groups.Length);
        Assert.IsTrue(minCount.Rows.Groups.All(g => g.Count >= 13));

        Assert.ThrowsException<Exception>(() => store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddCount().SetRowOptions(o => o.Region, sortByMeasure: "nope").Execute());
        Assert.ThrowsException<ArgumentException>(() => store.Query<PivotOrder>().Pivot().SetRowOptions(o => o.Region, maxGroups: 2), "options before the group");
    }

    [TestMethod]
    public void IncludeMissing_BucketForNodesWithoutAValue() {
        var store = OpenStore(out var all, out _, out _);
        // Pallets exists on the subtype only: every plain order lacks a value
        var pivot = store.Query<PivotOrder>().Pivot()
            .AddRowValues<PivotBigOrder>(o => o.Pallets)
            .SetRowOptions<PivotBigOrder>(o => o.Pallets, includeMissing: true)
            .AddCount()
            .Execute();
        var big = all.OfType<PivotBigOrder>().ToList();
        var missing = pivot.Rows.Groups.Single(g => g.Values[0] == null);
        Assert.AreEqual("(none)", missing.DisplayName);
        Assert.IsFalse(missing.IsOther);
        Assert.AreEqual(all.Count - big.Count, missing.Count);
        Assert.AreEqual(big.Select(b => b.Pallets).Distinct().Count() + 1, pivot.Rows.Groups.Length);
        Assert.AreEqual(all.Count, pivot.Rows.Groups.Sum(g => g.Count));
        // and without it, only the nodes with a value are grouped - the grand total still spans the source
        var without = store.Query<PivotOrder>().Pivot().AddRowValues<PivotBigOrder>(o => o.Pallets).AddCount().Execute();
        Assert.AreEqual(big.Count, without.Rows.Groups.Sum(g => g.Count));
        Assert.AreEqual(all.Count, without.GrandTotal.Count);
    }

    [TestMethod]
    public void RelationGroups_AreMappedNodesWithDisplayNames() {
        var store = OpenStore(out var all, out var customers, out var customerOf);
        var pivot = store.Query<PivotOrder>().Pivot()
            .AddRow(o => o.Customer)
            .AddColumn(o => o.Region)
            .AddCount()
            .AddSum(o => o.Amount, "revenue")
            .Execute();
        Assert.AreEqual(customers.Count, pivot.Rows.Groups.Length);
        Assert.IsTrue(pivot.Rows.Groups.All(g => g.Values[0] is PivotCustomer), "group values must be mapped node objects at the NodeStore layer");
        foreach (var customer in customers) {
            var g = pivot.Rows.Groups.Single(g => ((PivotCustomer)g.Values[0]!).Id == customer.Id);
            Assert.AreEqual(customer.Name, g.DisplayName);
            var orders = all.Where(o => customerOf[o.PId]?.Id == customer.Id).ToList();
            Assert.AreEqual(orders.Count, g.Count);
            var r = Array.IndexOf(pivot.Rows.Groups, g);
            Assert.AreEqual((double)orders.Sum(o => o.Amount), pivot.RowTotals[r].Get("revenue")!.Value, 1e-9);
            foreach (var region in _regions) {
                var expected = orders.Count(o => o.Region == region);
                Assert.AreEqual(expected, pivot[r, IndexOf(pivot.Columns, region)]?.Count ?? 0);
            }
        }
        Assert.AreEqual(all.Count, pivot.GrandTotal.Count, "orders without a customer are in no group but in the total");
        // the JSON path maps them too
        var json = store.Query<PivotOrder>().Pivot().AddRow(o => o.Customer).AddCount().EvaluateForJson() as PivotResult;
        Assert.IsNotNull(json);
        Assert.IsTrue(json.Rows.Groups.All(g => g.Values[0] is PivotCustomer));
    }

    [TestMethod]
    public void PivotAfterFacetSelection_UsesTheSelectionAsFilter() {
        var store = OpenStore(out var all, out _, out _);
        var north = all.Where(o => o.Region == "North").ToList();
        var pivot = store.Query<PivotOrder>()
            .Facets()
            .AddValueFacet(o => o.Region)
            .SetFacetValue(o => o.Region, "North")
            .Page(0, 5) // the facet page must not limit what is pivoted
            .Pivot()
            .AddRow(o => o.Channel)
            .AddCount()
            .AddSum(o => o.Amount, "revenue")
            .Execute();
        Assert.AreEqual(north.Count, pivot.SourceCount);
        Assert.AreEqual(north.Count, pivot.GrandTotal.Count);
        foreach (var channel in Enum.GetValues<PivotChannel>()) {
            var inChannel = north.Where(o => o.Channel == channel).ToList();
            var g = GroupOf(pivot.Rows, channel.ToString());
            Assert.AreEqual(inChannel.Count, g.Count);
            Assert.AreEqual((double)inChannel.Sum(o => o.Amount), pivot.RowTotals[IndexOf(pivot.Rows, channel.ToString())].Get("revenue")!.Value, 1e-9);
        }
        // two selections combine (AND across properties), and a range selection works too
        var narrow = store.Query<PivotOrder>().Facets()
            .SetFacetValue(o => o.Region, "North")
            .SetFacetRangeValue(o => o.Amount, 0m, 60m)
            .Pivot().AddRow(o => o.Express).AddCount().Execute();
        Assert.AreEqual(north.Count(o => o.Amount <= 60m), narrow.SourceCount);
        // the query string names the facet selection and the pivot, but not the facet page
        var text = store.Query<PivotOrder>().Facets().SetFacetValue(o => o.Region, "North").Page(0, 5).Pivot().AddRow(o => o.Channel).AddCount().ToString();
        StringAssert.Contains(text, ".SetFacetValue(");
        StringAssert.Contains(text, ".Pivot().AddRow(");
        Assert.IsFalse(text.Contains(".Page("), text);
    }

    [TestMethod]
    public void QueryString_RoundTripsAndParsesByName() {
        var store = OpenStore(out var all, out _, out _);
        var typed = store.Query<PivotOrder>().Where("o => o.Quantity > 2").Pivot() // a literal filter: the string is re-run without the typed query's parameters
            .AddRow(o => o.Region)
            .AddRow(o => o.OrderDate, DateInterval.Year)
            .AddColumnValues(o => o.Channel)
            .AddCount("n")
            .AddSum(o => o.Amount, "revenue")
            .AddAverage(o => o.Quantity)
            .SetRowOptions(o => o.Region, maxGroups: 3, sortByMeasure: "revenue", otherGroup: true)
            .SetTotals(rows: true, columns: false, subTotals: true)
            .SetLimits(1000)
            .SetRowPaging(0, 10);
        var text = typed.ToString();
        StringAssert.Contains(text, ".Pivot().AddRow(");
        StringAssert.Contains(text, "\"Year\")");
        StringAssert.Contains(text, ".AddColumnValues(");
        StringAssert.Contains(text, ".SetRowOptions(");
        StringAssert.Contains(text, ".SetTotals(true, false, true)");
        StringAssert.Contains(text, ".SetLimits(1000, false)");
        StringAssert.Contains(text, ".SetRowPaging(0, 10)");
        var expected = typed.Execute();
        // the same string, parsed and run by the store, gives the same table
        var parsed = store.Datastore.Query(text, [], null) as PivotQueryResultData;
        Assert.IsNotNull(parsed, "the store must answer a pivot query string with pivot data");
        var actual = parsed.Result;
        CollectionAssert.AreEqual(expected.Measures.Select(m => m.Name).ToArray(), actual.Measures.Select(m => m.Name).ToArray());
        CollectionAssert.AreEqual(expected.Rows.Groups.Select(g => g.DisplayName).ToArray(), actual.Rows.Groups.Select(g => g.DisplayName).ToArray());
        CollectionAssert.AreEqual(expected.Columns.Groups.Select(g => g.DisplayName).ToArray(), actual.Columns.Groups.Select(g => g.DisplayName).ToArray());
        Assert.AreEqual(expected.Cells.Length, actual.Cells.Length);
        for (var i = 0; i < expected.Cells.Length; i++) {
            Assert.AreEqual(expected.Cells[i].Row, actual.Cells[i].Row);
            Assert.AreEqual(expected.Cells[i].Column, actual.Cells[i].Column);
            Assert.AreEqual(expected.Cells[i].Count, actual.Cells[i].Count);
            CollectionAssert.AreEqual(expected.Cells[i].Values, actual.Cells[i].Values);
        }
        Assert.AreEqual(expected.RowSubTotals.Length, actual.RowSubTotals.Length);
        Assert.AreEqual(0, actual.ColumnTotals.Length);

        // hand-written, with "Type.Property" names instead of ids - what a REST client would send
        var byName = store.Datastore.Query("PivotOrder.Pivot().AddRow(\"PivotOrder.Region\").AddColumn(\"PivotOrder.Channel\").AddSum(\"PivotOrder.Amount\", \"total\").AddCount()", [], null) as PivotQueryResultData;
        Assert.IsNotNull(byName);
        Assert.AreEqual(_regions.Length, byName.Result.Rows.Groups.Length);
        Assert.AreEqual(3, byName.Result.Columns.Groups.Length);
        Assert.AreEqual((double)all.Sum(o => o.Amount), byName.Result.GrandTotal.Get("total")!.Value, 1e-9);
        Assert.AreEqual(all.Count, byName.Result.GrandTotal.Get("Count"));

        // and the JSON path of a query string
        var json = new QueryOfNodes<PivotOrder, PivotOrder>(store, null).Pivot().AddRow(o => o.Region).AddCount().EvaluateForJson();
        Assert.IsInstanceOfType(json, typeof(PivotResult));
        var serialized = JsonSerializer.Serialize(json); // must serialize without cycles or unsupported members
        StringAssert.Contains(serialized, "\"Rows\"");
        StringAssert.Contains(serialized, "\"Cells\"");
    }

    [TestMethod]
    public void RowPaging_AndCellLimit() {
        var store = OpenStore(out var all, out _, out _);
        var page = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddCount().SetRowPaging(1, 3).Execute();
        Assert.AreEqual(4, page.Rows.TotalGroupCount);
        Assert.AreEqual(1, page.Rows.Groups.Length);
        Assert.AreEqual(1, page.Rows.PageIndex);
        Assert.AreEqual(3, page.Rows.PageSize);
        Assert.AreEqual(_regions.OrderBy(r => r, StringComparer.Ordinal).Last(), page.Rows.Groups[0].DisplayName);
        Assert.AreEqual(all.Count, page.GrandTotal.Count);
        Assert.AreEqual(1, page.RowTotals.Length);

        var capped = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddColumn(o => o.Channel).AddCount().SetLimits(6).Execute();
        Assert.IsTrue(capped.Capped);
        Assert.IsTrue(capped.Rows.Groups.Length * capped.Columns.Groups.Length <= 6);
        Assert.IsTrue(capped.Rows.Groups.Length >= 1);
        Assert.ThrowsException<Exception>(() => store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddColumn(o => o.Channel).AddCount().SetLimits(6, throwWhenExceeded: true).Execute());
        var roomy = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddColumn(o => o.Channel).AddCount().SetLimits(12).Execute();
        Assert.IsFalse(roomy.Capped);
    }

    [TestMethod]
    public void Builder_IsImmutable() {
        var store = OpenStore(out _, out _, out _);
        var basePivot = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddCount();
        var before = basePivot.ToString();
        var withColumn = basePivot.AddColumn(o => o.Channel);
        var withMeasure = basePivot.AddSum(o => o.Amount);
        var withOptions = basePivot.SetRowOptions(o => o.Region, maxGroups: 1).SetTotals(subTotals: true).SetRowPaging(0, 1);
        Assert.AreEqual(before, basePivot.ToString(), "operators must not change the query they were called on");
        Assert.AreNotEqual(before, withColumn.ToString());
        Assert.AreNotEqual(before, withMeasure.ToString());
        Assert.AreNotEqual(before, withOptions.ToString());
        Assert.AreEqual(1, basePivot.Execute().Columns.Groups.Length);
        Assert.AreEqual(3, withColumn.Execute().Columns.Groups.Length);
        Assert.AreEqual(4, basePivot.Execute().Rows.Groups.Length);
        Assert.AreEqual(1, withOptions.Execute().Rows.Groups.Length);
    }

    [TestMethod]
    public void Validation_ClearErrors() {
        var store = OpenStore(out _, out _, out _);
        var ex = Assert.ThrowsException<Exception>(() => store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddSum(o => o.Note).Execute());
        StringAssert.Contains(ex.Message, "numeric");
        StringAssert.Contains(ex.Message, "Note");
        // CountDistinct works on any indexed scalar property
        var distinctNotes = store.Query<PivotOrder>().Pivot().AddCountDistinct(o => o.Note, "notes").Execute();
        Assert.AreEqual(90, distinctNotes.GrandTotal.Get("notes"));
        // duplicate measure names
        var dup = Assert.ThrowsException<Exception>(() => store.Query<PivotOrder>().Pivot().AddSum(o => o.Amount, "x").AddMin(o => o.Amount, "x").Execute());
        StringAssert.Contains(dup.Message, "\"x\"");
        // a pivot with no groups and no measures is still a valid (one cell) result
        var bare = store.Query<PivotOrder>().Pivot().Execute();
        Assert.AreEqual(1, bare.Rows.Groups.Length);
        Assert.AreEqual(1, bare.Columns.Groups.Length);
        Assert.AreEqual(1, bare.Cells.Length);
        Assert.AreEqual(90, bare.Cells[0].Count);
        Assert.AreEqual(0, bare.Measures.Length);
        // a filter that leaves nothing gives an empty table, not an error
        var empty = store.Query<PivotOrder>().Where(o => o.Quantity > 1000).Pivot().AddRow(o => o.Region).AddSum(o => o.Amount).Execute();
        Assert.AreEqual(0, empty.SourceCount);
        Assert.AreEqual(0, empty.Rows.Groups.Length);
        Assert.AreEqual(0, empty.Cells.Length);
        Assert.IsNull(empty.GrandTotal.Get("Amount.Sum"), "a sum over no values is undefined, not 0");
    }

    [TestMethod]
    public void Rendering_EnumerateRowsAndToTable() {
        var store = OpenStore(out var all, out _, out _);
        var pivot = store.Query<PivotOrder>().Pivot().AddRow(o => o.Region).AddColumn(o => o.Channel).AddCount().AddSum(o => o.Amount, "revenue").Execute();
        var rows = pivot.EnumerateRows().ToList();
        Assert.AreEqual(pivot.Rows.Groups.Length, rows.Count);
        foreach (var row in rows) {
            Assert.AreEqual(pivot.Columns.Groups.Length, row.Cells.Length);
            Assert.IsNotNull(row.Total);
            Assert.AreEqual(row.Cells.Sum(c => c?.Count ?? 0), row.Total.Count, "a scalar row property: the cells partition the row");
            for (var c = 0; c < row.Cells.Length; c++) Assert.AreSame(pivot[row.Index, c], row.Cells[c]);
        }
        var table = pivot.ToTable();
        CollectionAssert.AreEqual(new[] { "Region", "Channel", "Count", "Count", "revenue" }, table.Columns);
        Assert.AreEqual(pivot.Cells.Length, table.Rows.Count);
        var first = table.Rows[0];
        Assert.AreEqual(pivot.Rows.Groups[pivot.Cells[0].Row].DisplayName, first[0]);
        Assert.AreEqual(pivot.Columns.Groups[pivot.Cells[0].Column].DisplayName, first[1]);
        Assert.AreEqual(pivot.Cells[0].Count, first[2]);
        Assert.IsFalse(string.IsNullOrEmpty(pivot.ToString()));
        Assert.AreEqual(-1, pivot.IndexOfMeasure("nope"));
        Assert.AreEqual(1, pivot.IndexOfMeasure("REVENUE"));
        Assert.AreEqual(all.Count, pivot.GrandTotal.Count);
    }
}
