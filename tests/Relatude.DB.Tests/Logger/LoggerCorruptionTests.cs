using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
[TestClass]
public class LoggerCorruptionTests {
    [TestMethod]
    public void CorruptSegmentIsSkippedOnExtract() {
        var io = new IOProviderMemory();
        var settings = H.Settings(configure: s => s.EnableStatistics = false);
        var store = H.Store(io, settings);
        // three identical records flushed separately give three identically sized segments in one file
        for (var i = 0; i < 3; i++) {
            store.Record("test", H.Entry(H.T0.AddMinutes(i), ("p1", "AAAA")), flushToDisk: true);
        }
        store.Dispose();
        var fileKey = H.FileKeys.Logger_FileNameBin("test", FileInterval.Day, H.T0.Date);
        var fileSize = io.GetFileSizeOrZeroIfUnknown(fileKey);
        Assert.IsTrue(fileSize > 0);
        Assert.AreEqual(0L, fileSize % 3);
        var segmentSize = fileSize / 3;
        io.AddCorruption(fileKey, segmentSize * 2 - 16, 16); // destroy the end marker of the middle segment
        var store2 = H.Store(io, settings);
        var records = store2.ExtractLog("test", H.T0.Date, H.T0.Date.AddDays(1), 0, int.MaxValue, false, out var total).ToList();
        Assert.AreEqual(2, total); // the two intact segments survive
        CollectionAssert.AreEqual(new[] { H.T0, H.T0.AddMinutes(2) }, records.Select(r => r.Timestamp).ToArray());
        store2.Dispose();
    }
    [TestMethod]
    public void CorruptStatisticsFileIsRestoredFromBackup() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        for (var i = 0; i < 5; i++) store.Record("test", H.Entry(H.T0.AddSeconds(i), ("pInt", 1)));
        store.SaveStatistics(); // first save, no backup exists yet
        for (var i = 0; i < 5; i++) store.Record("test", H.Entry(H.T0.AddSeconds(10 + i), ("pInt", 1)));
        store.SaveStatistics(); // second save, the backup now holds the 5 row state
        store.Dispose();
        var statKey = H.FileKeys.Logger_GetStatistics("test");
        var backupKey = H.FileKeys.Logger_GetStatisticsBackUp("test");
        var statSize = io.GetFileSizeOrZeroIfUnknown(statKey);
        Assert.IsTrue(statSize > 32);
        Assert.IsTrue(io.ExistsAndIsNotEmpty(backupKey));
        io.AddCorruption(statKey, statSize - 16, 16); // destroy the end marker
        var store2 = H.Store(io, H.RichSettings());
        var rows = store2.AnalyseCombinedRows("test", IntervalType.Hour, H.T0, H.T0.AddHours(1));
        Assert.AreEqual(5, rows.Value); // the backup taken before the last save was restored
        store2.Dispose();
    }
    [TestMethod]
    public void CorruptStatisticsAndBackupFallBackToEmptyAndCanBeRebuilt() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        for (var i = 0; i < 5; i++) store.Record("test", H.Entry(H.T0.AddSeconds(i), ("pInt", 1)));
        store.SaveStatistics();
        for (var i = 0; i < 5; i++) store.Record("test", H.Entry(H.T0.AddSeconds(10 + i), ("pInt", 1)));
        store.SaveStatistics();
        store.Dispose();
        var statKey = H.FileKeys.Logger_GetStatistics("test");
        var backupKey = H.FileKeys.Logger_GetStatisticsBackUp("test");
        io.AddCorruption(statKey, io.GetFileSizeOrZeroIfUnknown(statKey) - 16, 16);
        io.AddCorruption(backupKey, io.GetFileSizeOrZeroIfUnknown(backupKey) - 16, 16);
        var store2 = H.Store(io, H.RichSettings());
        Assert.AreEqual(0, store2.AnalyseCombinedRows("test", IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value);
        Assert.IsTrue(io.DoesNotExistOrIsEmpty(statKey)); // both corrupt files were deleted
        Assert.IsTrue(io.DoesNotExistOrIsEmpty(backupKey));
        // the log itself is untouched, so the statistics can be rebuilt from it
        store2.RebuildStatistics("test");
        Assert.AreEqual(10, store2.AnalyseCombinedRows("test", IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value);
        store2.Dispose();
    }
}
