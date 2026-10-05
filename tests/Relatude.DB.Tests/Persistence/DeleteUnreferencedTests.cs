using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;

namespace Relatude.Persistence;

/// <summary>
/// MultiFileStore.DeleteUnreferenced: everything under the store folder whose '/'-joined file key
/// (the internal reference) is not in the valid set is deleted, folders left empty are removed,
/// and countOnly reports the same totals without touching anything.
/// </summary>
[TestClass]
public class DeleteUnreferencedTests {
    static readonly Guid _propertyId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    static async Task<FileValue> insert(MultiFileStore store, Guid fileId, string fileName, int size) {
        using var ms = new MemoryStream(new byte[size]);
        var r = await store.InsertAsync(fileId, ms, fileName);
        return FileValue.CreateNew(fileName, r.Length, r.FileHash, store.Id, fileId, r.StoreKey, new PropertyPath(Guid.NewGuid(), _propertyId));
    }

    [TestMethod]
    public async Task DeleteUnreferenced_DeletesOnlyUnreferencedFilesAndEmptyFolders() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2);
        // fixed file ids so the folder chains (first 2x2 hex chars) are known and distinct
        var kept1 = await insert(store, Guid.Parse("11111111-0000-0000-0000-000000000000"), "kept1.txt", 100);
        var kept2 = await insert(store, Guid.Parse("22222222-0000-0000-0000-000000000000"), "kept2.txt", 200);
        var lost1 = await insert(store, Guid.Parse("33333333-0000-0000-0000-000000000000"), "lost1.txt", 300);
        var lost2 = await insert(store, Guid.Parse("44444444-0000-0000-0000-000000000000"), "lost2.txt", 400);
        // a lost file sharing its first level folder ("11") with kept1, one sharing kept1's full
        // folder ("11/11"), and a stray file no insert created
        var lost3 = await insert(store, Guid.Parse("11155555-0000-0000-0000-000000000000"), "lost3.txt", 500);
        var lost4 = await insert(store, Guid.Parse("11116666-0000-0000-0000-000000000000"), "lost4.txt", 600);
        io.WriteAllBytes(["files", "aa", "bb", "stray.txt"], new byte[7]);

        var valid = new HashSet<string> {
            await store.GetInternalReference(kept1),
            await store.GetInternalReference(kept2),
        };
        var totalBefore = io.GetFiles().Length;

        // lost1, lost2 and the stray each free their 2 folders; lost3 frees only "11/15" as "11"
        // still holds kept1's subfolder, and lost4 frees nothing as kept1 stays in "11/11"
        var counted = await store.DeleteUnreferenced(valid, countOnly: true);
        Assert.AreEqual(5, counted.TotalFilesDeleted);
        Assert.AreEqual(300 + 400 + 500 + 600 + 7, counted.TotalBytesDeleted);
        Assert.AreEqual(7, counted.TotalFoldersDeleted);
        Assert.AreEqual(totalBefore, io.GetFiles().Length, "countOnly must not delete anything");

        var deleted = await store.DeleteUnreferenced(valid);
        Assert.AreEqual(counted.TotalFilesDeleted, deleted.TotalFilesDeleted);
        Assert.AreEqual(counted.TotalBytesDeleted, deleted.TotalBytesDeleted);
        Assert.AreEqual(counted.TotalFoldersDeleted, deleted.TotalFoldersDeleted);

        Assert.IsTrue(await store.ContainsFileAsync(kept1));
        Assert.IsTrue(await store.ContainsFileAsync(kept2));
        Assert.IsFalse(await store.ContainsFileAsync(lost1));
        Assert.IsFalse(await store.ContainsFileAsync(lost2));
        Assert.IsFalse(await store.ContainsFileAsync(lost3));
        Assert.IsFalse(await store.ContainsFileAsync(lost4));
        Assert.AreEqual(totalBefore - 5, io.GetFiles().Length);

        // nothing left to delete on a second run
        var again = await store.DeleteUnreferenced(valid);
        Assert.AreEqual(0, again.TotalFilesDeleted);
        Assert.AreEqual(0, again.TotalBytesDeleted);
        Assert.AreEqual(0, again.TotalFoldersDeleted);
    }

    [TestMethod]
    public async Task DeleteUnreferenced_OnDisk_RemovesEmptyFoldersAndKeepsTheRest() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-delete-unreferenced-" + Guid.NewGuid().ToString("N"));
        try {
            var io = new IOProviderDisk(dir);
            using var store = new MultiFileStore(Guid.NewGuid(), io, 2);
            var kept = await insert(store, Guid.Parse("11111111-0000-0000-0000-000000000000"), "kept.txt", 100);
            var lost = await insert(store, Guid.Parse("33333333-0000-0000-0000-000000000000"), "lost.txt", 300);
            var valid = new HashSet<string> { await store.GetInternalReference(kept) };
            var result = await store.DeleteUnreferenced(valid);
            Assert.AreEqual(1, result.TotalFilesDeleted);
            Assert.AreEqual(300, result.TotalBytesDeleted);
            Assert.AreEqual(2, result.TotalFoldersDeleted);
            Assert.IsTrue(await store.ContainsFileAsync(kept));
            Assert.IsFalse(await store.ContainsFileAsync(lost));
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "files", "33")), "emptied folders must be removed from disk");
            Assert.IsTrue(Directory.Exists(Path.Combine(dir, "files", "11", "11")), "folders holding referenced files must remain");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task DeleteUnreferenced_KeepsRecentFilesAndReportsProgress() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2);
        await insert(store, Guid.Parse("11111111-0000-0000-0000-000000000000"), "a.txt", 10);
        await insert(store, Guid.Parse("22222222-0000-0000-0000-000000000000"), "b.txt", 20);
        var progress = new List<(long processed, long total)>();
        // everything is unreferenced, but both files were just created so the cutoff keeps them
        var result = await store.DeleteUnreferenced(new HashSet<string>(),
            keepFilesNewerThanUtc: DateTime.UtcNow.AddMinutes(-5),
            onProgress: (processed, total) => progress.Add((processed, total)));
        Assert.AreEqual(0, result.TotalFilesDeleted);
        Assert.AreEqual(0, result.TotalFoldersDeleted);
        Assert.AreEqual(2, io.GetFiles().Length);
        Assert.AreEqual(2, progress.Count);
        Assert.AreEqual((2L, 2L), progress[^1]);
        // with the cutoff in the future no file counts as recent, so both go
        var deleted = await store.DeleteUnreferenced(new HashSet<string>(), keepFilesNewerThanUtc: DateTime.UtcNow.AddMinutes(5));
        Assert.AreEqual(2, deleted.TotalFilesDeleted);
    }

    [TestMethod]
    public async Task DeleteUnreferenced_ComparesReferencesCaseInsensitively() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2);
        var kept = await insert(store, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"), "kept.txt", 10);
        var valid = new HashSet<string> { (await store.GetInternalReference(kept)).ToUpperInvariant() };
        var result = await store.DeleteUnreferenced(valid);
        Assert.AreEqual(0, result.TotalFilesDeleted);
        Assert.IsTrue(await store.ContainsFileAsync(kept));
    }

    // An upload writes into the folder its file id names. One landing in a folder the sweep is emptying,
    // after the sweep listed the store, must survive: the folder used to be removed recursively.
    static async Task<int> sweepWithAFileArrivingAfterTheListing(IIOProvider inner) {
        string[] late = [FileKeyUtility.MultiFileStoreFolderKey, "33", "33", "late.bin"];
        var io = new AfterListingIO(inner, () => inner.WriteAllBytes(late, new byte[50]));
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2);
        var lost = await insert(store, Guid.Parse("33333333-0000-0000-0000-000000000000"), "lost.txt", 300);
        var result = await store.DeleteUnreferenced(new HashSet<string>());
        Assert.AreEqual(1, result.TotalFilesDeleted);
        Assert.IsFalse(await store.ContainsFileAsync(lost));
        Assert.AreEqual(50, inner.GetFileSizeOrZeroIfUnknown(late), "the file written after the listing must not be deleted with its folder");
        return result.TotalFoldersDeleted;
    }

    [TestMethod]
    public async Task DeleteUnreferenced_KeepsAFileWrittenIntoAnEmptiedFolderAfterTheListing_OnDisk() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-delete-unreferenced-" + Guid.NewGuid().ToString("N"));
        try {
            var foldersDeleted = await sweepWithAFileArrivingAfterTheListing(new IOProviderDisk(dir));
            Assert.AreEqual(0, foldersDeleted, "neither 33/33 nor 33 is empty any more");
            Assert.IsTrue(Directory.Exists(Path.Combine(dir, "files", "33", "33")));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task DeleteUnreferenced_KeepsAFileWrittenIntoAnEmptiedFolderAfterTheListing_InMemory() {
        // virtual folders are never deleted as such, so the count only says what the listing showed
        await sweepWithAFileArrivingAfterTheListing(new IOProviderMemory());
    }

    [TestMethod]
    public void DeleteFolderIfEmpty_OnlyEverDeletesAnEmptyFolder() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-delete-folder-if-empty-" + Guid.NewGuid().ToString("N"));
        try {
            foreach (var io in new IIOProvider[] { new IOProviderDisk(dir), new IOProviderMemory() }) {
                var name = io.GetType().Name;
                io.WriteAllBytes(["a", "b", "file.bin"], new byte[10]);
                Assert.IsFalse(io.DeleteFolderIfEmpty(["a", "b"]), name + ": a folder with a file");
                Assert.IsFalse(io.DeleteFolderIfEmpty(["a"]), name + ": a folder with a folder");
                Assert.IsTrue(io.Exists(["a", "b", "file.bin"]), name);
                Assert.IsTrue(io.DeleteFolderIfEmpty(["nothing", "here"]), name + ": a folder that does not exist is gone");
                Assert.IsFalse(io.DeleteFolderIfEmpty([]), name + ": the storage root is never deleted");
                io.DeleteFileIfItExists(["a", "b", "file.bin"]);
                Assert.IsTrue(io.DeleteFolderIfEmpty(["a", "b"]), name);
                Assert.IsTrue(io.DeleteFolderIfEmpty(["a"]), name);
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "a")), "the emptied folders are removed from disk");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Runs an action once, right after the first folder listing - where a sweep has decided
    /// what is empty but not yet acted on it.</summary>
    sealed class AfterListingIO(IIOProvider inner, Action afterListing) : IIOProvider {
        bool _done;
        public async Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) {
            var folder = await inner.GetFolderAsync(path, recursive, withFiles);
            if (!_done) { _done = true; afterListing(); }
            return folder;
        }
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public IAppendStream OpenAppend(string[] path) => inner.OpenAppend(path);
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
    }
}
