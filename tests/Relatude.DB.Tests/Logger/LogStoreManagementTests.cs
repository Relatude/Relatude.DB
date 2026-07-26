using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
[TestClass]
public class LogStoreManagementTests {
    [TestMethod]
    public void HasLogAndRecordAreCaseInsensitive() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings("Test"));
        Assert.IsTrue(store.HasLog("Test"));
        Assert.IsTrue(store.HasLog("test"));
        Assert.IsTrue(store.HasLog("TEST"));
        Assert.IsFalse(store.HasLog("nope"));
        Assert.IsTrue(store.Record("tEsT", H.Entry(H.T0, ("n", 1))));
        store.ExtractLog("TEST", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(1, total);
        store.Dispose();
    }
    [TestMethod]
    public void IsEnabledReflectsSettings() {
        var io = new IOProviderMemory();
        var on = H.Settings("on");
        var off = H.Settings("off", s => { s.EnableLog = false; s.EnableStatistics = false; });
        var statsOnly = H.Settings("statsOnly", s => s.EnableLog = false);
        var store = H.Store(io, on, off, statsOnly);
        Assert.IsTrue(store.IsEnabled("on"));
        Assert.IsFalse(store.IsEnabled("off"));
        Assert.IsTrue(store.IsEnabled("statsOnly"));
        Assert.IsFalse(store.IsEnabled("unknown"));
        Assert.IsTrue(on.IsEnabled());
        Assert.IsFalse(off.IsEnabled());
        store.Dispose();
    }
    [TestMethod]
    public void GetSettingReturnsSettingsAndThrowsForUnknownKey() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings("a"), H.Settings("b"));
        Assert.AreEqual("a", store.GetSetting("a").Key);
        Assert.ThrowsException<Exception>(() => store.GetSetting("nope"));
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, store.GetSettings().Select(s => s.Key).ToArray());
        store.Dispose();
    }
    [TestMethod]
    public void AddLogMakesNewLogAvailable() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings("a"));
        store.AddLog(H.Settings("b"));
        Assert.IsTrue(store.HasLog("b"));
        Assert.AreEqual(2, store.GetSettings().Count());
        Assert.IsTrue(store.Record("b", H.Entry(H.T0, ("n", 1))));
        store.ExtractLog("b", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(1, total);
        Assert.ThrowsException<ArgumentException>(() => store.AddLog(H.Settings("B"))); // duplicate, keys are case insensitive
        store.Dispose();
    }
    [TestMethod]
    public void GetAvailableStatisticsByPropertyListsConfiguredStatistics() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        var byProp = store.GetAvailableStatisticsByProperty("test");
        Assert.AreEqual(4, byProp.Count);
        Assert.AreEqual(4, byProp["pInt"].Count);
        Assert.AreEqual(3, byProp["pDouble"].Count);
        Assert.AreEqual(1, byProp["pGroup"].Count);
        Assert.IsTrue(byProp["pUnique"].Any(i => i.StatisticsType == StatisticsType.UniqueCountEstimate));
        Assert.AreEqual(0, store.GetAvailableStatisticsByProperty("nope").Count);
        store.Dispose();
    }
    [TestMethod]
    public void FileSizesSplitLogAndStatistics() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        H.RecordRichHours(store);
        Assert.AreEqual(0, store.GetStatisticsFileSize("test")); // nothing saved yet
        store.FlushToDiskNow("test");
        store.FlushToDiskNow();
        store.SaveStatistics();
        var totalSize = store.GetFileSize("test");
        var logSize = store.GetLogFileSize("test");
        var statSize = store.GetStatisticsFileSize("test");
        Assert.IsTrue(logSize > 0);
        Assert.IsTrue(statSize > 0);
        Assert.AreEqual(totalSize, logSize + statSize);
        // unknown keys report zero
        Assert.AreEqual(0, store.GetFileSize("nope"));
        Assert.AreEqual(0, store.GetLogFileSize("nope"));
        Assert.AreEqual(0, store.GetStatisticsFileSize("nope"));
        store.Dispose();
    }
    [TestMethod]
    public void DeleteLogOlderThanDeletesWholeFilesOnly() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => s.FileInterval = FileInterval.Minute));
        var t1 = H.T0.AddSeconds(10);
        var t2 = H.T0.AddSeconds(70);
        var t3 = H.T0.AddSeconds(130);
        foreach (var t in new[] { t1, t2, t3 }) store.Record("test", H.Entry(t, ("n", 1)));
        Assert.AreEqual(t1, store.GetTimestampOfFirstRecord("test"));
        Assert.AreEqual(t3, store.GetTimestampOfLastRecord("test"));
        store.DeleteLogOlderThan("test", H.T0.AddMinutes(1)); // only the first minute-file ends at or before this
        Assert.AreEqual(t2, store.GetTimestampOfFirstRecord("test"));
        store.ExtractLog("test", H.T0, H.T0.AddMinutes(10), 0, 10, false, out var total);
        Assert.AreEqual(2, total);
        store.Dispose();
    }
    [TestMethod]
    public void DeleteLogRemovesAllLogFiles() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        store.Record("test", H.Entry(H.T0, ("n", 1)));
        store.DeleteLog("test");
        Assert.AreEqual(0, store.GetLogFileSize("test"));
        Assert.IsNull(store.GetTimestampOfFirstRecord("test"));
        Assert.IsNull(store.GetTimestampOfLastRecord("test"));
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(0, total);
        store.Dispose();
    }
    [TestMethod]
    public void DeleteStatisticsResetsStatisticsButKeepsLog() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        H.RecordRichHours(store);
        store.SaveStatistics();
        Assert.IsTrue(store.GetStatisticsFileSize("test") > 0);
        Assert.AreEqual(6, store.AnalyseCombinedRows("test", IntervalType.Hour, H.T0, H.T0.AddHours(2)).Value);
        store.DeleteStatistics("test");
        Assert.AreEqual(0, store.GetStatisticsFileSize("test"));
        Assert.AreEqual(0, store.AnalyseCombinedRows("test", IntervalType.Hour, H.T0, H.T0.AddHours(2)).Value);
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(6, total); // log untouched
        store.Dispose();
    }
    [TestMethod]
    public void DeleteLogAndStatisticsRemovesBoth() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        H.RecordRichHours(store);
        store.SaveStatistics();
        store.DeleteLogAndStatistics("test");
        Assert.AreEqual(0, store.GetFileSize("test"));
        Assert.AreEqual(0, store.AnalyseCombinedRows("test", IntervalType.Hour, H.T0, H.T0.AddHours(2)).Value);
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(0, total);
        store.Dispose();
    }
    [TestMethod]
    public void DeleteAllRemovesEverythingForAllLogs() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings("a"), H.Settings("b"));
        store.Record("a", H.Entry(H.T0, ("n", 1)));
        store.Record("b", H.Entry(H.T0, ("n", 2)));
        store.SaveStatistics();
        store.DeleteAll();
        foreach (var key in new[] { "a", "b" }) {
            Assert.AreEqual(0, store.GetFileSize(key));
            store.ExtractLog(key, H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
            Assert.AreEqual(0, total);
        }
        store.Dispose();
    }
    [TestMethod]
    public void EnforceLimitsDeletesFilesOlderThanMaxAge() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => { s.MaxAgeOfLogFilesInDays = 1; s.MaxTotalSizeOfLogFilesInMb = 0; }));
        var oldTs = DateTime.UtcNow.AddDays(-3);
        var newTs = DateTime.UtcNow;
        store.Record("test", H.Entry(oldTs, ("n", 1)));
        store.Record("test", H.Entry(newTs, ("n", 2)));
        var from = DateTime.UtcNow.AddDays(-10);
        var until = DateTime.UtcNow.AddDays(1);
        store.ExtractLog("test", from, until, 0, 10, false, out var totalBefore);
        Assert.AreEqual(2, totalBefore);
        store.EnforceLimits();
        store.ExtractLog("test", from, until, 0, 10, false, out var totalAfter);
        Assert.AreEqual(1, totalAfter);
        Assert.AreEqual(newTs, store.GetTimestampOfFirstRecord("test"));
        store.Dispose();
    }
    [TestMethod]
    public void EnforceLimitsDeletesOldestFilesWhenOverSizeLimit() {
        runSizeLimitScenario(maxAgeDays: 10000);
    }
    [TestMethod]
    public void EnforceLimitsAppliesSizeLimitEvenWhenAgeLimitIsDisabled() {
        runSizeLimitScenario(maxAgeDays: 0);
    }
    static void runSizeLimitScenario(int maxAgeDays) {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => { s.MaxAgeOfLogFilesInDays = maxAgeDays; s.MaxTotalSizeOfLogFilesInMb = 1; }));
        var payload = new string('x', 4000);
        // noon avoids day-boundary crossings no matter when the test runs; the size check
        // must use UtcNow based dates since the current interval file is never deleted
        var day2 = DateTime.UtcNow.AddDays(-2).Date.AddHours(12);
        var day1 = DateTime.UtcNow.AddDays(-1).Date.AddHours(12);
        for (var i = 0; i < 220; i++) store.Record("test", H.Entry(day2.AddSeconds(i), ("p", payload))); // ~0.9 mb
        for (var i = 0; i < 220; i++) store.Record("test", H.Entry(day1.AddSeconds(i), ("p", payload))); // ~0.9 mb
        store.Record("test", H.Entry(DateTime.UtcNow, ("p", "current")));
        store.EnforceLimits(); // ~1.8 mb total, limit 1 mb: only the oldest full day-file fits the cut
        var day2Key = H.FileKeys.Logger_FileNameBin("test", FileInterval.Day, day2.Date);
        var day1Key = H.FileKeys.Logger_FileNameBin("test", FileInterval.Day, day1.Date);
        Assert.IsTrue(io.DoesNotExistOrIsEmpty(day2Key));
        Assert.IsTrue(io.ExistsAndIsNotEmpty(day1Key));
        Assert.AreEqual(day1, store.GetTimestampOfFirstRecord("test"));
        store.Dispose();
    }
    [TestMethod]
    public void SaveStatsAndDeleteExpiredDataDoesBoth() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => { s.MaxAgeOfLogFilesInDays = 1; s.MaxTotalSizeOfLogFilesInMb = 0; }));
        store.Record("test", H.Entry(DateTime.UtcNow.AddDays(-3), ("n", 1)));
        store.Record("test", H.Entry(DateTime.UtcNow, ("n", 2)));
        store.SaveStatsAndDeleteExpiredData();
        Assert.IsTrue(store.GetStatisticsFileSize("test") > 0); // statistics saved
        store.ExtractLog("test", DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(1, total); // expired file deleted
        store.Dispose();
    }
    [TestMethod]
    public void LogSettingsDefaultsAndStatisticsInfoResolutionClamp() {
        var s = new LogSettings();
        Assert.IsTrue(s.EnableLog);
        Assert.IsTrue(s.EnableStatistics);
        Assert.IsFalse(s.EnableLogTextFormat);
        Assert.IsTrue(s.IsEnabled());
        Assert.AreEqual(FileInterval.Day, s.FileInterval);
        Assert.AreEqual(1, new StatisticsInfo(StatisticsType.Count, 0).Resolution); // clamped to at least 1
        Assert.AreEqual(5, new StatisticsInfo(StatisticsType.Count, 5).Resolution);
        Assert.AreEqual(3, new StatisticsInfo(StatisticsType.Count).Resolution); // default
    }
}
