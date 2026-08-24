using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.Utils;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

/// <summary>
/// The folder layout below the storage root (data/state/bkup/log) and the one time startup
/// migration that moves pre-layout database log files from the root into the data folder.
/// Only the log files are moved: state, index and mapper files are rebuilt at their new
/// locations, and old backups stay where they were taken. File keys are string arrays; the
/// assertions compare their joined ('/'-separated) form.
/// </summary>
[TestClass]
public class FolderLayoutTests {

    [TestMethod]
    public void FileKeys_UseTheFolderLayout() {
        var keys = new FileKeyUtility(null);
        Assert.AreEqual("data/db.00000001.bin", keys.WAL_GetFileKey(1).AsKeyString());
        Assert.AreEqual("data/db.log", keys.WAL_GetSecondaryFileKey().AsKeyString());
        Assert.AreEqual("state/state.bin", keys.StateFileKey.AsKeyString());
        Assert.AreEqual("state/index.abc.bin", keys.Index_GetFileKey("abc").AsKeyString());
        Assert.AreEqual("state/mapper.5.dll", keys.MapperDll_GetFileKey(5).AsKeyString());
        Assert.AreEqual("state/queue.bin", keys.Queue_GetFileKey("bin").AsKeyString());
        var dt = new DateTime(2026, 8, 23, 10, 30, 0, DateTimeKind.Utc);
        Assert.AreEqual("bkup/db.2026-08-23-10-30-00.bkup", keys.WAL_GetFileKeyForBackup(dt, false).AsKeyString());
        Assert.AreEqual("bkup/db.bkup.keep.2026-08-23-10-30-00.bkup", keys.WAL_GetFileKeyForBackup(dt, true).AsKeyString());
        Assert.AreEqual("bkup/files.2026-08-23-10-30-00.bkup", keys.FileStore_GetFileKeyForBackup(dt, false).AsKeyString());
        Assert.AreEqual("log/critical.error.txt", keys.CriticalErrorLogFileKey.AsKeyString());
        Assert.AreEqual("indexes/ai.cache.bin", keys.GetAiCacheFileKey(AIProviderCacheType.Sqlite)!.AsKeyString());
        Assert.AreEqual("indexes/native.ai.cache.bin", keys.GetAiCacheFileKey(AIProviderCacheType.Native)!.AsKeyString());
        // unchanged: the multi file store and the log rewrite date parsing conventions
        Assert.AreEqual("files", keys.MultiFileStoreFolderKey);
        Assert.AreEqual(dt, keys.WAL_GetBackUpDateTimeFromFileKey(keys.WAL_GetFileKeyForBackup(dt, false)));
        Assert.IsTrue(keys.WAL_KeepForever(keys.WAL_GetFileKeyForBackup(dt, true)));
        Assert.AreEqual("state/index.abc.tmp", FileKeyUtility.TempFileKey(keys.Index_GetFileKey("abc")).AsKeyString());
    }

    [TestMethod]
    public void FileKeys_KeepThePrefixOnTheFileNameInsideTheFolder() {
        // plain folder names, prefix on the file name: several instances can share one storage location
        var keys = new FileKeyUtility("abc");
        Assert.AreEqual("data/abc.db.00000001.bin", keys.WAL_GetFileKey(1).AsKeyString());
        Assert.AreEqual("state/abc.state.bin", keys.StateFileKey.AsKeyString());
        Assert.AreEqual("bkup/abc.db.2026-08-23-10-30-00.bkup", keys.WAL_GetFileKeyForBackup(new DateTime(2026, 8, 23, 10, 30, 0, DateTimeKind.Utc), false).AsKeyString());
        // the indexes folder itself is prefixed, so the ai cache file inside it is not
        Assert.AreEqual("abc.indexes/ai.cache.bin", keys.GetAiCacheFileKey(AIProviderCacheType.Sqlite)!.AsKeyString());
    }

