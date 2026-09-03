using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;

namespace Relatude.Querying;

/// <summary>
/// The GroupBy API: LINQ / EF Core shaped grouping on top of the pivot engine. Uses the pivot test
/// model (PivotOrder, PivotCustomer) and checks every answer against LINQ over the same objects.
/// </summary>
[TestClass]
public class GroupByTests {
    static readonly string[] _regions = ["North", "South", "East", "West"];

    static NodeStore OpenStore(out List<PivotOrder> all, out List<PivotCustomer> customers, out Dictionary<Guid, PivotCustomer?> customerOf) {
        var dm = new Datamodel();
        dm.Add<PivotOrder>(autoDeduceRelations: true);
        dm.Add<PivotBigOrder>(autoDeduceRelations: true);
        dm.Add<PivotCustomer>(autoDeduceRelations: true);
        var store = new NodeStore(DataStoreLocal.Open(dm));
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
            order.Amount = (i % 13) * 10.5m + 1;
            order.Quantity = i % 7 + 1;
            order.OrderDate = new DateTime(2024, 1, 15).AddDays(i * 9);
            order.Tags = i % 5 == 0 ? ["gift", "bulk"] : i % 2 == 0 ? ["gift"] : ["plain"];
            order.Note = "note " + (i % 20);
            order.Express = i % 4 == 0;
            all.Add(order);
        }
        store.Insert(all);
        customerOf = new Dictionary<Guid, PivotCustomer?>();
        for (var i = 1; i <= 90; i++) {
            var order = all[i - 1];
            if (i % 10 == 0) { customerOf[order.PId] = null; continue; }
            var customer = customers[i % 3];
            store.AddRelation(order, o => o.Customer, customer);
            customerOf[order.PId] = customer;
        }
        return store;
    }

    [TestMethod]
    public void GroupBy_OneKey_GivesGroupsWithCounts() {
        var store = OpenStore(out var all, out _, out _);
        var groups = store.Query<PivotOrder>().GroupBy(o => o.Region).Execute();
        Assert.AreEqual(all.Count, groups.SourceCount);
        Assert.AreEqual(_regions.Length, groups.Count);
        Assert.AreEqual(_regions.Length, groups.TotalCount);
        foreach (var g in groups) {
            Assert.AreEqual(all.Count(o => o.Region == g.Key), g.Count, g.Key);
            Assert.AreEqual(g.Key, g.Label);
            Assert.IsFalse(g.IsMissing);
        }
        // natural order: values sorted
        CollectionAssert.AreEqual(_regions.OrderBy(r => r).ToArray(), groups.Select(g => g.Key).ToArray());
        // ToList / ToArray through the executable extensions
        Assert.AreEqual(_regions.Length, store.Query<PivotOrder>().GroupBy(o => o.Region).ToList().Count);
    }

    [TestMethod]
    public void GroupBy_CompositeKey_WithDateParts() {
        var store = OpenStore(out var all, out _, out _);
        var groups = store.Query<PivotOrder>()
            .GroupBy(o => new { o.Region, o.OrderDate.Year })
            .Execute();
        var expected = all.GroupBy(o => new { o.Region, o.OrderDate.Year }).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(expected.Count, groups.Count);
        foreach (var g in groups) Assert.AreEqual(expected[g.Key], g.Count, g.Key.ToString());
        Assert.AreEqual(2, groups.First().Labels.Length);

        // year and month of the same property: two calendar levels, the parts read off the bucket starts
        var byMonth = store.Query<PivotOrder>()
            .GroupBy(o => new { o.OrderDate.Year, o.OrderDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Orders = g.Count() })
            .Execute();
        var expectedMonths = all.GroupBy(o => new { o.OrderDate.Year, o.OrderDate.Month }).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(expectedMonths.Count, byMonth.Count);
        foreach (var r in byMonth) Assert.AreEqual(expectedMonths[new { r.Year, r.Month }], r.Orders);
        // ascending calendar order
        CollectionAssert.AreEqual(byMonth.Select(r => r.Year * 100 + r.Month).OrderBy(x => x).ToArray(), byMonth.Select(r => r.Year * 100 + r.Month).ToArray());
    }

    [TestMethod]
    public void Select_Aggregates_MatchLinq_AndKeepTheirTypes() {
        var store = OpenStore(out var all, out _, out _);
        var rows = store.Query<PivotOrder>()
            .GroupBy(o => o.Region)
            .Select(g => new {
                Region = g.Key,
                Orders = g.Count(),
                Revenue = g.Sum(o => o.Amount),
                AvgQuantity = g.Average(o => o.Quantity),
                Smallest = g.Min(o => o.Amount),
                Biggest = g.Max(o => o.Amount),
                Notes = g.CountDistinct(o => o.Note),
                NotesLinqStyle = g.Select(o => o.Note).Distinct().Count(),
                Big = g.LongCount(),
                PerOrder = g.Sum(o => o.Amount) / g.Count(), // arithmetic over aggregates runs on the rows
            })
            .Execute();
        Assert.AreEqual(_regions.Length, rows.Count);
        foreach (var r in rows) {
            var inGroup = all.Where(o => o.Region == r.Region).ToList();
            Assert.AreEqual(inGroup.Count, r.Orders);
            Assert.AreEqual(inGroup.Sum(o => o.Amount), r.Revenue);
            Assert.AreEqual(inGroup.Average(o => o.Quantity), r.AvgQuantity, 1e-9);
            Assert.AreEqual(inGroup.Min(o => o.Amount), r.Smallest);
            Assert.AreEqual(inGroup.Max(o => o.Amount), r.Biggest);
            Assert.AreEqual(inGroup.Select(o => o.Note).Distinct().Count(), r.Notes);
            Assert.AreEqual(r.Notes, r.NotesLinqStyle);
            Assert.AreEqual((long)inGroup.Count, r.Big);
            Assert.AreEqual(inGroup.Sum(o => o.Amount) / inGroup.Count, r.PerOrder);
        }
        Assert.IsInstanceOfType(rows.First().Revenue, typeof(decimal));
        Assert.IsInstanceOfType(rows.First().AvgQuantity, typeof(double));
        Assert.IsInstanceOfType(rows.First().Orders, typeof(int));
    }

    [TestMethod]
    public void Having_OrderBy_Take_OnOneKey_RunInTheEngine() {
        var store = OpenStore(out var all, out _, out _);
        var query = store.Query<PivotOrder>()
            .GroupBy(o => o.Region)
            .Select(g => new { Region = g.Key, Orders = g.Count(), Revenue = g.Sum(o => o.Amount) })
            .Having(r => r.Orders > 5)
            .OrderByDescending(r => r.Revenue)
            .Take(2);
        var text = query.ToString();
        StringAssert.Contains(text, ".SetRowOptions(", "one key sorted by one measure: the engine sorts");
        StringAssert.Contains(text, "\"Amount.Sum\", true");
        Assert.IsFalse(text.Contains(".SetRowPaging("), "a filter sits between the sort and the paging, so the page is taken here");
        var rows = query.Execute();
        var expected = all.GroupBy(o => o.Region).Select(g => new { Region = g.Key, Orders = g.Count(), Revenue = g.Sum(o => o.Amount) })
            .Where(r => r.Orders > 5).OrderByDescending(r => r.Revenue).Take(2).ToList();
        CollectionAssert.AreEqual(expected.Select(r => r.Region).ToArray(), rows.Select(r => r.Region).ToArray());
        Assert.AreEqual(_regions.Length, rows.TotalCount, "TotalCount is the number of groups after the filter (every region has more than 5 orders)");

        // no filter: sort AND page in the engine
        var paged = store.Query<PivotOrder>().GroupBy(o => o.Region)
            .Select(g => new { Region = g.Key, Revenue = g.Sum(o => o.Amount) })
            .OrderBy(r => r.Revenue)
            .Page(1, 1);
        StringAssert.Contains(paged.ToString(), ".SetRowPaging(1, 1)");
        var page = paged.Execute();
        Assert.AreEqual(1, page.Count);
        Assert.AreEqual(_regions.Length, page.TotalCount);
        Assert.AreEqual(1, page.PageIndex);
        var ascending = all.GroupBy(o => o.Region).Select(g => new { Region = g.Key, Revenue = g.Sum(o => o.Amount) }).OrderBy(r => r.Revenue).ToList();
        Assert.AreEqual(ascending[1].Region, page.First().Region);

        // ordering the groups themselves by count (Express is 22 true / 68 false: no tie), and by a key
        var byCount = store.Query<PivotOrder>().GroupBy(o => o.Express).OrderByDescending(g => g.Count).Take(1);
        StringAssert.Contains(byCount.ToString(), "\"Count\", true");
        Assert.AreEqual(all.GroupBy(o => o.Express).OrderByDescending(g => g.Count()).First().Key, byCount.Execute().Single().Key);
        var byKeyDesc = store.Query<PivotOrder>().GroupBy(o => o.Region).OrderByDescending(g => g.Key).Execute(); // in memory
        CollectionAssert.AreEqual(_regions.OrderByDescending(r => r).ToArray(), byKeyDesc.Select(g => g.Key).ToArray());
    }

    [TestMethod]
    public void OrderBy_OnCompositeKeys_IsFlat() {
        var store = OpenStore(out var all, out _, out _);
        var query = store.Query<PivotOrder>()
            .GroupBy(o => new { o.Region, o.Channel })
            .Select(g => new { g.Key.Region, g.Key.Channel, Revenue = g.Sum(o => o.Amount) })
            .OrderByDescending(r => r.Revenue)
            .ThenBy(r => r.Region)
            .Take(5);
        Assert.IsFalse(query.ToString().Contains(".SetRowOptions("), "a sort over nested levels is flat, so it is done here, not per level in the engine");
        var rows = query.Execute();
        var expected = all.GroupBy(o => new { o.Region, o.Channel }).Select(g => new { g.Key.Region, g.Key.Channel, Revenue = g.Sum(o => o.Amount) })
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.Region).Take(5).ToList();
        CollectionAssert.AreEqual(expected.Select(r => r.Region + "/" + r.Channel).ToArray(), rows.Select(r => r.Region + "/" + r.Channel).ToArray());
        Assert.AreEqual(all.GroupBy(o => new { o.Region, o.Channel }).Count(), rows.TotalCount);
    }

    [TestMethod]
    public void Enum_Bool_And_Relation_Keys() {
        var store = OpenStore(out var all, out var customers, out var customerOf);
        var byChannel = store.Query<PivotOrder>().GroupBy(o => o.Channel).Execute();
        Assert.IsInstanceOfType(byChannel.First().Key, typeof(PivotChannel));
        foreach (var g in byChannel) {
            Assert.AreEqual(all.Count(o => o.Channel == g.Key), g.Count);
            Assert.AreEqual(g.Key.ToString(), g.Label, "enum groups are labelled by name");
        }
        var byExpress = store.Query<PivotOrder>().GroupBy(o => o.Express).Execute();
        Assert.AreEqual(all.Count(o => o.Express), byExpress.Single(g => g.Key).Count);

        // a relation key: the related node, null for the orders without one - which form a group by default, as in SQL
        var byCustomer = store.Query<PivotOrder>().GroupBy(o => o.Customer).Execute();
        Assert.AreEqual(customers.Count + 1, byCustomer.Count);
        var missing = byCustomer.Single(g => g.IsMissing);
        Assert.IsNull(missing.Key);
        Assert.AreEqual("(none)", missing.Label);
        Assert.AreEqual(customerOf.Values.Count(c => c == null), missing.Count);
        foreach (var g in byCustomer.Where(g => !g.IsMissing)) {
            Assert.IsNotNull(g.Key);
            Assert.AreEqual(g.Key!.Name, g.Label, "relation groups are labelled by the related node's display name");
            Assert.AreEqual(customerOf.Values.Count(c => c?.Id == g.Key.Id), g.Count);
        }
        // and switched off
        var noMissing = store.Query<PivotOrder>().GroupBy(o => o.Customer).IncludeMissing(false).Execute();
        Assert.AreEqual(customers.Count, noMissing.Count);
        Assert.IsFalse(noMissing.Any(g => g.IsMissing));
        // with a Select the key is the typed node too
        var top = store.Query<PivotOrder>().GroupBy(o => o.Customer).IncludeMissing(false)
            .Select(g => new { Customer = g.Key!.Name, Revenue = g.Sum(o => o.Amount) })
            .OrderByDescending(r => r.Revenue).Execute();
        var expectedTop = all.Where(o => customerOf[o.PId] != null).GroupBy(o => customerOf[o.PId]!.Name).ToDictionary(g => g.Key, g => g.Sum(o => o.Amount));
        CollectionAssert.AreEquivalent(expectedTop.Keys.ToArray(), top.Select(r => r.Customer).ToArray());
        foreach (var r in top) Assert.AreEqual(expectedTop[r.Customer], r.Revenue); // (the three revenues tie in this data, so the order is not asserted)
    }

    [TestMethod]
    public void Bucket_Ranges_And_Interval() {
        var store = OpenStore(out var all, out _, out _);
        var auto = store.Query<PivotOrder>().GroupBy(o => Bucket.Ranges(o.Amount, 4)).Execute();
        Assert.IsTrue(auto.Count >= 2 && auto.Count <= 6, "about four buckets, got " + auto.Count);
        Assert.AreEqual(all.Count, auto.Sum(g => g.Count), "every order is in exactly one range");
        foreach (var g in auto) {
            Assert.IsTrue(g.Key.From <= g.Key.To);
            Assert.AreEqual(g.Key.Label, g.Label);
        }
        var explicitRanges = store.Query<PivotOrder>()
            .GroupBy(o => Bucket.Ranges(o.Amount, new[] { 0m, 50m, 200m })) // an expression tree cannot hold a collection expression
            .Select(g => new { g.Key.From, g.Key.To, g.Key.Label, Orders = g.Count() })
            .Execute();
        Assert.AreEqual(2, explicitRanges.Count);
        Assert.AreEqual(all.Count(o => o.Amount >= 0m && o.Amount <= 50m), explicitRanges.Single(r => r.From == 0m).Orders);
        Assert.AreEqual(all.Count(o => o.Amount >= 50m && o.Amount <= 200m), explicitRanges.Single(r => r.From == 50m).Orders);

        var quarters = store.Query<PivotOrder>()
            .GroupBy(o => Bucket.Interval(o.OrderDate, DateInterval.Quarter))
            .Select(g => new { Quarter = g.Key, Orders = g.Count() })
            .Execute();
        var expected = all.GroupBy(o => Bucket.Interval(o.OrderDate, DateInterval.Quarter)).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(expected.Count, quarters.Count);
        foreach (var r in quarters) Assert.AreEqual(expected[r.Quarter], r.Orders, r.Quarter.ToString("yyyy-MM-dd"));

        // a coarser Bucket.Interval next to a finer date part of the same property: one level, floored on the way out
        var mixed = store.Query<PivotOrder>()
            .GroupBy(o => new { Quarter = Bucket.Interval(o.OrderDate, DateInterval.Quarter), o.OrderDate.Month })
            .Execute();
        var expectedMixed = all.GroupBy(o => new { Quarter = Bucket.Interval(o.OrderDate, DateInterval.Quarter), o.OrderDate.Month }).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(expectedMixed.Count, mixed.Count);
        foreach (var g in mixed) Assert.AreEqual(expectedMixed[g.Key], g.Count);
    }

    [TestMethod]
    public void ArrayValuedKey_OneGroupPerElement() {
        var store = OpenStore(out var all, out _, out _);
        var byTag = store.Query<PivotOrder>().GroupBy(o => o.Tags).Execute();
        var expected = all.SelectMany(o => o.Tags).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(expected.Count, byTag.Count);
        foreach (var g in byTag) {
            Assert.AreEqual(1, g.Key.Length, "the key is the one element the group stands for");
            Assert.AreEqual(expected[g.Key[0]], g.Count);
        }
        Assert.IsTrue(byTag.Sum(g => g.Count) > all.Count, "a node with two tags is in two groups");
        Assert.AreEqual(all.Count, byTag.SourceCount);
    }

    [TestMethod]
    public void RuntimeKeys_AndAggregatesByName() {
        var store = OpenStore(out var all, out _, out _);
        var dm = store.Datastore.Datamodel;
        var region = store.Mapper.GetProperty<PivotOrder>(nameof(PivotOrder.Region)).Id;
        var orderDate = store.Mapper.GetProperty<PivotOrder>(nameof(PivotOrder.OrderDate)).Id;
        var amount = store.Mapper.GetProperty<PivotOrder>(nameof(PivotOrder.Amount)).Id;
        var query = store.Query<PivotOrder>()
            .GroupBy(GroupKey.Values(region), GroupKey.Interval(orderDate, DateInterval.Year))
            .Aggregate(PivotFunction.Sum, amount)
            .Aggregate(PivotFunction.Average, o => o.Quantity)
            .Aggregate(PivotFunction.Count, amount) // a no-op: Count is always there
            .OrderByDescending(g => g["Amount.Sum"]);
        var groups = query.Execute();
        var expected = all.GroupBy(o => new { o.Region, o.OrderDate.Year }).Select(g => new { g.Key.Region, g.Key.Year, Count = g.Count(), Revenue = g.Sum(o => o.Amount), Qty = g.Average(o => o.Quantity) })
            .ToDictionary(r => r.Region + "/" + r.Year);
        Assert.AreEqual(expected.Count, groups.Count);
        // descending by revenue (ties may land in either order, so the values are compared, not the keys)
        CollectionAssert.AreEqual(expected.Values.Select(r => (double)r.Revenue).OrderByDescending(x => x).ToArray(), groups.Select(g => g["Amount.Sum"]!.Value).ToArray());
        foreach (var g in groups) {
            var year = (GroupRange<object>)g.Key[1]!;
            var e = expected[g.Key[0] + "/" + ((DateTime)year.From).Year];
            Assert.AreEqual(e.Year.ToString(), year.Label);
            Assert.AreEqual(e.Count, g.Count);
            Assert.AreEqual((double)e.Revenue, g["Amount.Sum"]!.Value, 1e-9);
            Assert.AreEqual(e.Qty, g["quantity.average"]!.Value, 1e-9); // names are case-insensitive
            CollectionAssert.AreEqual(new[] { "Amount.Sum", "Quantity.Average" }, g.MeasureNames);
        }
        Assert.ThrowsException<ArgumentException>(() => groups.First()["Nope"]);

        // one runtime key: the engine sorts and pages
        var top = store.Query<PivotOrder>().GroupBy(GroupKey.Values(region)).Aggregate(PivotFunction.Sum, amount).OrderByDescending(g => g["Amount.Sum"]).Take(1);
        StringAssert.Contains(top.ToString(), ".SetRowOptions(");
        StringAssert.Contains(top.ToString(), ".SetRowPaging(0, 1)");
        Assert.AreEqual(all.GroupBy(o => o.Region).OrderByDescending(g => g.Sum(o => o.Amount)).First().Key, top.Execute().Single().Key[0]);
        Assert.AreEqual(dm.Properties[region].CodeName, "Region");
    }

    [TestMethod]
    public void QueryString_RoundTrips_AndParsesByName() {
        var store = OpenStore(out var all, out _, out _);
        var typed = store.Query<PivotOrder>().Where("o => o.Quantity > 2")
            .GroupBy(o => new { o.Region, o.OrderDate.Year })
            .Select(g => new { g.Key.Region, g.Key.Year, Orders = g.Count(), Revenue = g.Sum(o => o.Amount) });
        var text = typed.ToString();
        StringAssert.Contains(text, ".GroupBy(\"");
        StringAssert.Contains(text, "|Region\")");
        StringAssert.Contains(text, ".AddRow(\"");
        StringAssert.Contains(text, "\"Year\")");
        StringAssert.Contains(text, ".AddSum(\"");
        Assert.IsFalse(text.Contains(".SetTotals("), "row totals only is the GroupBy default and is not spelled out");
        Assert.IsFalse(text.Contains(".SetLimits("), "the throwing cell limit is the GroupBy default");
        var expected = typed.Execute();
        var parsed = store.Datastore.Query(text, [], null) as PivotQueryResultData;
        Assert.IsNotNull(parsed, "the store answers a GroupBy query string with pivot data");
        Assert.AreEqual(expected.Count, parsed.Result.Rows.Groups.Length);
        Assert.AreEqual(0, parsed.Result.ColumnTotals.Length);
        for (var i = 0; i < expected.Count; i++) {
            var row = expected.ToArray()[i];
            Assert.AreEqual(row.Orders, parsed.Result.Rows.Groups[i].Count);
            Assert.AreEqual((double)row.Revenue, parsed.Result.RowTotals[i].Get("Amount.Sum")!.Value, 1e-9);
        }
        // hand-written, by name - what a REST client sends
        var byName = store.Datastore.Query("PivotOrder.GroupBy(\"PivotOrder.Region\", \"PivotOrder.Channel\").AddCount().AddSum(\"PivotOrder.Amount\")", [], null) as PivotQueryResultData;
        Assert.IsNotNull(byName);
        Assert.AreEqual(all.GroupBy(o => new { o.Region, o.Channel }).Count(), byName.Result.Rows.Groups.Length);
        Assert.AreEqual(1, byName.Result.Columns.Groups.Length, "one axis: a single (all) column");
        Assert.AreEqual((double)all.Sum(o => o.Amount), byName.Result.GrandTotal.Get("Amount.Sum")!.Value, 1e-9);
        Assert.IsTrue(byName.Result.Rows.Levels.All(l => !l.IsRange));
    }

    [TestMethod]
    public void GroupBy_AfterFacetSelection_UsesTheSelectionAsFilter() {
        var store = OpenStore(out var all, out _, out _);
        var groups = store.Query<PivotOrder>()
            .Facets()
            .SetFacetValue(o => o.Express, true)
            .GroupBy(o => o.Region)
            .Execute();
        var express = all.Where(o => o.Express).ToList();
        Assert.AreEqual(express.Count, groups.SourceCount);
        foreach (var g in groups) Assert.AreEqual(express.Count(o => o.Region == g.Key), g.Count);
    }

    [TestMethod]
    public void Builder_IsImmutable_AndCountsGroups() {
        var store = OpenStore(out _, out _, out _);
        var groups = store.Query<PivotOrder>().GroupBy(o => o.Region);
        var filtered = groups.Where(g => g.Count > 1000);
        Assert.AreNotSame(groups, filtered);
        Assert.AreEqual(_regions.Length, groups.Count());
        Assert.AreEqual(0, filtered.Count());
        var rows = groups.Select(g => new { g.Key, N = g.Count() });
        var sorted = rows.OrderBy(r => r.N);
        Assert.AreNotSame(rows, sorted);
        Assert.IsFalse(rows.ToString().Contains("SetRowOptions"));
        StringAssert.Contains(sorted.ToString(), "SetRowOptions");
    }

    [TestMethod]
    public void Validation_ClearErrors() {
        var store = OpenStore(out _, out _, out _);
        var q = store.Query<PivotOrder>();
        var computed = Assert.ThrowsException<NotSupportedException>(() => q.GroupBy(o => o.Quantity * 2));
        StringAssert.Contains(computed.Message, "cannot be translated");
        var enumerated = Assert.ThrowsException<NotSupportedException>(() => q.GroupBy(o => o.Region).Select(g => g.First().Note));
        StringAssert.Contains(enumerated.Message, "cannot be enumerated");
        var filteredCount = Assert.ThrowsException<NotSupportedException>(() => q.GroupBy(o => o.Region).Select(g => g.Count(o => o.Express)));
        StringAssert.Contains(filteredCount.Message, "filtered count");
        var notAProperty = Assert.ThrowsException<NotSupportedException>(() => q.GroupBy(o => o.Region).Select(g => g.Sum(o => o.Note.Length)));
        StringAssert.Contains(notAProperty.Message, "must be a property of the node");
        // a non-numeric measure is refused by the engine with the property named
        var notNumeric = Assert.ThrowsException<Exception>(() => q.GroupBy(o => o.Region).Select(g => new { Max = g.Max(o => o.Note) }).Execute());
        StringAssert.Contains(notNumeric.Message, "Note");
        StringAssert.Contains(notNumeric.Message, "numeric");
        Assert.ThrowsException<NotSupportedException>(() => Bucket.Ranges(1m, 5));
        Assert.ThrowsException<ArgumentException>(() => q.GroupBy());
    }
}
