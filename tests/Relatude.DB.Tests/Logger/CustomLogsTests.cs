using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
[TestClass]
public class CustomLogsTests {
    static readonly string[] reserved = ["system", "query"];
    static CustomLogs open(IIOProvider io) => new(io, reserved);
    static LogSettings orders(Action<LogSettings>? configure = null) {
        var s = new LogSettings {
            Key = "orders",
            Name = "Orders",
            Description = "Every order placed",
            FileInterval = FileInterval.Day,
            FirstDayOfWeek = DayOfWeek.Monday,
        };
        s.Properties.Add("amount", new LogProperty { Name = "Amount", DataType = LogDataType.Double, Statistics = [new(StatisticsType.CountSumAvgMinMax)] });
        s.Properties.Add("customer", new LogProperty { Name = "Customer", DataType = LogDataType.String, Statistics = [new(StatisticsType.UniqueCountWithValues)] });
        s.Properties.Add("items", new LogProperty { Name = "Items", DataType = LogDataType.Integer });
        configure?.Invoke(s);
        return s;
    }
    static int count(ICustomLogs logs, string key = "orders") {
        logs.LogStore.ExtractLog(key, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), 0, 1, false, out var total);
        return total;
    }

    [TestMethod]
    public void ALogIsDefinedByItsSettingsFileAndFoundAgainAfterARestart() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        Assert.IsTrue(io.Exists(FileKeyUtility.Logger_GetSettings("orders")));
        Assert.IsTrue(logs.AnyEnabled);
        Assert.IsTrue(logs.Record("orders", ("amount", 10.0), ("customer", "acme"), ("items", 2)));
        logs.Dispose();

        var again = open(io);
        Assert.IsTrue(again.HasLog("orders"));
        var definition = again.GetDefinition("orders")!;
        Assert.AreEqual("Orders", definition.Name);
        Assert.AreEqual("Every order placed", definition.Description);
        Assert.AreEqual(3, definition.Properties.Count);
        Assert.AreEqual(1, count(again));
        again.Dispose();
    }

    [TestMethod]
    public void KeysAreCheckedBeforeALogIsCreated() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        StringAssert.Contains(logs.CheckNewKey("system"), "activity logs");
        StringAssert.Contains(logs.CheckNewKey("ORDERS"), "already a log");
        Assert.IsNotNull(logs.CheckNewKey("a.b"));
        Assert.IsNotNull(logs.CheckNewKey("a b"));
        Assert.IsNotNull(logs.CheckNewKey("-starts-with-dash"));
        Assert.IsNotNull(logs.CheckNewKey(new string('x', 65)));
        Assert.IsNotNull(logs.CheckNewKey(""));
        Assert.IsNull(logs.CheckNewKey("payments_2026-q3"));
        Assert.ThrowsExactly<ArgumentException>(() => logs.Create(orders(s => s.Key = "Query")));
        Assert.ThrowsExactly<ArgumentException>(() => logs.Create(orders()));
        logs.Dispose();
    }

    [TestMethod]
    public void EveryRecordOverloadConvertsToTheDeclaredColumns() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        var t0 = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        logs.Record("orders", new LogEntry { Timestamp = t0, Values = { ["amount"] = 1.5 } });
        logs.Record("orders", new Dictionary<string, object?> { ["amount"] = 2, ["customer"] = null }, t0.AddMinutes(1));
        logs.RecordObject("orders", new { Amount = 3.5m, Customer = "acme", Items = 4L }, t0.AddMinutes(2));
        Assert.IsFalse(logs.Record("nope", ("x", 1)));
        Assert.IsFalse(logs.RecordObject("nope", new { x = 1 }));
        var entries = logs.LogStore.ExtractLog("orders", t0, t0.AddHours(1), 0, 10, false, out _).ToList();
        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(2.0, entries[1].Values["amount"]); // an int recorded in a decimal column
        Assert.IsFalse(entries[1].Values.ContainsKey("customer")); // null is left out
        Assert.AreEqual(3.5, entries[2].Values["amount"]); // a decimal, and the property named Amount
        Assert.AreEqual("acme", entries[2].Values["customer"]);
        Assert.AreEqual(4, entries[2].Values["items"]);
        var sum = logs.LogStore.AnalyseCombinedCountSumAvgMinMax("orders", "amount", IntervalType.Hour, t0, t0.AddHours(1));
        Assert.AreEqual(3, sum.Value.Count);
        Assert.AreEqual(7.0, sum.Value.Sum);
        logs.Dispose();
    }

    [TestMethod]
    public void ADefinitionHandedOutIsACopy() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        var copy = logs.GetDefinition("orders")!;
        copy.EnableLog = false;
        copy.Properties.Clear();
        Assert.IsTrue(logs.LogStore.GetSetting("orders").EnableLog);
        Assert.AreEqual(3, logs.GetDefinition("orders")!.Properties.Count);
        logs.Dispose();
    }

    [TestMethod]
    public void SwitchesAreSavedAndStatisticsSurviveBeingTurnedOffAndOn() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        var t0 = DateTime.UtcNow.AddMinutes(-5);
        logs.Record("orders", new LogEntry { Timestamp = t0, Values = { ["amount"] = 5.0 } });
        logs.SetEnabled("orders", null, false);
        Assert.IsFalse(logs.GetDefinition("orders")!.EnableStatistics);
        logs.SetEnabled("orders", false, null);
        Assert.IsFalse(logs.AnyEnabled);
        logs.Dispose();

        logs = open(io);
        Assert.IsFalse(logs.IsEnabled("orders")); // saved, not only live
        logs.SetEnabled("orders", true, true);
        var sum = logs.LogStore.AnalyseCombinedCountSumAvgMinMax("orders", "amount", IntervalType.Hour, t0.AddHours(-1), t0.AddHours(1));
        Assert.AreEqual(5.0, sum.Value.Sum); // read back from the statistics file
        logs.Dispose();
    }

    [TestMethod]
    public void AddingAColumnAndStatisticsIsPlannedAndCanBeRebuiltFromTheEntries() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        for (var i = 0; i < 5; i++) logs.Record("orders", new LogEntry { Timestamp = t0.AddMinutes(i), Values = { ["amount"] = 1.0, ["items"] = i } });
        var next = orders(s => {
            s.Properties["items"].Statistics.Add(new(StatisticsType.Sum));
            s.Properties.Add("channel", new LogProperty { Name = "Channel", DataType = LogDataType.String });
        });
        var plan = logs.PlanUpdate(next);
        Assert.IsTrue(plan.Changed);
        Assert.IsTrue(plan.CanRebuildStatistics);
        Assert.IsFalse(plan.MovesEntries);
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("'Channel' (channel) is a new column")));
        Assert.AreEqual(0, logs.GetDefinition("orders")!.Properties["items"].Statistics.Count); // a plan changes nothing
        var done = logs.Update(next, rebuildStatistics: true);
        Assert.IsTrue(done.StatisticsRebuilt);
        var items = logs.LogStore.AnalyseCombinedIntegerSums("orders", "items", IntervalType.Hour, t0.AddHours(-1), t0.AddHours(1));
        Assert.AreEqual(0 + 1 + 2 + 3 + 4, items.Value);
        Assert.IsFalse(logs.PlanUpdate(next).Changed); // the same definition again changes nothing
        logs.Dispose();
    }

    [TestMethod]
    public void ANewFileIntervalMovesTheEntriesIntoFilesOfTheNewSize() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        var t0 = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 30; i++) logs.Record("orders", new LogEntry { Timestamp = t0.AddHours(i * 3), Values = { ["amount"] = (double)i } }); // ~4 days
        logs.FlushToDiskNow();
        Assert.IsTrue(logs.GetFiles("orders").Count(f => f.Kind == "entries") >= 4);
        var next = orders(s => s.FileInterval = FileInterval.Hour);
        var plan = logs.PlanUpdate(next);
        Assert.IsTrue(plan.MovesEntries);
        var done = logs.Update(next);
        Assert.AreEqual(30, done.EntriesMoved);
        Assert.AreEqual(30, count(logs));
        var files = logs.GetFiles("orders");
        Assert.AreEqual(30, files.Count(f => f.Kind == "entries")); // one per hour that has an entry
        Assert.IsFalse(files.Any(f => f.Kind == "left-over")); // the day files are gone
        Assert.IsTrue(files.All(f => f.Kind != "entries" || f.FileKey.Contains(".hour.")));
        var entries = logs.LogStore.ExtractLog("orders", t0, t0.AddDays(10), 0, 100, false, out _).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(0, 30).Select(i => (double)i).ToArray(), entries.Select(e => (double)e.Values["amount"]).ToArray());
        logs.Dispose();
        // and it is still there after a restart, in its new layout
        logs = open(io);
        Assert.AreEqual(FileInterval.Hour, logs.GetDefinition("orders")!.FileInterval);
        Assert.AreEqual(30, count(logs));
        logs.Dispose();
    }

    [TestMethod]
    public void ATypeChangeOfAColumnWithStatisticsRebuildsThem() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders(s => s.Properties["items"].Statistics.Add(new(StatisticsType.Sum))));
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        logs.Record("orders", new LogEntry { Timestamp = t0, Values = { ["items"] = 2 } });
        logs.Record("orders", new LogEntry { Timestamp = t0.AddMinutes(1), Values = { ["items"] = 3 } });
        var next = orders(s => {
            s.Properties["items"].DataType = LogDataType.Double;
            s.Properties["items"].Statistics.Add(new(StatisticsType.Sum));
        });
        var plan = logs.PlanUpdate(next);
        Assert.IsTrue(plan.DiscardsStatistics);
        var done = logs.Update(next);
        Assert.IsTrue(done.StatisticsRebuilt);
        var sum = logs.LogStore.AnalyseCombinedFloatSums("orders", "items", IntervalType.Hour, t0.AddHours(-1), t0.AddHours(1));
        Assert.AreEqual(5.0, sum.Value);
        logs.Dispose();
    }

    [TestMethod]
    public void AChangeOfTheLevelOfDetailIsSaidOnce() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders(s => s.ResolutionRowStats = 3)); // every statistic at level 3
        static void level(LogSettings s, int value) {
            s.ResolutionRowStats = value;
            foreach (var p in s.Properties.Values) p.Statistics = [.. p.Statistics.Select(x => new StatisticsInfo(x.StatisticsType, value))];
        }
        var plan = logs.PlanUpdate(orders(s => level(s, 5)));
        Assert.AreEqual(1, plan.Notes.Count(n => n.Contains("level of statistical detail")));
        Assert.IsFalse(plan.Notes.Any(n => n.Contains("instead of")), string.Join("\n", plan.Notes)); // not once per statistic
        // a statistic that went to a level of its own is still named
        plan = logs.PlanUpdate(orders(s => {
            level(s, 5);
            s.Properties["amount"].Statistics = [new(StatisticsType.CountSumAvgMinMax, 7)];
        }));
        Assert.AreEqual(1, plan.Notes.Count(n => n.Contains("at level 7 instead of 3")));
        logs.Dispose();
    }

    [TestMethod]
    public void ATighterAgeLimitIsNamedInThePlan() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        logs.Record("orders", new LogEntry { Timestamp = DateTime.UtcNow.AddDays(-20), Values = { ["amount"] = 1.0 } });
        var plan = logs.PlanUpdate(orders(s => s.MaxAgeOfLogFilesInDays = 7));
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("older than 7 days")));
        logs.Dispose();
    }

    [TestMethod]
    public void DeletingTheDefinitionOnlyKeepsTheEntriesForALogWithTheSameKey() {
        var io = new IOProviderMemory();
        var logs = open(io);
        logs.Create(orders());
        logs.Record("orders", ("amount", 1.0));
        logs.Delete("orders", deleteRecorded: false);
        Assert.IsFalse(logs.HasLog("orders"));
        Assert.IsFalse(io.Exists(FileKeyUtility.Logger_GetSettings("orders")));
        logs.Create(orders());
        Assert.AreEqual(1, count(logs));
        logs.Delete("orders", deleteRecorded: true);
        logs.Create(orders());
        Assert.AreEqual(0, count(logs));
        Assert.IsFalse(logs.GetFiles("orders").Any(f => f.Kind == "entries"));
        logs.Dispose();
    }

    [TestMethod]
    public void BrokenSettingsFilesAreReportedAndCanBeRepairedOrDeleted() {
        var io = new IOProviderMemory();
        io.WriteAllTextUTF8(FileKeyUtility.Logger_GetSettings("broken"), "{ this is not json");
        io.WriteAllTextUTF8(FileKeyUtility.Logger_GetSettings("gone"), """{ "Key": "gone", "FileInterval": "Fortnight" }""");
        // settings saved for a system log belong to it, and are no error here
        io.WriteAllTextUTF8(FileKeyUtility.Logger_GetSettings("system"), """{ "Key": "system" }""");
        var logs = open(io);
        Assert.AreEqual(2, logs.LoadErrors.Count);
        Assert.IsFalse(logs.HasLog("system"));
        var broken = logs.LoadErrors.Single(e => e.FileKey.Contains("broken")).FileKey;
        Assert.AreEqual("{ this is not json", logs.ReadBrokenDefinition(broken));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => logs.RepairBrokenDefinition(broken, "{ still not"));
        logs.RepairBrokenDefinition(broken, """{ "Key": "broken", "Name": "Fixed" }""");
        Assert.IsTrue(logs.HasLog("broken"));
        Assert.AreEqual("Fixed", logs.GetDefinition("broken")!.Name);
        var gone = logs.LoadErrors.Single().FileKey;
        logs.DeleteBrokenDefinition(gone);
        Assert.AreEqual(0, logs.LoadErrors.Count);
        Assert.IsFalse(io.Exists(gone.SplitKey()));
        Assert.ThrowsExactly<ArgumentException>(() => logs.DeleteBrokenDefinition(FileKeyUtility.Logger_GetSettings("broken").AsKeyString()));
        logs.Dispose();
        Assert.AreEqual(0, open(io).LoadErrors.Count);
    }

    [TestMethod]
    public void AFileNamedForAnotherKeyIsLoadedAndSavedUnderItsOwnName() {
        var io = new IOProviderMemory();
        orders().Save(io, FileKeyUtility.Logger_GetSettings("renamed-by-hand"));
        var logs = open(io);
        Assert.IsTrue(logs.HasLog("orders"));
        logs.SetEnabled("orders", false, null);
        Assert.IsTrue(io.Exists(FileKeyUtility.Logger_GetSettings("orders")));
        Assert.IsFalse(io.Exists(FileKeyUtility.Logger_GetSettings("renamed-by-hand")));
        logs.Dispose();
    }

    [TestMethod]
    public void ReloadPicksUpAFileWrittenWhileRunning() {
        var io = new IOProviderMemory();
        var logs = open(io);
        Assert.IsFalse(logs.HasLog("orders"));
        orders().Save(io);
        logs.Reload();
        Assert.IsTrue(logs.HasLog("orders"));
        logs.Dispose();
    }
}
