using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public interface IDupDoc {
    Guid Id { get; set; }
    string Title { get; set; }
    FileValue Attachment { get; set; }
}

/// <summary>
/// DataStoreLocal.FindDuplicateFilesAsync: stored files (one per file id and store) grouped by the
/// hash and size their values carry, the duplicate copies and bytes, the groups listed by wasted
/// bytes, and values that already share a stored file.
/// </summary>
[TestClass]
public class DuplicateFilesTests {
    static readonly Guid _storeId = Guid.Parse("dddddddd-1111-0000-0000-000000000001");
    static Datamodel datamodel() {
        var dm = new Datamodel();
        dm.Add<IDupDoc>();
        return dm;
    }
    static NodeStore open(IIOProvider dbIo, IIOProvider filesIo, bool sameHashSameFile)
        => new(DataStoreLocal.Open(datamodel(), new SettingsLocal { DefaultFileStore = _storeId }, dbIo, [new MultiFileStore(_storeId, filesIo, 2, sameHashSameFile)]));
    static byte[] bytes(int size, int seed) {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
    static async Task upload(NodeStore store, string title, byte[]? data, string fileName = "file.bin") {
        var doc = store.Create<IDupDoc>();
        doc.Title = title;
        store.Insert(doc);
        if (data != null) await store.FileUploadAsync(doc, d => d.Attachment, data, fileName);
    }
    static DuplicateFilesResult find(NodeStore store) => ((DataStoreLocal)store.Datastore).FindDuplicateFilesAsync().GetAwaiter().GetResult();

    [TestMethod]
    public async Task Duplicates_AreCountedPerContent_AndListedByWastedBytes() {
        using var store = open(new IOProviderMemory(), new IOProviderMemory(), sameHashSameFile: false);
        var a = bytes(1000, 1);
        var b = bytes(500, 2);
        await upload(store, "a1", a, "report.pdf");
        await upload(store, "a2", a, "report (1).pdf");
        await upload(store, "a3", a, "copy.pdf");
        await upload(store, "b1", b);
        await upload(store, "b2", b);
        await upload(store, "c", bytes(200, 3));
        await upload(store, "no file", null);

        var result = find(store);
        Assert.AreEqual(6, result.ValuesChecked);
        Assert.AreEqual(6, result.StoredFiles);
        Assert.AreEqual(3 * 1000 + 2 * 500 + 200, result.StoredBytes);
        Assert.AreEqual(3, result.DistinctContents);
        Assert.AreEqual(3, result.DuplicateFiles, "two extra copies of a, one of b");
        Assert.AreEqual(2 * 1000 + 500, result.DuplicateBytes);
        Assert.AreEqual(0, result.SharedValues);
        Assert.AreEqual(0, result.ValuesWithoutHash);
        Assert.AreEqual(2, result.Groups.Length, "content stored once is not a duplicate");
        Assert.IsFalse(result.ListTruncated);
        var first = result.Groups[0];
        Assert.AreEqual(1000, first.Size);
        Assert.AreEqual(3, first.Copies);
        Assert.AreEqual(3, first.Values);
        Assert.AreEqual(2000, first.DuplicateBytes);
        Assert.AreEqual(_storeId, first.StorageId);
        Assert.AreEqual(3, first.Examples.Length);
        CollectionAssert.Contains(first.Examples, "IDupDoc.Attachment - report.pdf");
        Assert.AreEqual(500, result.Groups[1].DuplicateBytes);
    }

    [TestMethod]
    public async Task WithSameHashSameFile_TheValuesShareOneCopy_AndNothingIsDuplicated() {
        using var store = open(new IOProviderMemory(), new IOProviderMemory(), sameHashSameFile: true);
        var a = bytes(1000, 1);
        await upload(store, "a1", a);
        await upload(store, "a2", a);
        await upload(store, "a3", a);
        var result = find(store);
        Assert.AreEqual(3, result.ValuesChecked);
        Assert.AreEqual(1, result.StoredFiles);
        Assert.AreEqual(0, result.DuplicateFiles);
        Assert.AreEqual(0, result.Groups.Length);
        Assert.AreEqual(2, result.SharedValues);
        Assert.AreEqual(2000, result.SharedBytes, "what storing them separately would have taken");
    }

    [TestMethod]
    public async Task TurnedOnAfterFilesWereUploaded_OneExtraCopyPerContentAndNoMore() {
        IIOProvider dbIo = new IOProviderMemory(), filesIo = new IOProviderMemory();
        var a = bytes(1000, 1);
        using (var before = open(dbIo, filesIo, sameHashSameFile: false)) {
            await upload(before, "old1", a);
            await upload(before, "old2", a);
        }
        using var after = open(dbIo, filesIo, sameHashSameFile: true);
        await upload(after, "new1", a);
        await upload(after, "new2", a);
        await upload(after, "new3", a);
        var result = find(after);
        Assert.AreEqual(5, result.ValuesChecked);
        Assert.AreEqual(3, result.StoredFiles, "the two old copies, and one kept by its hash for all new uploads");
        Assert.AreEqual(1, result.DistinctContents);
        Assert.AreEqual(2, result.DuplicateFiles);
        Assert.AreEqual(2000, result.DuplicateBytes);
        Assert.AreEqual(2, result.SharedValues, "the three new values share the copy kept by its hash");
        Assert.AreEqual(1, result.Groups.Length);
        Assert.AreEqual(3, result.Groups[0].Copies);
        Assert.AreEqual(5, result.Groups[0].Values);
    }
}
