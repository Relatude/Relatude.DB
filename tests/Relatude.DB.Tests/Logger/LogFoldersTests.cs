using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
/// <summary>
/// Every log keeps its files in a folder of its own below the log folder (logs/{key}/), and the
/// definitions of custom logs are kept apart from the data, in two folders (<see cref="LayeredDefinitionFolders"/>):
/// SETTINGS, deployed with the application, and DATA, this installation's, where every change is saved.
/// </summary>
[TestClass]
public class LogFoldersTests {
    static readonly DateTime from = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    static readonly DateTime to = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    static LogSettings orders(Action<LogSettings>? configure = null) {
        var s = new LogSettings {
            Key = "Orders",
            Name = "Orders",
            FileInterval = FileInterval.Day,
            FirstDayOfWeek = DayOfWeek.Monday,
            EnableLogTextFormat = true,
        };
        s.Properties.Add("amount", new LogProperty { Name = "Amount", DataType = LogDataType.Double, Statistics = [new(StatisticsType.CountSumAvgMinMax)] });
        configure?.Invoke(s);
        return s;
    }
    static int count(ILogStore store, string key = "Orders") {
        store.ExtractLog(key, from, to, 0, 1, false, out var total);
        return total;
    }
    static void record(ILogStore store, int n) {
        for (var i = 0; i < n; i++) {
            var entry = new LogEntry();
            entry.Values["amount"] = (double)i;
            store.Record("Orders", entry);
        }
        store.FlushToDiskNow();
    }
    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-log-folders-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [TestMethod]
    public void EveryFileOfALogIsInTheFolderNamedAfterItsKey() {
        var io = new IOProviderMemory();
        using (var store = new LogStore(io, [orders()])) {
            record(store, 3);
        }
        var keys = io.GetFiles().Select(f => f.Key).Where(k => k.StartsWith("logs/")).ToArray();
        Assert.IsTrue(keys.Length >= 3, "entries, text copy and statistics: " + string.Join(", ", keys));
        foreach (var key in keys) StringAssert.StartsWith(key, "logs/orders/log.", "the folder is the key lowercased, the file names still carry the key: " + key);
        Assert.IsTrue(io.Exists(FileKeyUtility.Logger_GetStatistics("Orders")));
        Assert.AreEqual("logs/orders/log.Orders.statistics.bin", FileKeyUtility.Logger_GetStatistics("Orders").AsKeyString());
        var folderDescription = typeof(FileKeyUtility).GetMethod("FolderTypeDescription", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.AreEqual("Files of one log", folderDescription.Invoke(null, ["logs/orders"]));
        Assert.AreEqual("Log file", FileKeyUtility.FileTypeDescription(keys[0]));
    }

    [TestMethod]
    public void TheDiskProviderListsTheFilesInTheFolderOfEachLog() {
        var root = newRoot("disk");
        try {
            var io = new IOProviderDisk(root);
            using (var store = new LogStore(io, [orders()])) record(store, 5);
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "logs", "orders")));
            Assert.IsTrue(io.GetFiles().Any(f => f.Key.StartsWith("logs/orders/log.orders.day.")), "listed, so the log finds its files again");
            using var again = new LogStore(new IOProviderDisk(root), [orders()]);
            Assert.AreEqual(5, count(again));
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static LayeredDefinitionFolders folders(IIOProvider settings, IIOProvider data)
        => new(new DefinitionFolder(settings, [], "relatude.settings/logs"), new DefinitionFolder(data, ["overrides", "logs"], "relatude.data/overrides/logs"));

