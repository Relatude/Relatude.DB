using System.Text;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public interface IMfDoc { // a file of its own, and embedded sections that carry one too
    Guid Id { get; set; }
    string Title { get; set; }
    FileValue Attachment { get; set; }
    Embedded<MfSection> Sections { get; }
}
[Node]
public interface IMfAlbum { // no file property of its own: only reachable through the embedded type
    Guid Id { get; set; }
    string Name { get; set; }
    Embedded<MfSection> Pages { get; }
}
[Node]
public interface IMfPlain { // no files anywhere, must never be scanned
    Guid Id { get; set; }
    string Name { get; set; }
}
public class MfSection {
    public Guid Id { get; set; }
    public string Caption { get; set; } = string.Empty;
    public FileValue Image { get; set; } = FileValue.Empty;
}

/// <summary>
/// DataStoreLocal.FindMissingFilesAsync: which node types are scanned (a file property of their own,
/// or one reachable through an embedded type), and that a file value whose file is gone from the
/// store is reported with the node, property path and reason.
/// </summary>
[TestClass]
public class MissingFilesTests {
    static Datamodel getDatamodel() {
        var dm = new Datamodel();
        dm.Add<IMfDoc>();
        dm.Add<IMfAlbum>();
        dm.Add<IMfPlain>();
        dm.Add<MfSection>();
        return dm;
    }
    static (NodeStore store, DataStoreLocal data, IOProviderMemory io) open() {
        var io = new IOProviderMemory();
        var data = DataStoreLocal.Open(getDatamodel(), null, io);
        return (new NodeStore(data), data, io);
    }
    static Guid typeId(DataStoreLocal data, string codeName)
        => data.Datamodel.NodeTypes.Values.Single(t => t.CodeName == codeName).Id;
    static async Task<Guid> addDocWithFile(NodeStore store, string title, int size) {
        var doc = store.Create<IMfDoc>();
        doc.Title = title;
        store.Insert(doc);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        await store.FileUploadAsync(doc, d => d.Attachment, data, title + ".bin");
        return doc.Id;
    }

    [TestMethod]
    public void TypesThatMayContainFiles_CoversDirectAndEmbeddedAndSkipsTheRest() {
        var (store, data, _) = open();
        using (store) {
            var types = data.GetNodeTypeIdsThatMayContainFiles();
            Assert.IsTrue(types.Contains(typeId(data, "IMfDoc")), "a type with a file property of its own");
            Assert.IsTrue(types.Contains(typeId(data, "MfSection")), "the embedded type holding the file");
            Assert.IsTrue(types.Contains(typeId(data, "IMfAlbum")), "a type that only reaches a file through an embedded type");
            Assert.IsFalse(types.Contains(typeId(data, "IMfPlain")), "a type with no files anywhere");
        }
    }

    [TestMethod]
    public async Task FilesPresentInTheStore_AreNotReported() {
        var (store, data, _) = open();
        using (store) {
            await addDocWithFile(store, "one", 500);
            await addDocWithFile(store, "two", 700);
            store.Insert(store.Create<IMfPlain>()); // never scanned, has no file property
            var result = await data.FindMissingFilesAsync();
            Assert.AreEqual(2, result.FilesChecked);
            Assert.AreEqual(0, result.MissingCount);
            Assert.AreEqual(0, result.Missing.Length);
            Assert.AreEqual(2, result.NodesScanned, "only the nodes of file carrying types are loaded");
        }
    }

    [TestMethod]
    public async Task FileDeletedFromTheStore_IsReportedWithItsNodeAndProperty() {
        var (store, data, io) = open();
        using (store) {
            var keptId = await addDocWithFile(store, "kept", 500);
            var lostId = await addDocWithFile(store, "lost", 1234);
            // remove the second file behind the store's back, as a lost or never replicated file would be
            var lostKey = io.GetFiles().Single(f => f.Key.Contains("lost") && f.Key.StartsWith("files/")).KeyOf();
            io.DeleteFileIfItExists(lostKey);

            var result = await data.FindMissingFilesAsync();
            Assert.AreEqual(2, result.FilesChecked);
            Assert.AreEqual(1, result.MissingCount);
            Assert.AreEqual(1234, result.MissingBytes);
            Assert.IsFalse(result.ListTruncated);
            var missing = result.Missing.Single();
            Assert.AreEqual(lostId, missing.NodeId);
            Assert.AreNotEqual(keptId, missing.NodeId);
            Assert.AreEqual("Attachment", missing.Property);
            Assert.AreEqual("lost.bin", missing.FileName);
            Assert.AreEqual(1234, missing.Size);
            Assert.IsTrue(missing.NodeType.EndsWith("MfDoc"), "reported type was " + missing.NodeType);
            Assert.IsTrue(missing.Reason.Length > 0);
        }
    }

    [TestMethod]
    public async Task FileValueInsideAnEmbeddedObject_IsCheckedAndReportedWithItsPath() {
        var (store, data, _) = open();
        using (store) {
            var album = store.Create<IMfAlbum>();
            album.Name = "album";
            // a file value pointing at a file that was never written to the store
            album.Pages.Add(new MfSection {
                Id = Guid.NewGuid(),
                Caption = "page one",
                Image = FileValue.CreateNew("ghost.png", 4242, "hash", Guid.Empty, Guid.NewGuid(),
                    Encoding.UTF8.GetBytes("ghost.0123456789abcdef01234567.png"), new PropertyPath(Guid.NewGuid(), Guid.NewGuid())),
            });
            store.Insert(album);

            var result = await data.FindMissingFilesAsync();
            Assert.AreEqual(1, result.FilesChecked, "the file value inside the embedded object must be checked");
            Assert.AreEqual(1, result.MissingCount);
            var missing = result.Missing.Single();
            Assert.AreEqual("Pages.Image", missing.Property, "embedded values are reported with their path");
            Assert.AreEqual("ghost.png", missing.FileName);
            Assert.AreEqual(album.Id, missing.NodeId, "reported against the node that owns the embedded object");
        }
    }

    [TestMethod]
    public async Task Cancelling_StopsTheScan() {
        var (store, data, _) = open();
        using (store) {
            await addDocWithFile(store, "one", 500);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => data.FindMissingFilesAsync(null, cancellation.Token));
        }
    }

    [TestMethod]
    public async Task ProgressIsReported_EndingAtCompletion() {
        var (store, data, _) = open();
        using (store) {
            await addDocWithFile(store, "one", 500);
            var reports = new List<(string description, int percent)>();
            var result = await data.FindMissingFilesAsync((description, percent) => reports.Add((description, percent)));
            Assert.AreEqual(1, result.FilesChecked);
            Assert.IsTrue(reports.Count >= 2, "at least a start and a completion");
            Assert.AreEqual(0, reports[0].percent);
            Assert.AreEqual(100, reports[^1].percent);
            Assert.AreEqual("Check completed", reports[^1].description);
        }
    }
}
