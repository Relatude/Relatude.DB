using Relatude.DB.IO;
using Relatude.DB.Logging;

namespace Relatude.Logger;
[TestClass]
public class LogSettingsJsonTests {
    static void assertSame(LogSettings expected, LogSettings actual) {
        Assert.AreEqual(expected.Key, actual.Key);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.FileInterval, actual.FileInterval);
        Assert.AreEqual(expected.EnableLog, actual.EnableLog);
        Assert.AreEqual(expected.EnableStatistics, actual.EnableStatistics);
        Assert.AreEqual(expected.EnableLogTextFormat, actual.EnableLogTextFormat);
        Assert.AreEqual(expected.ResolutionRowStats, actual.ResolutionRowStats);
        Assert.AreEqual(expected.FirstDayOfWeek, actual.FirstDayOfWeek);
        Assert.AreEqual(expected.MaxAgeOfLogFilesInDays, actual.MaxAgeOfLogFilesInDays);
        Assert.AreEqual(expected.MaxTotalSizeOfLogFilesInMb, actual.MaxTotalSizeOfLogFilesInMb);
        Assert.AreEqual(expected.Compressed, actual.Compressed);
        Assert.AreEqual(expected.Properties.Count, actual.Properties.Count);
        foreach (var (key, p) in expected.Properties) {
            var a = actual.Properties[key];
            Assert.AreEqual(p.Name, a.Name);
            Assert.AreEqual(p.DataType, a.DataType);
            CollectionAssert.AreEqual(p.Statistics.Select(s => (s.StatisticsType, s.Resolution)).ToList(), a.Statistics.Select(s => (s.StatisticsType, s.Resolution)).ToList());
        }
    }
    static LogSettings rich() => H.RichSettings("rich", s => {
        s.Name = "Rich log";
        s.FileInterval = FileInterval.Hour;
        s.EnableLogTextFormat = true;
        s.ResolutionRowStats = 7;
        s.FirstDayOfWeek = DayOfWeek.Sunday;
        s.MaxAgeOfLogFilesInDays = 3;
        s.MaxTotalSizeOfLogFilesInMb = 42;
        s.Compressed = true;
        s.Properties["pInt"].Statistics.Add(new(StatisticsType.Count, 9));
    });

    [TestMethod]
    public void RoundTripKeepsEverySetting() {
        var s = rich();
        assertSame(s, LogSettings.FromJson(s.ToJson()));
    }
    [TestMethod]
    public void EnumsAreWrittenByName() {
        var json = rich().ToJson();
        StringAssert.Contains(json, "\"Hour\"");
        StringAssert.Contains(json, "\"Sunday\"");
        StringAssert.Contains(json, "\"CountSumAvgMinMax\"");
        StringAssert.Contains(json, "\"Integer\"");
    }
    [TestMethod]
    public void HandWrittenJsonIsAccepted() {
        // BOM, comments, trailing commas, any casing, enums by number, resolution left out
        var json = (char)0xFEFF + """
            {
              // a hand written log
              "key": "hand",
              "fileinterval": 1,
              "FirstDayOfWeek": "monday",
              "Properties": {
                "Duration": { "DataType": "Double", "Statistics": [ { "StatisticsType": "AvgMinMax" }, { "statisticsType": 0, "resolution": 5 }, ] },
                "text": { },
              },
            }
            """;
        var s = LogSettings.FromJson(json);
        Assert.AreEqual("hand", s.Key);
        Assert.AreEqual(FileInterval.Hour, s.FileInterval);
        Assert.AreEqual(DayOfWeek.Monday, s.FirstDayOfWeek);
        Assert.AreEqual(100, s.MaxAgeOfLogFilesInDays); // default kept
        Assert.IsTrue(s.EnableLog);
        var d = s.Properties["duration"]; // case insensitive again after loading
        Assert.AreEqual(LogDataType.Double, d.DataType);
        Assert.AreEqual(2, d.Statistics.Count);
        Assert.AreEqual(StatisticsType.AvgMinMax, d.Statistics[0].StatisticsType);
        Assert.AreEqual(3, d.Statistics[0].Resolution); // the constructor default
        Assert.AreEqual(StatisticsType.Count, d.Statistics[1].StatisticsType);
        Assert.AreEqual(5, d.Statistics[1].Resolution);
        Assert.AreEqual(LogDataType.String, s.Properties["TEXT"].DataType);
        Assert.AreEqual(0, s.Properties["text"].Statistics.Count);
    }
    [TestMethod]
    public void NullCollectionsBecomeEmpty() {
        var s = LogSettings.FromJson("""{ "Key": "n", "Properties": { "p": { "Statistics": null } } }""");
        Assert.AreEqual(0, s.Properties["p"].Statistics.Count);
        s = LogSettings.FromJson("""{ "Key": "n", "Properties": null }""");
        Assert.AreEqual(0, s.Properties.Count);
    }
    [TestMethod]
    public void InvalidSettingsAreRejected() {
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Name": "no key" }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a/b" }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a*" }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": " a" }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a", "FileInterval": 99 }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a", "Properties": { "p": { "DataType": 99 } } }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a", "Properties": { "p": { "Statistics": [ { "StatisticsType": 99 } ] } } }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a", "Properties": { "p": {}, "P": {} } }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("""{ "Key": "a", "Properties": { "p": null } }"""));
        Assert.Throws<ArgumentException>(() => LogSettings.FromJson("null"));
        Assert.Throws<System.Text.Json.JsonException>(() => LogSettings.FromJson("""{ "Key": "a", "FileInterval": "Fortnight" }"""));
        Assert.Throws<System.Text.Json.JsonException>(() => LogSettings.FromJson("{ not json"));
        Assert.Throws<ArgumentException>(() => new LogSettings().ToJson());
    }
    [TestMethod]
    public void SaveAndLoadThroughIOProvider() {
        var io = new IOProviderMemory();
        var s = rich();
        Assert.IsNull(LogSettings.LoadIfSaved(io, "rich"));
        s.Save(io);
        var fileKey = FileKeyUtility.Logger_GetSettings("rich");
        Assert.IsTrue(io.Exists(fileKey));
        Assert.AreEqual("Log settings", FileKeyUtility.FileTypeDescription(fileKey.AsKeyString()));
        assertSame(s, LogSettings.Load(io, fileKey));
        assertSame(s, LogSettings.LoadIfSaved(io, "rich")!);
        s.Name = "Renamed";
        s.Save(io); // overwrites
        Assert.AreEqual("Renamed", LogSettings.LoadIfSaved(io, "rich")!.Name);
        LogSettings.DeleteSaved(io, "rich");
        Assert.IsNull(LogSettings.LoadIfSaved(io, "rich"));
    }
    [TestMethod]
    public void LoadAllFindsOnlySettingsFiles() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings("a"), H.Settings("b"));
        H.RecordRichHours(store, "a");
        store.FlushToDiskNow();
        store.SaveStatistics();
        store.SaveAllSettings();
        store.Dispose();
        var all = LogSettings.LoadAll(io);
        CollectionAssert.AreEqual(new[] { "a", "b" }, all.Select(s => s.Key).ToArray());
        assertSame(H.RichSettings("a"), all[0]);
    }
    [TestMethod]
    public void DeletingLogDataKeepsSavedSettings() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        H.RecordRichHours(store);
        store.FlushToDiskNow();
        store.SaveSettings("test");
        store.DeleteLogAndStatistics("test");
        store.EnforceLimits();
        Assert.IsTrue(store.HasSavedSettings("test"));
        store.DeleteSavedSettings("test");
        Assert.IsFalse(store.HasSavedSettings("test"));
        store.Dispose();
    }
    [TestMethod]
    public void StoreFromSavedSettingsKeepsLoggingIntoTheSameLog() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        H.RecordRichHours(store);
        store.FlushToDiskNow();
        store.SaveStatistics();
        store.SaveSettings("test");
        store.Dispose();

        var reopened = LogStore.FromSavedSettings(io);
        Assert.IsTrue(reopened.HasLog("test"));
        reopened.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 100, false, out var total);
        Assert.AreEqual(6, total);
        var sum = reopened.AnalyseCombinedIntegerSums("test", "pInt", Relatude.DB.Logging.Statistics.IntervalType.Hour, H.T0, H.T0.AddHours(2));
        Assert.AreEqual(40, sum.Value); // statistics state found again: same stat file keys
        reopened.Dispose();
    }
    [TestMethod]
    public void AddLogFromJsonFileAndSavedSettings() {
        var io = new IOProviderMemory();
        var store = H.Store(io);
        var fromJson = store.AddLogFromJson(H.Settings("j").ToJson());
        Assert.AreEqual("j", fromJson.Key);
        Assert.IsTrue(store.HasLog("j"));
        Assert.IsTrue(store.Record("j", H.Entry(H.T0, ("x", 1))));

        H.RichSettings("saved").Save(io);
        store.AddLogFromSavedSettings("saved");
        Assert.IsTrue(store.HasLog("saved"));
        Assert.Throws<Exception>(() => store.AddLogFromSavedSettings("missing"));

        var folder = Path.Combine(Path.GetTempPath(), "relatude-logsettings-" + Guid.NewGuid().ToString("N"));
        try {
            var file = Path.Combine(folder, "sub", "f.json");
            store.SaveSettingsToFile("saved", file); // creates the folders
            Assert.IsFalse(File.Exists(file + ".tmp"));
            var json = File.ReadAllText(file);
            Assert.AreEqual(store.GetSettingJson("saved"), json);
            File.WriteAllText(file, json.Replace("\"saved\"", "\"fromFile\""));
            store.AddLogFromFile(file);
            Assert.IsTrue(store.HasLog("fromFile"));
            assertSame(H.RichSettings("fromFile"), store.GetSetting("fromFile"));
        } finally {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        store.Dispose();
    }
}