    [TestMethod]
    public void ValidateFileKeyPath_AcceptsSegmentsAndRejectsEscapes() {
        FileKeyUtility.ValidateFileKeyPath(["db.00000001.bin"]);
        FileKeyUtility.ValidateFileKeyPath(["data", "db.00000001.bin"]);
        foreach (var bad in new string[][] { [""], ["data", ""], [".."], ["data", "..", "x"], ["data", "."], ["data/x"] }) {
            Assert.ThrowsException<ArgumentException>(() => FileKeyUtility.ValidateFileKeyPath(bad), "must reject: " + bad.AsKeyString());
        }
    }

    [TestMethod]
    public void DiskProvider_ListsRootAndSystemFolderFilesOnly() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-layout-" + Guid.NewGuid());
        try {
            var io = new IOProviderDisk(dir);
            using (var s = io.OpenAppend(["root.bin"])) s.Append([1]);
            using (var s = io.OpenAppend(["data", "db.00000001.bin"])) s.Append([1, 2]);
            using (var s = io.OpenAppend(["bkup", "db.2026-01-01-00-00-00.bkup"])) s.Append([1, 2, 3]);
            using (var s = io.OpenAppend(["other", "hidden.bin"])) s.Append([1, 2, 3, 4]); // not a system folder
            var keys = io.GetFiles().Select(f => f.Key).Order().ToArray();
            CollectionAssert.AreEqual(new[] { "bkup/db.2026-01-01-00-00-00.bkup", "data/db.00000001.bin", "root.bin" }, keys);
            Assert.IsTrue(io.Exists(["other", "hidden.bin"]), "the file is reachable by key, just not listed");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void GetAllFileKeys_ListsOnlyThisInstancesFiles() {
        var io = new IOProviderMemory();
        io.WriteAllBytes(["data", "db.00000001.bin"], [1]);
        io.WriteAllBytes(["state", "state.bin"], [1]);
        io.WriteAllBytes(["data", "abc.db.00000001.bin"], [1]);
        io.WriteAllBytes(["state", "abc.state.bin"], [1]);
        var prefixed = new FileKeyUtility("abc").GetAllFileKeys(io).Select(k => k.AsKeyString()).ToArray();
        CollectionAssert.AreEqual(new[] { "data/abc.db.00000001.bin", "state/abc.state.bin" }, prefixed);
    }

    [TestMethod]
    public void LegacyRootLogFiles_AreMovedToTheDataFolderOnStartup() {
        var io = new IOProviderMemory();
        var count = insertArticlesAndSimulateLegacyLayout(io, out var legacyKey, out var newKey);
        openAndAssertIntact(io, count);
        Assert.IsFalse(io.Exists(legacyKey));
        Assert.IsTrue(io.Exists(newKey));
    }

    [TestMethod]
    public void LegacyRootLogFiles_AreMovedByCopyWhenTheProviderCannotRename() {
        var inner = new IOProviderMemory();
        var count = insertArticlesAndSimulateLegacyLayout(inner, out var legacyKey, out var newKey);
        var io = new NoRenameIOProvider(inner);
        openAndAssertIntact(io, count);
        Assert.IsFalse(io.Exists(legacyKey), "the source must be deleted after a verified copy");
        Assert.IsTrue(io.Exists(newKey));
    }

    [TestMethod]
    public void LegacySecondaryLogFile_IsMovedToTheDataFolder() {
        var io = new IOProviderMemory();
        io.WriteAllBytes(["db.log"], [1, 2, 3]);
        using (var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io)) { }
        Assert.IsFalse(io.Exists(["db.log"]));
        Assert.AreEqual(3, io.GetFileSizeOrZeroIfUnknown(["data", "db.log"]));
    }

    [TestMethod]
    public void CrashedRewriteFlaggedWithALegacyKey_CleansUpThePartialFileInTheDataFolder() {
        // a rewrite that crashed under the pre-layout version leaves a root level partial log file
        // and a flag holding its root level key; after migration the partial file sits in the data
        // folder and must be deleted there, or it would be picked up as the latest log file
        var io = new IOProviderMemory();
        var count = insertArticlesAndSimulateLegacyLayout(io, out _, out _);
        io.WriteAllBytes(["db.00000002.bin"], [9, 9, 9]); // the partial rewrite output
        io.WriteString(["rewrite.flag"], "db.00000002.bin");
        openAndAssertIntact(io, count);
        Assert.IsFalse(io.Exists(["db.00000002.bin"]));
        Assert.IsFalse(io.Exists(["data", "db.00000002.bin"]), "the migrated partial file must be cleaned up");
        Assert.IsTrue(io.DoesNotExistOrIsEmpty(["rewrite.flag"]));
    }