    [TestMethod]
    public void DefinitionsAreKeptApartFromTheData_AndWhatIsSavedHereGoesToData() {
        var data = new IOProviderMemory();
        var settings = new IOProviderMemory();
        var logs = new CustomLogs(data, ["system"], folders(settings, data));
        logs.Create(orders());
        Assert.IsTrue(data.Exists(["overrides", "logs", "Orders.json"]), "saved in DATA");
        Assert.IsFalse(settings.GetFiles().Any(), "SETTINGS is never written by a save");
        Assert.AreEqual("relatude.data/overrides/logs/Orders.json", logs.DefinitionFileOf("Orders"));
        Assert.AreEqual(DefinitionSource.Data, logs.SourceOf("Orders"));
        Assert.AreEqual("relatude.settings/logs", logs.SettingsFolder);
        Assert.AreEqual(DataChange.Added, logs.DataEntries.Single().Change);
        logs.Record("Orders", ("amount", 1.0));
        logs.FlushToDiskNow();
        Assert.IsTrue(data.GetFiles().Any(f => f.Key.StartsWith("logs/orders/")));
        Assert.IsTrue(logs.GetFiles("Orders").All(f => f.Kind != "left-over") && logs.GetFiles("Orders").Any(f => f.Kind == "entries"));
        logs.Dispose();

        var again = new CustomLogs(data, ["system"], folders(settings, data));
        Assert.AreEqual(1, count(again.LogStore));
        again.Delete("Orders", deleteRecorded: true);
        Assert.IsFalse(data.Exists(["overrides", "logs", "Orders.json"]), "only DATA had it: the file goes");
        Assert.AreEqual(0, again.DataEntries.Count);
        again.Dispose();
    }

    [TestMethod]
    public void DataReplacesSettings_AndDeletingASettingsLogLeavesAMarkerInData() {
        var data = new IOProviderMemory();
        var settings = new IOProviderMemory();
        settings.WriteAllTextUTF8(["Orders.json"], "// by hand\n" + orders(s => s.Name = "From settings").ToJson());
        var logs = new CustomLogs(data, ["system"], folders(settings, data));
        Assert.AreEqual("From settings", logs.GetDefinition("Orders")!.Name);
        Assert.AreEqual(DefinitionSource.Settings, logs.SourceOf("Orders"));
        Assert.AreEqual(0, logs.DataEntries.Count);

        logs.SetEnabled("Orders", log: false, statistics: null);
        Assert.AreEqual(DefinitionSource.Data, logs.SourceOf("Orders"), "a change made here is DATA's");
        Assert.AreEqual(DataChange.Changed, logs.DataEntries.Single().Change);
        StringAssert.StartsWith(settings.ReadAllTextUTF8(["Orders.json"]), "// by hand", "SETTINGS keeps its file, comments and all");
        logs.SetEnabled("Orders", log: true, statistics: null);
        Assert.AreEqual(DefinitionSource.Settings, logs.SourceOf("Orders"), "saying what SETTINGS says is not kept twice");
        Assert.IsFalse(data.Exists(["overrides", "logs", "Orders.json"]));

        logs.Delete("Orders", deleteRecorded: false);
        Assert.IsFalse(logs.HasLog("Orders"));
        Assert.IsTrue(settings.Exists(["Orders.json"]), "SETTINGS is not this installation's to delete");
        StringAssert.Contains(data.ReadAllTextUTF8(["overrides", "logs", "Orders.json"]), LayeredDefinitions.RemovedMarker);
        Assert.AreEqual(DataChange.Removed, logs.DataEntries.Single().Change);
        logs.Dispose();
        var again = new CustomLogs(data, ["system"], folders(settings, data));
        Assert.IsFalse(again.HasLog("Orders"), "taken away at the next start too");
        Assert.AreEqual(0, again.LoadErrors.Count);
        again.Dispose();
    }

