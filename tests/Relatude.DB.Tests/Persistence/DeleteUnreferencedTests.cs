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
}
