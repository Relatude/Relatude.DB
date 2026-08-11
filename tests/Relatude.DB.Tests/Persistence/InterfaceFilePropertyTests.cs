using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public interface IFileDoc {
    Guid Id { get; set; }
    string Title { get; set; }
    FileValue Attachment { get; set; }
}

[Node]
public class FileDocClass {
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public FileValue Attachment { get; set; } = FileValue.Empty;
}

// Regression tests for file properties on interface node types: a FileValue carries the path of the property it
// belongs to, and the file APIs taking a FileValue rely on it. An empty value is never stored with a path (inserts
// and FileDeleteAsync write the shared FileValue.Empty, and ToBytes drops the path of an empty value), so the
// generated mapper stamps the path when it materializes a class node. The generated interface proxy read file
// values straight out of the node data instead, so an empty file on an interface node came back with a null path
// and could not be uploaded to.
[TestClass]
public class InterfaceFilePropertyTests {

    static Datamodel getDatamodel() {
        var dm = new Datamodel();
        dm.Add<IFileDoc>();
        dm.Add<FileDocClass>();
        return dm;
    }
    static Guid attachmentId(NodeStore store) => store.Mapper.GetProperty<IFileDoc, FileValue>(n => n.Attachment).Id;

    [TestMethod]
    public void EmptyFile_HasPropertyPathAfterInsert() {
        using var store = new NodeStore(DataStoreLocal.Open(getDatamodel(), null, new IOProviderMemory()));

        var doc = store.Create<IFileDoc>();
        doc.Title = "T1";
        store.Insert(doc);

        var loaded = store.Get<IFileDoc>(doc.Id);
        Assert.IsTrue(loaded.Attachment.IsEmpty);
        Assert.IsNotNull(loaded.Attachment.PropertyPath, "An empty file value must still know which property it belongs to. ");
        Assert.AreEqual(doc.Id, loaded.Attachment.PropertyPath!.NodePath.NodeKey.Guid);
        Assert.AreEqual(attachmentId(store), loaded.Attachment.PropertyPath.PropertyId);
    }

    [TestMethod]
    public void EmptyFile_HasPropertyPathAfterLogReplay() {
        var io = new IOProviderMemory();
        var datamodel = getDatamodel();
        var store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));

        var doc = store.Create<IFileDoc>();
        doc.Title = "T1";
        store.Insert(doc);
        var id = doc.Id;
        store.Dispose();

        // reopen from the transaction log, forcing the value through serialization both ways:
        store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));
        var loaded = store.Get<IFileDoc>(id);
        Assert.IsNotNull(loaded.Attachment.PropertyPath, "The path must be restored after replay, where the empty value carries no data. ");
        Assert.AreEqual(id, loaded.Attachment.PropertyPath!.NodePath.NodeKey.Guid);
        store.Dispose();
    }

    [TestMethod]
    public void EmptyFile_CanBeUploadedToByValue() {
        using var store = new NodeStore(DataStoreLocal.Open(getDatamodel(), null, new IOProviderMemory()));

        var doc = store.Create<IFileDoc>();
        doc.Title = "T1";
        store.Insert(doc);

        var localFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllText(localFile, "hello");
        try {
            // the FileValue overload addresses the property through the path on the value itself:
            var uploaded = store.FileUploadAsync(store.Get<IFileDoc>(doc.Id).Attachment, localFile).GetAwaiter().GetResult();
            Assert.IsFalse(uploaded.IsEmpty);
            var loaded = store.Get<IFileDoc>(doc.Id);
            Assert.IsFalse(loaded.Attachment.IsEmpty);
            Assert.AreEqual(5, loaded.Attachment.Size);
            Assert.IsNotNull(loaded.Attachment.PropertyPath);
        } finally {
            File.Delete(localFile);
        }
    }

    [TestMethod]
    public void EmptyFile_HasPropertyPathAfterFileDelete() {
        using var store = new NodeStore(DataStoreLocal.Open(getDatamodel(), null, new IOProviderMemory()));

        var doc = store.Create<IFileDoc>();
        doc.Title = "T1";
        store.Insert(doc);
        var propertyId = attachmentId(store);
        store.FileUploadAsync(doc.Id, propertyId, new MemoryStream([1, 2, 3]), "x.txt").GetAwaiter().GetResult();
        Assert.IsFalse(store.Get<IFileDoc>(doc.Id).Attachment.IsEmpty);

        // deleting writes the shared FileValue.Empty, which has no path:
        store.FileDeleteAsync(doc.Id, propertyId).GetAwaiter().GetResult();

        var loaded = store.Get<IFileDoc>(doc.Id);
        Assert.IsTrue(loaded.Attachment.IsEmpty);
        Assert.IsNotNull(loaded.Attachment.PropertyPath, "The path must survive a file delete, so a new file can be uploaded. ");
    }

    [TestMethod]
    public void UploadedFile_KeepsPropertyPathAfterLogReplay() {
        var io = new IOProviderMemory();
        var datamodel = getDatamodel();
        var store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));

        var doc = store.Create<IFileDoc>();
        doc.Title = "T1";
        store.Insert(doc);
        var id = doc.Id;
        store.FileUploadAsync(id, attachmentId(store), new MemoryStream([1, 2, 3]), "x.txt").GetAwaiter().GetResult();
        store.Dispose();

        store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));
        var loaded = store.Get<IFileDoc>(id);
        Assert.IsFalse(loaded.Attachment.IsEmpty);
        Assert.AreEqual(3, loaded.Attachment.Size);
        Assert.IsNotNull(loaded.Attachment.PropertyPath);
        Assert.AreEqual(id, loaded.Attachment.PropertyPath!.NodePath.NodeKey.Guid);
        store.Dispose();
    }

    [TestMethod]
    public void MutableMembersSurviveUpdate() {
        using var store = new NodeStore(DataStoreLocal.Open(getDatamodel(), null, new IOProviderMemory()));

        var doc = store.Create<IFileDoc>();
        doc.Title = "T1";
        store.Insert(doc);
        store.FileUploadAsync(doc.Id, attachmentId(store), new MemoryStream([1, 2, 3]), "x.txt").GetAwaiter().GetResult();

        // the proxy hands out the same value on every read, so edits to it are kept until the node is written back:
        var loaded = store.Get<IFileDoc>(doc.Id);
        loaded.Attachment.Name = "renamed.txt";
        Assert.AreEqual("renamed.txt", loaded.Attachment.Name, "Repeated reads must return the same value instance. ");
        store.Update(loaded);

        Assert.AreEqual("renamed.txt", store.Get<IFileDoc>(doc.Id).Attachment.Name);
    }

    [TestMethod]
    public void InterfaceAndClassBehaveTheSame() {
        var io = new IOProviderMemory();
        var datamodel = getDatamodel();
        var store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));

        var iDoc = store.Create<IFileDoc>();
        iDoc.Title = "I";
        store.Insert(iDoc);
        var classDoc = new FileDocClass { Title = "C" };
        store.Insert(classDoc);
        var iId = iDoc.Id;
        var cId = classDoc.Id;
        store.Dispose();

        store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));
        var iLoaded = store.Get<IFileDoc>(iId);
        var cLoaded = store.Get<FileDocClass>(cId);
        Assert.AreEqual(cLoaded.Attachment.IsEmpty, iLoaded.Attachment.IsEmpty);
        Assert.AreEqual(cLoaded.Attachment.PropertyPath != null, iLoaded.Attachment.PropertyPath != null);
        store.Dispose();
    }
}