    [TestMethod]
    public void HotSwappedLogFlaggedWithALegacyKey_IsNotDeleted() {
        // the hot swap completed but the flag deletion crashed: the flagged file is the only (live)
        // log file and must survive the cleanup, also when it was flagged under its root level key
        var io = new IOProviderMemory();
        var count = insertArticlesAndSimulateLegacyLayout(io, out var legacyKey, out var newKey);
        io.WriteString(["rewrite.flag"], legacyKey.AsKeyString());
        openAndAssertIntact(io, count);
        Assert.IsTrue(io.Exists(newKey), "the live log file must never be deleted");
        Assert.IsTrue(io.DoesNotExistOrIsEmpty(["rewrite.flag"]));
    }

    [TestMethod]
    public void InterruptedCopyMigration_IsCompletedOnTheNextStartup() {
        // a copy based migration that crashed between copy and delete leaves the file in both
        // places with equal sizes; the next startup finishes it by deleting the source
        var io = new IOProviderMemory();
        var count = insertArticlesAndSimulateLegacyLayout(io, out var legacyKey, out var newKey);
        io.CopyFile(io, legacyKey, newKey);
        openAndAssertIntact(io, count);
        Assert.IsFalse(io.Exists(legacyKey));
        Assert.IsTrue(io.Exists(newKey));
    }

    /// <summary>
    /// Builds a store on the io, then rearranges its files the way a pre-layout store looked on
    /// disk: the log file in the storage root and no state files. Returns the article count.
    /// </summary>
    static int insertArticlesAndSimulateLegacyLayout(IOProviderMemory io, out string[] legacyKey, out string[] newKey) {
        var keys = new FileKeyUtility(null);
        var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(100);
        foreach (var chunk in articles.Chunk(10)) store.Insert(chunk);
        store.Dispose();
        newKey = keys.WAL_GetFileKey(1);
        legacyKey = ["db.00000001.bin"];
        io.RenameFile(newKey, legacyKey);
        io.DeleteFileIfItExists(keys.StateFileKey);
        foreach (var f in keys.Index_GetAll(io)) io.DeleteFileIfItExists(f);
        return articles.Count;
    }

    static void openAndAssertIntact(IIOProvider io, int expectedCount) {
        var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        var store = new NodeStore(storeData);
        Assert.AreEqual(expectedCount, store.Query<Article>().Count());
        store.Dispose();
    }

    /// <summary>Delegates to an inner provider but cannot rename, like the Azure blob providers,
    /// forcing the migration onto its copy-verify-delete path.</summary>
    class NoRenameIOProvider(IIOProvider inner) : IIOProvider {
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public IAppendStream OpenAppend(string[] path) => inner.OpenAppend(path);
        public bool Exists(string[] path) => inner.Exists(path);
        public bool DoesNotExistOrIsEmpty(string[] path) => inner.DoesNotExistOrIsEmpty(path);
        public void DeleteFileIfItExists(string[] path) => inner.DeleteFileIfItExists(path);
        public FileMeta[] GetFiles() => inner.GetFiles();
        public long GetFileSizeOrZeroIfUnknown(string[] path) => inner.GetFileSizeOrZeroIfUnknown(path);
        public bool CanRenameFile => false;
        public void RenameFile(string[] path, string[] newPath) => throw new NotSupportedException();
        public bool CanTruncate => inner.CanTruncate;
        public void TruncateFile(string[] path, long newLength) => inner.TruncateFile(path, newLength);
        public void CloseAllOpenStreams() => inner.CloseAllOpenStreams();
        public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
        public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
        public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => inner.TryMoveIfSameDrive(fromLocalFilePath, destination);
        public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
        public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
        public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);
    }
}
