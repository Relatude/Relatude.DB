using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using Relatude.Utils;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

/// <summary>
/// The automatic backup with a backup provider of its own (IoBackup): whether this hour's backup
/// exists, which backups the retention rules prune, and what a backup that failed or was cut short
/// leaves behind are all questions about the backup provider, never the database's own.
/// </summary>
[TestClass]
public class AutoBackupTests {

    [TestMethod]
    public async Task ThisHoursBackup_IsLookedForInTheBackupProvider() {
        var dbIo = new IOProviderMemory();
        var backupIo = new IOProviderMemory();
        var hour = DateTime.UtcNow.Hour;
        using var store = open(dbIo, backupIo, out var data);
        var articles = Helper.GenerateArticles(30);
        store.Insert(articles);

        data.RunAutoBackupPass();
        Assert.AreEqual(1, data.TaskQueue.CountTasks(BatchState.Pending), "no backup yet this hour: one is due");
        await runTasks(data, expectError: false);
        var backups = FileKeyUtility.WAL_GetAllBackUpFileKeys(backupIo);
        Assert.AreEqual(1, backups.Length, "the backup is written to the backup provider");
        Assert.AreEqual(0, FileKeyUtility.WAL_GetAllBackUpFileKeys(dbIo).Length, "and not to the database's own");

        data.RunAutoBackupPass();
        if (DateTime.UtcNow.Hour != hour) Assert.Inconclusive("the hour changed during the test");
        Assert.AreEqual(0, data.TaskQueue.CountTasks(BatchState.Pending), "this hour's backup is found where it was written, so no second one is queued");

        // the backup is a whole database
        var restoredIo = new IOProviderMemory();
        backupIo.CopyFile(restoredIo, backups[0], FileKeyUtility.WAL_GetFileKey(1));
        using var restored = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), null, restoredIo));
        Assert.AreEqual(articles.Count, restored.Query<Article>().Count());
    }

    [TestMethod]
    public void Retention_PrunesTheBackupProviderOnly() {
        var dbIo = new IOProviderMemory();
        var backupIo = new IOProviderMemory();
        var expired = FileKeyUtility.WAL_GetFileKeyForBackup(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), false);
        var keepForever = FileKeyUtility.WAL_GetFileKeyForBackup(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), true);
        var thisHour = FileKeyUtility.WAL_GetFileKeyForBackup(DateTime.UtcNow, false);
        backupIo.WriteAllBytes(expired, [1, 2, 3]);
        backupIo.WriteAllBytes(keepForever, [1, 2, 3]);
        backupIo.WriteAllBytes(thisHour, [1, 2, 3]);
        dbIo.WriteAllBytes(expired, [1, 2, 3]); // left on the database's storage from before it had a backup provider
        using var store = open(dbIo, backupIo, out var data);

        data.RunAutoBackupPass();

        Assert.IsFalse(backupIo.Exists(expired), "an expired backup is pruned from the backup provider");
        Assert.IsTrue(backupIo.Exists(keepForever), "a keep-forever backup never expires");
        Assert.IsTrue(backupIo.Exists(thisHour));
        Assert.IsTrue(dbIo.Exists(expired), "the database's own storage is not the backup provider and is left alone");
    }

    [TestMethod]
    public async Task BackupCutShort_IsCleanedUpInTheBackupProviderAtStartup() {
        var dbIo = new IOProviderMemory();
        var backupIo = new IOProviderMemory();
        var partial = FileKeyUtility.WAL_GetFileKeyForBackup(DateTime.UtcNow, false);
        backupIo.WriteAllBytes(partial, [1, 2, 3]);
        using (var flag = backupIo.OpenAppend(["rewrite.flag"])) flag.WriteString(partial.AsKeyString());
        var unrelatedState = FileKeyUtility.State_GetFileKey(1);
        backupIo.WriteAllBytes(unrelatedState, [1, 2, 3]);
        using var store = open(dbIo, backupIo, out var data, truncateBackups: true);

        Assert.IsFalse(backupIo.Exists(["rewrite.flag"]), "the flag of the backup cut short is removed");
        Assert.IsFalse(backupIo.Exists(partial), "and so is its half written file");
        Assert.IsTrue(backupIo.Exists(unrelatedState), "a backup says nothing about state files");

        store.Insert(Helper.GenerateArticles(10));
        data.RunAutoBackupPass();
        await runTasks(data, expectError: false); // refused as "already in progress" while the flag was there
        Assert.AreEqual(1, FileKeyUtility.WAL_GetAllBackUpFileKeys(backupIo).Length);
    }

    [TestMethod]
    [DataRow(true, false, DisplayName = "Rewritten (TruncateBackups), failing while writing")]
    [DataRow(true, true, DisplayName = "Rewritten (TruncateBackups), failing at the start")]
    [DataRow(false, true, DisplayName = "Copied")]
    public async Task FailedBackup_LeavesTheDatabaseOpenAndTheNextBackupWorks(bool truncateBackups, bool failAppends) {
        var dbIo = new IOProviderMemory();
        var backupIo = new FailingBackupWritesIO(new IOProviderMemory());
        using var store = open(dbIo, backupIo, out var data, truncateBackups);
        var articles = Helper.GenerateArticles(35); // generated alike every time: the later ones are taken from the same list
        store.Insert(articles.Take(30));

        // a rewrite writes the file's header as it starts and flushes once the snapshot goes in; a
        // copy fails at its first write either way
        if (failAppends) backupIo.FailAppends = true;
        else backupIo.FailFlushes = true;
        data.RunAutoBackupPass();
        await runTasks(data, expectError: true);
        Assert.AreEqual(DataStoreState.Open, data.State, "a backup storage that fails fails the backup, not the database");
        Assert.AreEqual(0, FileKeyUtility.WAL_GetAllBackUpFileKeys(backupIo).Length, "no half written backup is left to pass for this hour's");
        Assert.IsFalse(backupIo.Exists(["rewrite.flag"]));

        backupIo.FailFlushes = backupIo.FailAppends = false;
        store.Insert(articles.Skip(30));
        data.RunAutoBackupPass();
        await runTasks(data, expectError: false);
        Assert.AreEqual(1, FileKeyUtility.WAL_GetAllBackUpFileKeys(backupIo).Length, "the next backup is not refused");
        Assert.AreEqual(articles.Count, store.Query<Article>().Count());
    }

    [TestMethod]
    public async Task FlagAFailedBackupCouldNotRemove_DoesNotBlockTheNextBackup() {
        // the storage was still failing when the backup tried to clean up after itself: the next
        // backup, not the next start, is what removes the leftovers
        var dbIo = new IOProviderMemory();
        var backupIo = new IOProviderMemory();
        using var store = open(dbIo, backupIo, out var data, truncateBackups: true);
        store.Insert(Helper.GenerateArticles(10));
        var partial = FileKeyUtility.WAL_GetFileKeyForBackup(DateTime.UtcNow.AddHours(-2), false);
        backupIo.WriteAllBytes(partial, [1, 2, 3]);
        using (var flag = backupIo.OpenAppend(["rewrite.flag"])) flag.WriteString(partial.AsKeyString());

        data.RunAutoBackupPass();
        await runTasks(data, expectError: false);

        Assert.IsFalse(backupIo.Exists(["rewrite.flag"]));
        Assert.IsFalse(backupIo.Exists(partial), "the half written file the flag named goes with it");
        Assert.AreEqual(1, FileKeyUtility.WAL_GetAllBackUpFileKeys(backupIo).Length);
    }

    static NodeStore open(IIOProvider dbIo, IIOProvider backupIo, out DataStoreLocal data, bool truncateBackups = false) {
        // AutoBackUp stays off: the tests run the backup pass themselves, and the queue too
        var settings = new SettingsLocal { AutoDequeTasks = false, TruncateBackups = truncateBackups };
        data = DataStoreLocal.Open(Helper.GetDatamodel(), settings, dbIo, bkup: backupIo);
        return new NodeStore(data);
    }

    static async Task runTasks(DataStoreLocal data, bool expectError) {
        var results = await data.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
        Assert.AreEqual(1, results.Length, "one backup batch");
        if (expectError) Assert.IsNotNull(results[0].Error, "the backup was expected to fail");
        else Assert.IsNull(results[0].Error, "the backup failed: " + results[0].Error);
    }

    /// <summary>Delegates to an inner provider, but can make the writes or the flushes of files in the
    /// backup folder throw, the way a backup storage that went away mid-backup does.</summary>
    sealed class FailingBackupWritesIO(IIOProvider inner) : IIOProvider {
        public volatile bool FailAppends;
        public volatile bool FailFlushes;
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public IAppendStream OpenAppend(string[] path) {
            var stream = inner.OpenAppend(path);
            return path[0] == FileKeyUtility.BackupFolderName ? new FailingStream(stream, this) : stream;
        }
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
        public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
        public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
        public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => inner.TryMoveIfSameDrive(fromLocalFilePath, destination);
        public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
        public bool DeleteFolderIfEmpty(string[] path) => inner.DeleteFolderIfEmpty(path);
        public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
        public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);

        sealed class FailingStream(IAppendStream inner, FailingBackupWritesIO io) : IAppendStream {
            static void throwIf(bool failing) { if (failing) throw new IOException("The backup storage is unreachable. "); }
            public string FileKey => inner.FileKey;
            public long Length => inner.Length;
            public void Append(byte[] data) { throwIf(io.FailAppends); inner.Append(data); }
            public void Append(byte[] data, int count) { throwIf(io.FailAppends); inner.Append(data, count); }
            public void RecordChecksum() => inner.RecordChecksum();
            public void WriteChecksum() => inner.WriteChecksum();
            public void Flush(bool deepFlush) { throwIf(io.FailFlushes); inner.Flush(deepFlush); }
            public void Get(long position, int count, byte[] buffer) => inner.Get(position, count, buffer);
            public void ResetByteCounter() => inner.ResetByteCounter();
            public long GetBytesRead() => inner.GetBytesRead();
            public long GetBytesWritten() => inner.GetBytesWritten();
            public Task AppendAsyncNoChecksumOrLock(byte[] buffer, int count) { throwIf(io.FailAppends); return inner.AppendAsyncNoChecksumOrLock(buffer, count); }
            public void Dispose() => inner.Dispose();
        }
    }
}
