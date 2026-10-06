using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Tasks;

namespace Relatude.Persistence;

[Node]
public class FrArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public FileValue File { get; set; } = FileValue.Empty;
    [EmbeddedMapProperty(KeyProperty = nameof(FrParagraph.Code))]
    public EmbeddedMap<string, FrParagraph> Paragraphs { get; set; } = [];
}
public class FrParagraph {
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public FileValue File { get; set; } = FileValue.Empty;
}

/// <summary>
/// DataStoreLocal.RewriteFilesAsync: every file value pointing into one store - revisions and embedded
/// objects included - is given a copy written by another store (or the same one, with what it does now),
/// shared files are copied once, the old copies are left, a file that cannot be read is reported and left
/// as it was, and nothing is queued for indexing. Also that the implicit store stays readable once a
/// configured store has become the default.
/// </summary>
[TestClass]
public class RewriteFilesTests {
    static readonly Guid _a = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    static readonly Guid _b = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    static Datamodel datamodel() {
        var dm = new Datamodel();
        dm.Add<FrArticle>();
        dm.Add<FrParagraph>();
        return dm;
    }
    static NodeStore open(IIOProvider dbIo, IFileStore[]? stores, Guid? defaultStore, bool textIndex = false)
        => new(DataStoreLocal.Open(datamodel(), new SettingsLocal { DefaultFileStore = defaultStore, EnableTextIndexByDefault = textIndex, AutoDequeTasks = false }, dbIo, stores));
    static DataStoreLocal local(NodeStore store) => (DataStoreLocal)store.Datastore;
    static byte[] bytes(int size, int seed) {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
    static Guid property(NodeStore store, Type type, string name) => store.Datastore.Datamodel.NodeTypesByFullName[type.FullName!].AllPropertiesByName[name].Id;
    static PropertyPath filePath(NodeStore store, Guid articleId) => new(articleId, property(store, typeof(FrArticle), "File"));
    static PropertyPath paragraphPath(NodeStore store, Guid articleId, Guid paragraphId)
        => new PropertyPath(articleId, property(store, typeof(FrArticle), "Paragraphs")).CreatePathToInnerNode(paragraphId).CreatePropertyPath(property(store, typeof(FrParagraph), "File"));
    static Task upload(NodeStore store, PropertyPath path, byte[] data, string name) => store.Datastore.FileUploadAsync(path, new MemoryStream(data), name);
    static async Task<byte[]> read(NodeStore store, FileValue value) {
        using var stream = await local(store).GetFileStream(value);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
    // every file value of a node as it is stored: all revisions, embedded objects included
    static List<FileValue> stored(NodeStore store, Guid nodeId) {
        var data = local(store);
        Assert.IsTrue(data._nodes.TryGet(data._guids.GetId(nodeId), out var node, out _));
        var found = new List<FileValue>();
        void collect(INodeData n) {
            if (n is NodeDataRevisions revisions) {
                foreach (var revision in revisions.Revisions) collect(revision);
                return;
            }
            foreach (var entry in n.Values) {
                if (entry.Value is FileValue value && !value.IsEmpty) found.Add(value);
                else if (entry.Value is IInnerNodeDataMap inner) foreach (var innerNode in inner) collect(innerNode);
            }
        }
        collect(node);
        return found;
    }
    static int filesIn(IOProviderMemory io) => io.GetFiles().Count(f => f.Key.StartsWith("files/"));
    static int pendingTasks(NodeStore store) => local(store).TaskQueue.CountTasks(BatchState.Pending) + local(store).TaskQueuePersisted.CountTasks(BatchState.Pending);

    [TestMethod]
    public async Task EveryValueMoves_RevisionsAndEmbeddedIncluded_SharedFilesCopiedOnce() {
        var dbIo = new IOProviderMemory();
        var ioA = new IOProviderMemory();
        var ioB = new IOProviderMemory();
        using var store = open(dbIo, [new MultiFileStore(_a, ioA, 2), new MultiFileStore(_b, ioB, 2, true, FileHashAlgorithm.SHA256)], _a);
        var x = bytes(3000, 1);
        var y = bytes(1700, 2);
        var first = Guid.NewGuid();
        var paragraph1 = Guid.NewGuid();
        var paragraph2 = Guid.NewGuid();
        var article = new FrArticle { Id = first, Title = "first" };
        article.Paragraphs.Add(new FrParagraph { Id = paragraph1, Code = "p1" });
        article.Paragraphs.Add(new FrParagraph { Id = paragraph2, Code = "p2" });
        store.Insert(article);
        await upload(store, filePath(store, first), x, "x.bin");
        await upload(store, paragraphPath(store, first, paragraph1), x, "x-again.bin"); // same bytes, its own file in A
        await upload(store, paragraphPath(store, first, paragraph2), y, "y.bin");
        var second = Guid.NewGuid();
        store.Insert(new FrArticle { Id = second, Title = "second" });
        await upload(store, filePath(store, second), y, "y-too.bin");
        // a second revision of the first article, holding the same three file values
        store.Execute(store.CreateTransaction().EnableRevisions(first, out var published));
        store.Execute(store.CreateTransaction().CreateRevision(first, published, RevisionType.Preliminary));
        Assert.AreEqual(6, stored(store, first).Count);
        Assert.AreEqual(4, filesIn(ioA));

        var result = await local(store).RewriteFilesAsync(_a, _b);

        Assert.AreEqual(7, result.ValuesFound);
        Assert.AreEqual(7, result.ValuesRewritten);
        Assert.AreEqual(0, result.FailedCount);
        Assert.AreEqual(4, result.FilesCopied, "a stored file both revisions point at is copied once");
        Assert.AreEqual(2L * x.Length + 2L * y.Length, result.BytesCopied);
        var values = stored(store, first).Concat(stored(store, second)).ToList();
        Assert.AreEqual(7, values.Count);
        foreach (var value in values) {
            Assert.AreEqual(_b, value.StorageId);
            Assert.AreEqual(64, value.Hash.Length, "hashed by the target store: SHA256");
            Assert.IsTrue(FileValue.IsKeptByHash(value), "kept once per content by the target store");
        }
        Assert.AreEqual(2, values.Select(v => v.FileId).Distinct().Count(), "two contents, one copy each");
        Assert.AreEqual(2, filesIn(ioB));
        Assert.AreEqual(4, filesIn(ioA), "the old copies are left for the unreferenced cleanup");
        CollectionAssert.AreEqual(x, await read(store, values.First(v => v.Name == "x-again.bin")));
        CollectionAssert.AreEqual(y, await read(store, values.First(v => v.Name == "y-too.bin")));
        CollectionAssert.AreEquivalent(new[] { "x.bin", "x-again.bin", "y.bin", "y-too.bin" }, values.Select(v => v.Name).Distinct().ToArray(), "names stay");

        var again = await local(store).RewriteFilesAsync(_a, _b);
        Assert.AreEqual(0, again.ValuesFound, "nothing points into the old store any more");
    }

    [TestMethod]
    public async Task SameStore_CatchesOldFilesUpWithItsHashAndOneCopyPerContent() {
        var dbIo = new IOProviderMemory();
        var io = new IOProviderMemory();
        var x = bytes(2500, 3);
        Guid one = Guid.NewGuid(), two = Guid.NewGuid(), three = Guid.NewGuid();
        using (var store = open(dbIo, [new MultiFileStore(_a, io, 2)], _a)) {
            foreach (var id in new[] { one, two, three }) store.Insert(new FrArticle { Id = id, Title = id.ToString() });
            await upload(store, filePath(store, one), x, "one.bin");
            await upload(store, filePath(store, two), x, "two.bin");
            await upload(store, filePath(store, three), bytes(900, 4), "three.bin");
        }
        // the store turned to SHA256 and one copy per content: the files stored before are as they were
        using (var store = open(dbIo, [new MultiFileStore(_a, io, 2, true, FileHashAlgorithm.SHA256)], _a)) {
            var result = await local(store).RewriteFilesAsync(_a, _a);
            Assert.AreEqual(3, result.ValuesFound);
            Assert.AreEqual(3, result.ValuesRewritten);
            Assert.AreEqual(0, result.ValuesUpToDate);
            var values = new[] { one, two, three }.SelectMany(id => stored(store, id)).ToList();
            Assert.IsTrue(values.All(v => v.StorageId == _a && v.Hash.Length == 64 && FileValue.IsKeptByHash(v)));
            Assert.AreEqual(values[0].FileId, values[1].FileId, "the two uploads of the same bytes now share one copy");
            CollectionAssert.AreEqual(x, await read(store, values[1]));
            Assert.AreEqual(3 + 2, filesIn(io), "two kept-by-hash copies beside the three old files");

            var again = await local(store).RewriteFilesAsync(_a, _a);
            Assert.AreEqual(3, again.ValuesUpToDate);
            Assert.AreEqual(0, again.ValuesRewritten);
            Assert.AreEqual(0, again.FilesCopied);
        }
    }

    [TestMethod]
    public async Task ImplicitStore_StaysReadableUnderAConfiguredDefault_AndRewritesIntoIt() {
        var dbIo = new IOProviderMemory();
        var ioB = new IOProviderMemory();
        var x = bytes(1200, 5);
        var id = Guid.NewGuid();
        using (var store = open(dbIo, null, null)) { // no store configured: the implicit one
            store.Insert(new FrArticle { Id = id, Title = "old" });
            await upload(store, filePath(store, id), x, "old.bin");
            Assert.AreEqual(Guid.Empty, stored(store, id).Single().StorageId);
        }
        using (var store = open(dbIo, [new MultiFileStore(_b, ioB, 2, true, FileHashAlgorithm.SHA256)], _b)) {
            var old = stored(store, id).Single();
            // the configured default is elsewhere: the file must still be read from the implicit store
            CollectionAssert.AreEqual(x, await read(store, old));
            Assert.IsTrue(await local(store).FileExistsAsync(old));

            var result = await local(store).RewriteFilesAsync(Guid.Empty, _b);
            Assert.AreEqual(1, result.ValuesRewritten);
            var moved = stored(store, id).Single();
            Assert.AreEqual(_b, moved.StorageId);
            CollectionAssert.AreEqual(x, await read(store, moved));
            Assert.AreEqual(1, filesIn(ioB));
        }
    }

    [TestMethod]
    public async Task UnreadableFile_IsReportedAndLeftAsItWas() {
        var dbIo = new IOProviderMemory();
        var ioA = new IOProviderMemory();
        var ioB = new IOProviderMemory();
        using var store = open(dbIo, [new MultiFileStore(_a, ioA, 2), new MultiFileStore(_b, ioB, 2)], _a);
        Guid kept = Guid.NewGuid(), lost = Guid.NewGuid();
        store.Insert(new FrArticle { Id = kept, Title = "kept" });
        store.Insert(new FrArticle { Id = lost, Title = "lost" });
        await upload(store, filePath(store, kept), bytes(800, 6), "kept.bin");
        await upload(store, filePath(store, lost), bytes(700, 7), "lost.bin");
        ioA.DeleteFileIfItExists(ioA.GetFiles().Single(f => f.Key.Contains("lost")).KeyOf()); // gone behind the store's back

        var result = await local(store).RewriteFilesAsync(_a, _b);

        Assert.AreEqual(2, result.ValuesFound);
        Assert.AreEqual(1, result.ValuesRewritten);
        Assert.AreEqual(1, result.FailedCount);
        var failure = result.Failures.Single();
        Assert.AreEqual(lost, failure.NodeId);
        Assert.AreEqual("lost.bin", failure.FileName);
        Assert.AreEqual("File", failure.Property);
        Assert.IsTrue(failure.Reason.Length > 0);
        Assert.AreEqual(_a, stored(store, lost).Single().StorageId, "left pointing at the old store");
        Assert.AreEqual(_b, stored(store, kept).Single().StorageId);
    }

    [TestMethod]
    public async Task NothingIsQueuedForIndexing() {
        var dbIo = new IOProviderMemory();
        using var store = open(dbIo, [new MultiFileStore(_a, new IOProviderMemory(), 2), new MultiFileStore(_b, new IOProviderMemory(), 2)], _a, textIndex: true);
        var id = Guid.NewGuid();
        store.Insert(new FrArticle { Id = id, Title = "indexed" });
        await upload(store, filePath(store, id), bytes(500, 8), "a.bin");
        var before = pendingTasks(store);
        Assert.IsTrue(before > 0, "the text index is on, so the insert and the upload queued indexing");

        var result = await local(store).RewriteFilesAsync(_a, _b);

        Assert.AreEqual(1, result.ValuesRewritten);
        Assert.AreEqual(before, pendingTasks(store), "moving a file changes nothing an index reads");
    }

    [TestMethod]
    public async Task Cancelling_StopsTheRewrite() {
        var dbIo = new IOProviderMemory();
        using var store = open(dbIo, [new MultiFileStore(_a, new IOProviderMemory(), 2), new MultiFileStore(_b, new IOProviderMemory(), 2)], _a);
        var id = Guid.NewGuid();
        store.Insert(new FrArticle { Id = id, Title = "x" });
        await upload(store, filePath(store, id), bytes(500, 9), "a.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => local(store).RewriteFilesAsync(_a, _b, null, cancellation.Token));
        Assert.AreEqual(_a, stored(store, id).Single().StorageId);
    }
}
