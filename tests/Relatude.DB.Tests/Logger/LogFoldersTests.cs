using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
/// <summary>
/// Every log keeps its files in a folder of its own below the log folder (log/{key}/), and the
/// definitions of custom logs are kept apart from the data, in a <see cref="LogDefinitionFolder"/>.
/// The files an older version kept directly in the log folder - data and definitions alike - are moved.
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

    [TestMethod]
    public void FilesOfTheOldFlatLayoutAreMovedIntoTheFolderOfTheirLog() {
        var io = new IOProviderMemory();
        using (var store = new LogStore(io, [orders()])) record(store, 4);
        // the layout an older version wrote: every file directly in the log folder
        foreach (var key in io.GetFiles().Select(f => f.KeyOf()).Where(k => k.Length == 3 && k[0] == "logs").ToArray()) io.RenameFile(key, ["logs", key.FileName()]);
        io.WriteAllTextUTF8(["logs", "log.gone.day.2026-01-01.bin"], "x"); // a log nothing defines any more
        io.WriteAllTextUTF8(["logs", "log.Orders.settings.json"], "{}"); // a definition: not data, moved elsewhere
        io.WriteAllTextUTF8(["logs", "critical.error.txt"], "boom");
        var said = new List<string>();
        Assert.IsTrue(LogFileLayout.MoveIntoLogFolders(io, said.Add) >= 4);
        Assert.AreEqual(0, FileKeyUtility.Logger_GetLegacyFlatFileKeys(io).Length, "nothing of the old layout is left");
        Assert.IsTrue(io.Exists(["logs", "gone", "log.gone.day.2026-01-01.bin"]), "moved whether a log is defined for it or not");
        Assert.IsTrue(io.Exists(["logs", "log.Orders.settings.json"]), "definitions are not data");
        Assert.IsTrue(io.Exists(["logs", "critical.error.txt"]));
        Assert.IsTrue(said.Any(s => s.Contains("logs/orders/")), string.Join(" | ", said));
        using var again = new LogStore(io, [orders()]);
        Assert.AreEqual(4, count(again), "the entries are read from their new place");
        Assert.AreEqual(0, LogFileLayout.MoveIntoLogFolders(io), "a second run has nothing to move");
    }

    [TestMethod]
    public void TheOldLogFolderIsMovedToLogsAndItsFlatFilesIntoTheFolderOfTheirLog() {
        var io = new IOProviderMemory();
        io.WriteAllTextUTF8(["log", "orders", "log.orders.day.2026-01-01.bin"], "in a folder already");
        io.WriteAllTextUTF8(["log", "log.flat.day.2026-01-01.bin"], "flat");
        io.WriteAllTextUTF8(["log", "critical.error.txt"], "boom");
        var said = new List<string>();
        Assert.AreEqual(4, LogFileLayout.MoveIntoLogFolders(io, said.Add), "three out of log/, then the flat one into its folder: " + string.Join(" | ", said));
        Assert.AreEqual("in a folder already", io.ReadAllTextUTF8(["logs", "orders", "log.orders.day.2026-01-01.bin"]));
        Assert.IsTrue(io.Exists(["logs", "flat", "log.flat.day.2026-01-01.bin"]));
        Assert.IsTrue(io.Exists(["logs", "critical.error.txt"]));
        Assert.IsFalse(io.GetFiles().Any(f => f.Key.StartsWith("log/")), "nothing is left in log/");
        Assert.AreEqual(0, LogFileLayout.MoveIntoLogFolders(io));
    }

    [TestMethod]
    public void OnDiskTheOldLogFolderIsRenamedWhole() {
        var root = newRoot("rename-folder");
        try {
            var io = new IOProviderDisk(root);
            using (var store = new LogStore(io, [orders()])) record(store, 3);
            // the layout an older version wrote: logs/ called log/
            Directory.Move(Path.Combine(root, "logs"), Path.Combine(root, "log"));
            Assert.IsFalse(io.GetFiles().Any(f => f.Key.StartsWith("log")), "the disk provider does not list the old folder");
            var said = new List<string>();
            Assert.IsTrue(LogFileLayout.MoveIntoLogFolders(new IOProviderDisk(root), said.Add) >= 3);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "log")));
            StringAssert.Contains(said.Single(), "Renamed the log folder");
            using var again = new LogStore(new IOProviderDisk(root), [orders()]);
            Assert.AreEqual(3, count(again));
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void OnDiskTheOldLogFolderIsMovedFileByFileWhenLogsIsThereAlready() {
        var root = newRoot("merge-folder");
        try {
            Directory.CreateDirectory(Path.Combine(root, "log", "orders"));
            File.WriteAllText(Path.Combine(root, "log", "orders", "log.orders.day.2026-01-01.bin"), "old");
            Directory.CreateDirectory(Path.Combine(root, "logs", "query"));
            File.WriteAllText(Path.Combine(root, "logs", "query", "log.query.day.2026-10-01.bin"), "new");
            var io = new IOProviderDisk(root);
            Assert.AreEqual(1, LogFileLayout.MoveIntoLogFolders(io));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(root, "logs", "orders", "log.orders.day.2026-01-01.bin")));
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(root, "logs", "query", "log.query.day.2026-10-01.bin")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "log")), "the emptied old folder is removed");
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void AMoveNeverOverwritesAFileOfAnotherSize() {
        var io = new IOProviderMemory();
        io.WriteAllTextUTF8(["logs", "orders", "log.orders.day.2026-01-01.bin"], "newer and longer");
        io.WriteAllTextUTF8(["logs", "log.orders.day.2026-01-01.bin"], "older");
        io.WriteAllTextUTF8(["logs", "orders", "log.orders.day.2026-01-02.bin"], "same");
        io.WriteAllTextUTF8(["logs", "log.orders.day.2026-01-02.bin"], "same");
        var said = new List<string>();
        LogFileLayout.MoveIntoLogFolders(io, said.Add);
        Assert.AreEqual("newer and longer", io.ReadAllTextUTF8(["logs", "orders", "log.orders.day.2026-01-01.bin"]));
        Assert.IsTrue(io.Exists(["logs", "log.orders.day.2026-01-01.bin"]), "left where it is");
        Assert.IsTrue(said.Any(s => s.Contains("another size")));
        Assert.IsFalse(io.Exists(["logs", "log.orders.day.2026-01-02.bin"]), "the copy of an interrupted move is finished off");
    }

    [TestMethod]
    public void DefinitionsAreKeptInTheirOwnFolderAndTheDataInTheLogStorage() {
        var data = new IOProviderMemory();
        var settings = new IOProviderMemory();
        var folder = new LogDefinitionFolder(settings, [], "relatude.settings/logs/db");
        var logs = new CustomLogs(data, ["system"], folder);
        logs.Create(orders());
        Assert.IsTrue(settings.Exists(["Orders.json"]));
        Assert.IsFalse(data.GetFiles().Any(f => f.Key.EndsWith(".json")), "no definition with the data");
        Assert.AreEqual("relatude.settings/logs/db/Orders.json", logs.DefinitionFileOf("Orders"));
        Assert.AreEqual("relatude.settings/logs/db", logs.DefinitionsFolder);
        logs.Record("Orders", ("amount", 1.0));
        logs.FlushToDiskNow();
        Assert.IsTrue(data.GetFiles().Any(f => f.Key.StartsWith("logs/orders/")));
        Assert.IsTrue(logs.GetFiles("Orders").All(f => f.Kind != "left-over") && logs.GetFiles("Orders").Any(f => f.Kind == "entries"));
        logs.Dispose();

        var again = new CustomLogs(data, ["system"], folder);
        Assert.AreEqual(1, count(again.LogStore));
        again.Delete("Orders", deleteRecorded: true);
        Assert.IsFalse(settings.Exists(["Orders.json"]));
        again.Dispose();
    }

    [TestMethod]
    public void AnOldDefinitionBesideTheDataIsMovedAsItIs() {
        var data = new IOProviderMemory();
        var settings = new IOProviderMemory();
        var text = "// written by hand\n" + orders().ToJson();
        data.WriteAllTextUTF8(FileKeyUtility.Logger_GetLegacySettings("Orders"), text);
        var said = new List<string>();
        var logs = new CustomLogs(data, ["system"], new LogDefinitionFolder(settings, [], "relatude.settings/logs/db"), said.Add);
        Assert.IsTrue(logs.HasLog("Orders"));
        Assert.AreEqual(text, settings.ReadAllTextUTF8(["Orders.json"]), "the text goes across as it is, comments and all");
        Assert.IsFalse(data.Exists(FileKeyUtility.Logger_GetLegacySettings("Orders")));
        Assert.AreEqual(0, logs.LoadErrors.Count);
        Assert.IsTrue(said.Any(s => s.Contains("'Orders'")), string.Join(" | ", said));
        logs.Dispose();
    }

    [TestMethod]
    public void AnOldDefinitionIsMovedWithinTheLogFolderWhenThatIsWhereDefinitionsAreKept() {
        var io = new IOProviderMemory();
        io.WriteAllTextUTF8(FileKeyUtility.Logger_GetLegacySettings("Orders"), orders().ToJson());
        var logs = new CustomLogs(io, ["system"]);
        Assert.IsTrue(logs.HasLog("Orders"));
        Assert.IsTrue(io.Exists(["logs", "Orders.json"]));
        Assert.IsFalse(io.Exists(FileKeyUtility.Logger_GetLegacySettings("Orders")));
        Assert.AreEqual(0, logs.LoadErrors.Count);
        logs.Dispose();
    }

    [TestMethod]
    public void AnOldCopyOfADefinitionThatWasMovedBeforeIsListedAndCanBeDeleted() {
        var data = new IOProviderMemory();
        var settings = new IOProviderMemory();
        settings.WriteAllTextUTF8(["Orders.json"], orders(s => s.Name = "The one read").ToJson());
        data.WriteAllTextUTF8(FileKeyUtility.Logger_GetLegacySettings("Orders"), orders(s => s.Name = "An older copy").ToJson());
        var logs = new CustomLogs(data, ["system"], new LogDefinitionFolder(settings, [], "relatude.settings/logs/db"));
        Assert.AreEqual("The one read", logs.GetDefinition("Orders")!.Name);
        Assert.AreEqual(1, logs.LoadErrors.Count);
        var error = logs.LoadErrors[0];
        Assert.AreEqual("logs/log.Orders.settings.json", error.FileKey);
        StringAssert.Contains(error.Message, "older copy");
        StringAssert.Contains(logs.ReadBrokenDefinition(error.FileKey), "An older copy");
        logs.DeleteBrokenDefinition(error.FileKey);
        Assert.IsFalse(data.Exists(FileKeyUtility.Logger_GetLegacySettings("Orders")));
        Assert.AreEqual(0, logs.LoadErrors.Count);
        Assert.IsTrue(settings.Exists(["Orders.json"]), "the one read is untouched");
        logs.Dispose();
    }

    [TestMethod]
    public void AnOldDefinitionThatCannotBeMovedIsReadWhereItIsAndMovedByTheNextSave() {
        var data = new IOProviderMemory();
        var settings = new FailingWrites(new IOProviderMemory());
        data.WriteAllTextUTF8(FileKeyUtility.Logger_GetLegacySettings("Orders"), orders().ToJson());
        var said = new List<string>();
        var logs = new CustomLogs(data, ["system"], new LogDefinitionFolder(settings, [], "relatude.settings/logs/db"), said.Add);
        Assert.IsTrue(logs.HasLog("Orders"), "read where it is");
        Assert.IsTrue(data.Exists(FileKeyUtility.Logger_GetLegacySettings("Orders")), "and left there");
        Assert.IsTrue(said.Any(s => s.Contains("Could not move")), string.Join(" | ", said));
        settings.Fail = false;
        logs.SetEnabled("Orders", log: false, statistics: null);
        Assert.IsTrue(settings.Exists(["Orders.json"]), "the next save writes it where it belongs...");
        Assert.IsFalse(data.Exists(FileKeyUtility.Logger_GetLegacySettings("Orders")), "...and removes the old one");
        logs.Dispose();
    }

    /// <summary>A provider whose writes fail while <see cref="Fail"/> is set: a read-only deployment, say.</summary>
    sealed class FailingWrites(IIOProvider inner) : IIOProvider {
        public bool Fail { get; set; } = true;
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public IAppendStream OpenAppend(string[] path) => Fail ? throw new UnauthorizedAccessException("read-only") : inner.OpenAppend(path);
        public bool Exists(string[] path) => inner.Exists(path);
        public bool DoesNotExistOrIsEmpty(string[] path) => inner.DoesNotExistOrIsEmpty(path);
        public void DeleteFileIfItExists(string[] path) => inner.DeleteFileIfItExists(path);
        public FileMeta[] GetFiles() => inner.GetFiles();
        public long GetFileSizeOrZeroIfUnknown(string[] path) => inner.GetFileSizeOrZeroIfUnknown(path);
        public bool CanRenameFile => inner.CanRenameFile;
        public void RenameFile(string[] path, string[] newPath) => inner.RenameFile(path, newPath);
        public bool CanRenameFolder => inner.CanRenameFolder;
        public void RenameFolder(string[] path, string[] newPath) => inner.RenameFolder(path, newPath);
        public bool SupportsEmptyFolders => inner.SupportsEmptyFolders;
        public bool CanTruncate => inner.CanTruncate;
        public void TruncateFile(string[] path, long newLength) => inner.TruncateFile(path, newLength);
        public void CloseAllOpenStreams() => inner.CloseAllOpenStreams();
        public bool TryGetLocalFilePath(string[] path, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
        public bool TryGetLocalFolderPath(string[] path, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
        public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => inner.TryMoveIfSameDrive(fromLocalFilePath, destination);
        public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
        public bool DeleteFolderIfEmpty(string[] path) => inner.DeleteFolderIfEmpty(path);
        public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
        public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);
    }
}
