using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public interface IAuditDoc {
    Guid Id { get; set; }
    string Title { get; set; }
    FileValue Attachment { get; set; }
    [FileProperty(FileStorageProviderId = "aaaaaaaa-2222-0000-0000-000000000002")]
    FileValue Archive { get; set; }
    Embedded<AuditSection> Sections { get; }
}
public class AuditSection {
    public Guid Id { get; set; }
    public string Caption { get; set; } = string.Empty;
    public FileValue Image { get; set; } = FileValue.Empty;
}

/// <summary>
/// The "Missing and redundant files" audit of the Storage page, end to end on disk: the two scans the
/// page runs (FindMissingFilesAsync, then DeleteUnreferencedFilesAsync counting and deleting) against
/// files that are present, gone, cut short, empty, replaced, only referenced from an embedded object,
/// stray, too young to judge, and kept by two stores sharing one folder. Files are backdated past the
/// 15 minute grace period the redundant scan gives young files.
/// </summary>
[TestClass]
public class FileAuditEndToEndTests {
    static readonly Guid _mainStoreId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    static readonly Guid _archiveStoreId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000002");

    sealed class Site : IDisposable {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "relatude-file-audit-" + Guid.NewGuid().ToString("N"));
        public NodeStore Store = null!;
        public DataStoreLocal Data = null!;
        public Guid Attachment, Archive;
        public string FilesFolder => Path.Combine(Dir, "files", FileKeyUtility.MultiFileStoreFolderKey);
        /// <param name="archiveInSameFolder">the archive store on the same provider as the main one, so both keep their files in one folder</param>
        public Site(bool archiveInSameFolder) {
            var dm = new Datamodel();
            dm.Add<IAuditDoc>();
            dm.Add<AuditSection>();
            var files = new IOProviderDisk(Path.Combine(Dir, "files"));
            var archive = archiveInSameFolder ? files : new IOProviderDisk(Path.Combine(Dir, "archive"));
            Data = DataStoreLocal.Open(dm, new SettingsLocal { DefaultFileStore = _mainStoreId }, new IOProviderMemory(),
                [new MultiFileStore(_mainStoreId, files, 2), new MultiFileStore(_archiveStoreId, archive, 2)]);
            Store = new NodeStore(Data);
            var type = Data.Datamodel.NodeTypes.Values.Single(t => t.CodeName == nameof(IAuditDoc));
            Attachment = type.AllProperties.Values.Single(p => p.CodeName == nameof(IAuditDoc.Attachment)).Id;
            Archive = type.AllProperties.Values.Single(p => p.CodeName == nameof(IAuditDoc.Archive)).Id;
        }
        public Guid AddDoc(string title) {
            var doc = Store.Create<IAuditDoc>();
            doc.Title = title;
            Store.Insert(doc);
            return doc.Id;
        }
        public Task<FileValue> Upload(Guid nodeId, Guid propertyId, string name, int size) {
            var data = new byte[size];
            new Random(size + name.Length).NextBytes(data);
            return Store.FileUploadAsync(nodeId, propertyId, new MemoryStream(data), name);
        }
        /// <summary>The file on disk that a stored file name begins with (the store adds the file id).</summary>
        public string? PathOf(string fileName) => Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)
            .SingleOrDefault(f => Path.GetFileName(f).StartsWith(Path.GetFileNameWithoutExtension(fileName) + ".", StringComparison.OrdinalIgnoreCase));
        /// <summary>Moves every file written so far past the grace period of the redundant file scan.</summary>
        public void Backdate() {
            foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetCreationTimeUtc(f, DateTime.UtcNow.AddHours(-1));
        }
        public void WriteStray(string relativeFolder, string name, int size) {
            var folder = Path.Combine(FilesFolder, relativeFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), new byte[size]);
        }
        public void Dispose() {
            Store.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestMethod]
    public async Task Missing_ReportsGoneCutShortAndEmptyFiles_AndNotThePresentOne() {
        using var site = new Site(archiveInSameFolder: false);
        await site.Upload(site.AddDoc("present"), site.Attachment, "present.bin", 500);
        await site.Upload(site.AddDoc("gone"), site.Attachment, "gone.bin", 300);
        await site.Upload(site.AddDoc("cut"), site.Attachment, "cut.bin", 400);
        await site.Upload(site.AddDoc("empty"), site.Attachment, "empty.bin", 0);
        File.Delete(site.PathOf("gone.bin")!);
        using (var cut = new FileStream(site.PathOf("cut.bin")!, FileMode.Open)) cut.SetLength(390);
        File.Delete(site.PathOf("empty.bin")!);

        var result = await site.Data.FindMissingFilesAsync();
        Assert.AreEqual(4, result.FilesChecked);
        CollectionAssert.AreEquivalent(new[] { "gone.bin", "cut.bin", "empty.bin" }, result.Missing.Select(m => m.FileName).ToArray());
        Assert.AreEqual(3, result.MissingCount);
        Assert.AreEqual(300 + 400 + 0, result.MissingBytes);
    }

    [TestMethod]
    public async Task Redundant_CountsAndDeletesOnlyFilesNothingPointsAt() {
        using var site = new Site(archiveInSameFolder: false);
        await site.Upload(site.AddDoc("kept"), site.Attachment, "kept.bin", 500);
        var replaced = site.AddDoc("replaced");
        await site.Upload(replaced, site.Attachment, "old.bin", 600);
        await site.Upload(replaced, site.Attachment, "new.bin", 700); // the old file is left behind
        // a file whose only reference is inside an embedded object of another node
        var moved = site.AddDoc("moved");
        var embeddedValue = await site.Upload(moved, site.Attachment, "embedded.bin", 800);
        var holder = site.Store.Create<IAuditDoc>();
        holder.Title = "holder";
        holder.Sections.Add(new AuditSection { Id = Guid.NewGuid(), Caption = "section", Image = embeddedValue });
        site.Store.Insert(holder);
        await site.Upload(moved, site.Attachment, "moved-new.bin", 100);
        // removing a file deletes it at once, so it is never redundant
        var removed = site.AddDoc("removed");
        await site.Upload(removed, site.Attachment, "removed.bin", 900);
        await site.Store.FileDeleteAsync(removed, site.Attachment);
        Assert.IsNull(site.PathOf("removed.bin"));
        site.WriteStray(Path.Combine("aa", "bb"), "stray.bin", 50);
        site.Backdate();
        site.WriteStray(Path.Combine("cc", "dd"), "recent.bin", 70); // too young to judge

        var counted = await site.Data.DeleteUnreferencedFilesAsync(countOnly: true);
        Assert.AreEqual(2, counted.TotalFilesDeleted, "old.bin and stray.bin");
        Assert.AreEqual(600 + 50, counted.TotalBytesDeleted);
        Assert.IsNotNull(site.PathOf("old.bin"), "counting deletes nothing");

        var deleted = await site.Data.DeleteUnreferencedFilesAsync(countOnly: false);
        Assert.AreEqual(counted.TotalFilesDeleted, deleted.TotalFilesDeleted);
        Assert.AreEqual(counted.TotalBytesDeleted, deleted.TotalBytesDeleted);
        Assert.IsNull(site.PathOf("old.bin"));
        Assert.IsNull(site.PathOf("stray.bin"));
        Assert.IsFalse(Directory.Exists(Path.Combine(site.FilesFolder, "aa")), "the stray's emptied folders go too");
        foreach (var name in new[] { "kept.bin", "new.bin", "embedded.bin", "moved-new.bin" }) Assert.IsNotNull(site.PathOf(name), name + " is referenced and must stay");
        Assert.IsNotNull(site.PathOf("recent.bin"), "files younger than the grace period are left alone");

        var missing = await site.Data.FindMissingFilesAsync();
        Assert.AreEqual(0, missing.MissingCount, "the clean-up must never take a referenced file");
        Assert.AreEqual(0, (await site.Data.DeleteUnreferencedFilesAsync(countOnly: true)).TotalFilesDeleted, "nothing left to clean");
    }

    [TestMethod]
    public async Task Redundant_TwoStoresSharingOneFolder_KeepEachOthersFiles_AndCountAStrayOnce() {
        using var site = new Site(archiveInSameFolder: true);
        var doc = site.AddDoc("doc");
        await site.Upload(doc, site.Attachment, "main.bin", 500);
        await site.Upload(doc, site.Archive, "archive.bin", 600);
        site.WriteStray(Path.Combine("aa", "bb"), "stray.bin", 50);
        site.Backdate();

        var counted = await site.Data.DeleteUnreferencedFilesAsync(countOnly: true);
        Assert.AreEqual(1, counted.TotalFilesDeleted, "only the stray: each store's files are referenced, and the shared folder is walked once");
        Assert.AreEqual(50, counted.TotalBytesDeleted);
        var deleted = await site.Data.DeleteUnreferencedFilesAsync(countOnly: false);
        Assert.AreEqual(1, deleted.TotalFilesDeleted);
        Assert.IsNotNull(site.PathOf("main.bin"));
        Assert.IsNotNull(site.PathOf("archive.bin"));
        Assert.AreEqual(0, (await site.Data.FindMissingFilesAsync()).MissingCount);
    }
}