    [TestMethod]
    public void MovingIntoSettings_ChangesNothingThatRuns_AndDiscardingGoesBackToSettings() {
        var data = new IOProviderMemory();
        var settings = new IOProviderMemory();
        settings.WriteAllTextUTF8(["Orders.json"], orders(s => s.Name = "From settings").ToJson());
        var logs = new CustomLogs(data, ["system"], folders(settings, data));
        var changed = orders(s => s.Name = "Changed here");
        logs.Update(changed);
        var added = orders(s => { s.Key = "Refunds"; s.Name = "Refunds"; });
        logs.Create(added);
        Assert.AreEqual(2, logs.DataEntries.Count);

        // discarding the change puts SETTINGS back; discarding the new log takes it away, its data kept
        record(logs.LogStore, 2);
        var discarded = logs.DiscardData(["Orders", "Refunds", "nothing-there"]);
        CollectionAssert.AreEquivalent(new[] { "Orders", "Refunds" }, discarded.ToArray());
        Assert.AreEqual("From settings", logs.GetDefinition("Orders")!.Name);
        Assert.IsFalse(logs.HasLog("Refunds"));
        Assert.AreEqual(2, count(logs.LogStore), "what Orders recorded is kept");
        Assert.AreEqual(0, logs.DataEntries.Count);

        // a change moved into SETTINGS: the same definition, now from there
        logs.Update(changed);
        logs.Delete("Orders", deleteRecorded: false);
        logs.Create(added);
        Assert.AreEqual(2, logs.MoveToSettings(["Orders", "Refunds"]));
        Assert.IsFalse(settings.Exists(["Orders.json"]), "the marker took the SETTINGS file away");
        Assert.AreEqual("Refunds", LogSettings.FromJson(settings.ReadAllTextUTF8(["Refunds.json"])).Name);
        Assert.AreEqual(0, logs.DataEntries.Count);
        Assert.AreEqual(DefinitionSource.Settings, logs.SourceOf("Refunds"));
        Assert.IsFalse(data.GetFiles().Any(f => f.Key.StartsWith("overrides/")), "DATA holds nothing more");
        logs.Dispose();
    }

    [TestMethod]
    public void OnDiskTheDataFolderIsReadBackToo() {
        // the disk provider lists only some folders; overrides/logs/ has to be among them, or a change saved
        // here would be written and never read again
        var root = newRoot("layers");
        try {
            var settings = new IOProviderDisk(Path.Combine(root, "relatude.settings"), plainFolder: true);
            var data = new IOProviderDisk(Path.Combine(root, "relatude.data"));
            settings.WriteAllTextUTF8(["logs", "Orders.json"], orders(s => s.Name = "From settings").ToJson());
            var definitions = new LayeredDefinitionFolders(new DefinitionFolder(settings, ["logs"], "relatude.settings/logs"), new DefinitionFolder(data, ["overrides", "logs"], "relatude.data/overrides/logs"));
            var logs = new CustomLogs(data, ["system"], definitions);
            logs.SetEnabled("Orders", log: false, statistics: null);
            Assert.IsTrue(File.Exists(Path.Combine(root, "relatude.data", "overrides", "logs", "Orders.json")));
            Assert.AreEqual(DefinitionSource.Data, logs.SourceOf("Orders"), "read back from DATA");
            Assert.AreEqual(1, logs.DataEntries.Count);
            logs.Dispose();
            var again = new CustomLogs(new IOProviderDisk(Path.Combine(root, "relatude.data")), ["system"], definitions);
            Assert.IsFalse(again.GetDefinition("Orders")!.EnableLog, "and at the next start");
            Assert.AreEqual(1, again.DiscardData(["Orders"]).Count);
            Assert.IsTrue(again.GetDefinition("Orders")!.EnableLog);
            again.Dispose();
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void WithoutASettingsFolderTheDefinitionsAreInTheLogFolder() {
        var io = new IOProviderMemory();
        var logs = new CustomLogs(io, ["system"]);
        logs.Create(orders());
        Assert.IsTrue(io.Exists(["logs", "Orders.json"]));
        Assert.IsNull(logs.SettingsFolder);
        Assert.AreEqual("logs", logs.DataFolder);
        logs.Dispose();
    }
}
