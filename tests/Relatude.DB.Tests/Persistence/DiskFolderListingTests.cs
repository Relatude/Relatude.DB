using Relatude.DB.IO;

namespace Relatude.Persistence;

/// <summary>
/// The disk provider's folder listing. A walk of the whole tree no longer asks every folder
/// beforehand whether it holds files and folders (it lists them anyway, and on a network share each
/// question is a trip), so what those two flags say has to come out the same either way; and the
/// open stream counts, now copied at the start rather than read under the provider lock throughout,
/// still have to be on the files.
/// </summary>
[TestClass]
public class DiskFolderListingTests {

    [TestMethod]
    public async Task ATreeWalkReportsTheSameFlagsAsTheFolderByFolderListing() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-folder-listing-" + Guid.NewGuid().ToString("N"));
        try {
            // top: a file and three folders - one empty, one with only a folder in it, one with only files
            Directory.CreateDirectory(Path.Combine(root, "top", "empty"));
            Directory.CreateDirectory(Path.Combine(root, "top", "nested", "leaf"));
            Directory.CreateDirectory(Path.Combine(root, "top", "files"));
            File.WriteAllText(Path.Combine(root, "top", "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "top", "nested", "leaf", "b.txt"), "b");
            File.WriteAllText(Path.Combine(root, "top", "files", "c.txt"), "c");
            var io = new IOProviderDisk(root);

            foreach (var withFiles in new[] { true, false }) {
                var tree = await io.GetFolderAsync(["top"], recursive: true, withFiles: withFiles);
                foreach (var path in new[] { "top/empty", "top/nested", "top/nested/leaf", "top/files" }) {
                    var walked = find(tree, path.Split('/')[1..]);
                    var listed = await io.GetFolderAsync(path.Split('/')[..^1], recursive: false, withFiles: false);
                    var stub = listed.SubFolders.Single(f => f.Name == path.Split('/')[^1]);
                    Assert.AreEqual(stub.HasFiles, walked.HasFiles, $"{path}: HasFiles (withFiles {withFiles})");
                    Assert.AreEqual(stub.HasSubFolders, walked.HasSubFolders, $"{path}: HasSubFolders (withFiles {withFiles})");
                }
                Assert.AreEqual(withFiles ? 1 : 0, find(tree, ["nested", "leaf"]).Files.Length, "the files come with the walk only when asked for");
            }
            Assert.IsTrue(find(await io.GetFolderAsync(["top"], true, true), ["nested"]).HasSubFolders);
            Assert.IsFalse(find(await io.GetFolderAsync(["top"], true, true), ["nested"]).HasFiles);
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task TheListingCarriesTheOpenStreamCountsOfItsFiles() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-folder-listing-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.WriteAllText(Path.Combine(root, "logs", "read.bin"), "read me");
            var io = new IOProviderDisk(root);
            using (io.OpenRead(["logs", "read.bin"], 0))
            using (io.OpenAppend(["logs", "written.bin"])) {
                var listing = await io.GetFolderAsync([], recursive: true, withFiles: true);
                var files = find(listing, ["logs"]).Files;
                Assert.AreEqual(1, files.Single(f => f.Key == "logs/read.bin").Readers);
                Assert.AreEqual(1, files.Single(f => f.Key == "logs/written.bin").Writers);
            }
            var after = find(await io.GetFolderAsync([], true, true), ["logs"]).Files;
            Assert.IsTrue(after.All(f => f.Readers == 0 && f.Writers == 0), "closed streams are no longer counted");
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static FolderMeta find(FolderMeta folder, string[] path) {
        foreach (var name in path) folder = folder.SubFolders.Single(f => f.Name == name);
        return folder;
    }
}
