using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.FileConversion.ImageEncoders;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public interface IShfDoc {
    Guid Id { get; set; }
    string Title { get; set; }
    FileValue Attachment { get; set; }
}

/// <summary>
/// MultiFileStore with sameHashSameFile: the same content is stored once whatever it is called, the
/// file value points at the shared copy, deleting a value leaves the copy for the unreferenced sweep,
/// and the sweep does not take a copy an upload was just given. Also the SHA256 option, the multipart
/// path, a provider that cannot rename (blob storage), and that the store is unchanged with both off.
/// </summary>
[TestClass]
public class SameHashSameFileTests {
    static readonly Guid _propertyId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    static byte[] bytes(int size, int seed) {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
    static async Task<FileValue> insert(MultiFileStore store, byte[] data, string fileName) {
        using var ms = new MemoryStream(data);
        var r = await store.InsertAsync(Guid.NewGuid(), ms, fileName);
        return FileValue.CreateNew(fileName, r.Length, r.FileHash, store.Id, r.FileId, r.StoreKey, new PropertyPath(Guid.NewGuid(), _propertyId));
    }
    static async Task<byte[]> extract(MultiFileStore store, FileValue value) {
        using var ms = new MemoryStream();
        await store.ExtractAsync(value, ms);
        return ms.ToArray();
    }
    static FileMeta[] storedFiles(IIOProvider io) => io.GetFiles().Where(f => f.Key.StartsWith(FileKeyUtility.MultiFileStoreFolderKey + "/")).ToArray();

    [TestMethod]
    public async Task SameBytes_AreStoredOnce_WhateverTheyAreCalled() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
        var data = bytes(5000, 1);
        var first = await insert(store, data, "report.pdf");
        var second = await insert(store, data, "Copy of report.PDF");
        Assert.AreEqual(first.FileId, second.FileId);
        CollectionAssert.AreEqual(FileValue.GetFileKeyData(first), FileValue.GetFileKeyData(second));
        Assert.AreEqual(Convert.ToHexString(MD5.HashData(data)), first.Hash);
        Assert.AreEqual(Guid.ParseExact(first.Hash, "N"), first.FileId, "the file id is the hash");
        Assert.AreEqual(1, storedFiles(io).Length, "the second upload must not leave a file behind");
        CollectionAssert.AreEqual(data, await extract(store, second));
        Assert.IsTrue(await store.ContainsFileAsync(first));
    }

    [TestMethod]
    public async Task DifferentBytes_AreStoredSeparately() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
        var a = await insert(store, bytes(5000, 1), "a.bin");
        var b = await insert(store, bytes(5000, 2), "a.bin");
        var empty = await insert(store, [], "empty.txt");
        var emptyAgain = await insert(store, [], "other.txt");
        Assert.AreNotEqual(a.FileId, b.FileId);
        Assert.AreEqual(empty.FileId, emptyAgain.FileId, "empty files are content too");
        Assert.AreEqual(3, storedFiles(io).Length);
        CollectionAssert.AreEqual(bytes(5000, 2), await extract(store, b));
        Assert.AreEqual(0, (await extract(store, emptyAgain)).Length);
    }

    [TestMethod]
    public async Task WithBothOptionsOff_EveryUploadKeepsItsOwnFileAndId() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2);
        Assert.AreEqual(HashAlgorithmName.MD5, store.HashAlgorithm);
        var data = bytes(3000, 3);
        var uploadId = Guid.NewGuid();
        using var ms = new MemoryStream(data);
        var r = await store.InsertAsync(uploadId, ms, "a.bin");
        Assert.AreEqual(uploadId, r.FileId, "the id asked for is the id used");
        Assert.AreEqual(Convert.ToHexString(MD5.HashData(data)), r.FileHash);
        await insert(store, data, "a.bin");
        Assert.AreEqual(2, storedFiles(io).Length);
    }

    [TestMethod]
    public async Task Sha256_HashesWithSha256_AndStillKeepsOneCopy() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true, hashAlgorithm: FileHashAlgorithm.SHA256);
        var data = bytes(7000, 4);
        var first = await insert(store, data, "a.bin");
        var second = await insert(store, data, "b.bin");
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(data)), first.Hash);
        Assert.AreEqual(first.FileId, second.FileId);
        Assert.AreEqual(1, storedFiles(io).Length);
        // the whole hash is in the stored file's key, not only the 128 bits of the file id
        var key = storedFiles(io).Single().Key.Replace("/", "");
        StringAssert.Contains(key, first.Hash[4..].ToLowerInvariant());
        CollectionAssert.AreEqual(data, await extract(store, second));
    }

    [TestMethod]
    public async Task Sha256_WithoutSameHashSameFile_OnlyChangesTheHash() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, hashAlgorithm: FileHashAlgorithm.SHA256);
        var data = bytes(2000, 5);
        var a = await insert(store, data, "a.bin");
        var b = await insert(store, data, "a.bin");
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(data)), a.Hash);
        Assert.AreNotEqual(a.FileId, b.FileId);
        Assert.AreEqual(2, storedFiles(io).Length);
    }

    [TestMethod]
    public async Task ParallelUploadsOfTheSameBytes_EndAsOneFile() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
        var data = bytes(200_000, 6);
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => insert(store, data, "p" + i + ".bin"))));
        Assert.AreEqual(1, values.Select(v => v.FileId).Distinct().Count());
        Assert.AreEqual(1, storedFiles(io).Length);
        CollectionAssert.AreEqual(data, await extract(store, values[^1]));
    }

    [TestMethod]
    public async Task Delete_LeavesASharedFile_ButStillDeletesOneNotKeptByHash() {
        var io = new IOProviderMemory();
        var storeId = Guid.NewGuid();
        using var store = new MultiFileStore(storeId, io, 2, sameHashSameFile: true);
        var data = bytes(4000, 7);
        var first = await insert(store, data, "a.bin");
        var second = await insert(store, data, "b.bin");
        await store.DeleteAsync(first);
        Assert.IsTrue(await store.ContainsFileAsync(second), "the other value still points at the bytes");
        // an upload that was never completed is not kept by its hash, and goes
        var partialId = Guid.NewGuid();
        var partialKey = await store.InitiatePartialUpload(partialId, "partial.bin");
        await store.AppendDataAsync(partialId, partialKey, data, 100);
        var partial = FileValue.CreateNew("partial.bin", 100, string.Empty, storeId, partialId, partialKey, new PropertyPath(Guid.NewGuid(), _propertyId));
        Assert.AreEqual(2, storedFiles(io).Length);
        await store.DeleteAsync(partial);
        Assert.AreEqual(1, storedFiles(io).Length);
        // turned off again: the shared file is still read, and still not deleted with a value
        using var off = new MultiFileStore(storeId, io, 2);
        CollectionAssert.AreEqual(data, await extract(off, second));
        await off.DeleteAsync(second);
        Assert.IsTrue(await off.ContainsFileAsync(second));
    }

    [TestMethod]
    public async Task MultipartUpload_IsKeptByHash_AndADuplicateLeavesNoPartBehind() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
        var data = bytes(300_000, 8);
        async Task<FileInsertResult> upload() {
            var uploadId = Guid.NewGuid();
            var key = await store.InitiatePartialUpload(uploadId, "big.bin");
            using var hash = IncrementalHash.CreateHash(store.HashAlgorithm);
            for (var offset = 0; offset < data.Length; offset += 64_000) {
                var part = data[offset..Math.Min(offset + 64_000, data.Length)];
                await store.AppendDataAsync(uploadId, key, part, part.Length);
                hash.AppendData(part);
            }
            return await store.CompletePartialUpload(uploadId, key, Convert.ToHexString(hash.GetHashAndReset()), data.Length);
        }
        var first = await upload();
        var second = await upload();
        var direct = await insert(store, data, "big.bin");
        Assert.AreEqual(first.FileId, second.FileId);
        Assert.AreEqual(first.FileId, direct.FileId, "a multipart upload and a whole one of the same bytes are the same file");
        Assert.AreEqual(1, storedFiles(io).Length);
        // a completion claiming more bytes than arrived is refused instead of filed under a wrong name
        var shortId = Guid.NewGuid();
        var shortKey = await store.InitiatePartialUpload(shortId, "short.bin");
        await store.AppendDataAsync(shortId, shortKey, data, 10);
        await Assert.ThrowsExactlyAsync<Exception>(() => store.CompletePartialUpload(shortId, shortKey, first.FileHash, 20));
    }

    [TestMethod]
    public async Task OnAProviderThatCannotRename_TheFileIsCopiedIntoPlace() {
        var io = new NoRenameIO(new IOProviderMemory());
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
        var data = bytes(10_000, 9);
        var first = await insert(store, data, "a.bin");
        var second = await insert(store, data, "b.bin");
        Assert.AreEqual(first.FileId, second.FileId);
        Assert.AreEqual(1, storedFiles(io).Length, "the uploaded copy must be deleted after it is copied");
        CollectionAssert.AreEqual(data, await extract(store, second));
    }

    [TestMethod]
    public async Task OnDisk_TheUploadIsRenamedIntoPlace() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-same-hash-" + Guid.NewGuid().ToString("N"));
        try {
            var io = new IOProviderDisk(dir);
            using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
            var data = bytes(10_000, 10);
            var first = await insert(store, data, "a.bin");
            var second = await insert(store, data, "b.bin");
            Assert.AreEqual(first.FileId, second.FileId);
            Assert.AreEqual(1, Directory.GetFiles(Path.Combine(dir, FileKeyUtility.MultiFileStoreFolderKey), "*", SearchOption.AllDirectories).Length);
            Assert.IsTrue(store.TryGetLocalFilePath(second, out var localPath));
            CollectionAssert.AreEqual(data, File.ReadAllBytes(localPath));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task UnreferencedSweep_KeepsAFileAnUploadWasJustGiven() {
        var io = new IOProviderMemory();
        using var store = new MultiFileStore(Guid.NewGuid(), io, 2, sameHashSameFile: true);
        var data = bytes(1000, 11);
        await insert(store, data, "a.bin"); // nothing will reference it: an orphan
        await Task.Delay(30);
        var cutoff = DateTime.UtcNow; // the file is older than this, so its age does not protect it
        await Task.Delay(30);
        // a sweep that collected its references before this upload linked the orphan up again
        await insert(store, data, "b.bin");
        var kept = await store.DeleteUnreferenced(new HashSet<string>(), keepFilesNewerThanUtc: cutoff);
        Assert.AreEqual(0, kept.TotalFilesDeleted, "the upload was handed this file after the cutoff");
        Assert.AreEqual(1, storedFiles(io).Length);
        // handed out before the cutoff, it is an ordinary unreferenced file
        var deleted = await store.DeleteUnreferenced(new HashSet<string>(), keepFilesNewerThanUtc: DateTime.UtcNow.AddMinutes(5));
        Assert.AreEqual(1, deleted.TotalFilesDeleted);
        Assert.AreEqual(0, storedFiles(io).Length);
        // and the next upload of those bytes stores them again
        var again = await insert(store, data, "c.bin");
        CollectionAssert.AreEqual(data, await extract(store, again));
    }

    // through the database: two nodes uploading the same bytes share the file, removing it from one
    // leaves it for the other, and a multipart upload is hashed with the store's algorithm
    static (NodeStore store, DataStoreLocal data, IOProviderMemory filesIo) open(FileHashAlgorithm hashAlgorithm) {
        var dm = new Datamodel();
        dm.Add<IShfDoc>();
        var filesIo = new IOProviderMemory();
        var storeId = Guid.NewGuid();
        var data = DataStoreLocal.Open(dm, new SettingsLocal { DefaultFileStore = storeId }, new IOProviderMemory(),
            [new MultiFileStore(storeId, filesIo, 2, sameHashSameFile: true, hashAlgorithm: hashAlgorithm)]);
        return (new NodeStore(data), data, filesIo);
    }
    static Guid attachmentId(DataStoreLocal data)
        => data.Datamodel.NodeTypes.Values.Single(t => t.CodeName == nameof(IShfDoc)).AllProperties.Values.Single(p => p.CodeName == nameof(IShfDoc.Attachment)).Id;
    static Guid addDoc(NodeStore store, string title) {
        var doc = store.Create<IShfDoc>();
        doc.Title = title;
        store.Insert(doc);
        return doc.Id;
    }

    [TestMethod]
    public async Task ThroughTheDatabase_NodesShareTheFile_AndRemovingItFromOneKeepsItForTheOther() {
        var (store, data, filesIo) = open(FileHashAlgorithm.MD5);
        using (store) {
            var propertyId = attachmentId(data);
            var bytesA = bytes(8000, 12);
            var doc1 = addDoc(store, "one");
            var doc2 = addDoc(store, "two");
            var v1 = await store.FileUploadAsync(doc1, propertyId, new MemoryStream(bytesA), "one.bin");
            var v2 = await store.FileUploadAsync(doc2, propertyId, new MemoryStream(bytesA), "two.bin");
            Assert.AreEqual(v1.FileId, v2.FileId);
            Assert.AreEqual("two.bin", v2.Name, "the name stays the node's own");
            Assert.AreEqual(1, storedFiles(filesIo).Length);

            await store.FileDeleteAsync(doc1, propertyId);
            CollectionAssert.AreEqual(bytesA, await store.FileDownloadAsync(doc2, propertyId));
            var sweep = await data.DeleteUnreferencedFilesAsync(countOnly: false);
            Assert.AreEqual(0, sweep.TotalFilesDeleted, "doc2 still references the shared file");
            CollectionAssert.AreEqual(bytesA, await store.FileDownloadAsync(doc2, propertyId));
        }
    }

    [TestMethod]
    public async Task ThroughTheDatabase_AMultipartUploadIsHashedWithTheStoresAlgorithm() {
        var (store, data, filesIo) = open(FileHashAlgorithm.SHA256);
        using (store) {
            var propertyId = attachmentId(data);
            var content = bytes(150_000, 13);
            var doc1 = addDoc(store, "whole");
            var doc2 = addDoc(store, "parts");
            var whole = await store.FileUploadAsync(doc1, propertyId, new MemoryStream(content), "whole.bin");
            var uploadId = await data.InitiateMultipartUploadAsync(new PropertyPath(doc2, propertyId), "parts.bin");
            for (var offset = 0; offset < content.Length; offset += 50_000) {
                var part = content[offset..Math.Min(offset + 50_000, content.Length)];
                await data.AppendMultipartUploadAsync(uploadId, part, part.Length);
            }
            var parts = await data.FinalizeMultipartUploadAsync(uploadId, maxWaitForMetaUpdate: 0);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(content)), whole.Hash);
            Assert.AreEqual(whole.Hash, parts.Hash);
            Assert.AreEqual(whole.FileId, parts.FileId);
            Assert.AreEqual(1, storedFiles(filesIo).Length);
            CollectionAssert.AreEqual(content, await store.FileDownloadAsync(doc2, propertyId));
        }
    }

    [TestMethod]
    public void ConversionKey_IsUnchangedForOwnFiles_AndCarriesTheSourceFormatForSharedOnes() {
        var path = new PropertyPath(Guid.NewGuid(), _propertyId);
        var adj = new FileAdjustmentImage { RequestedFormat = FileFormat.Png, Width = 100 };
        var hash = Convert.ToHexString(MD5.HashData(new byte[] { 1, 2, 3 }));
        var own = FileValue.CreateNew("a.png", 3, hash, Guid.Empty, Guid.NewGuid(), [], path);
        Assert.IsFalse(FileValue.IsKeptByHash(own));
        Assert.AreEqual(own.FileId.CombineHashGuid(adj.GetKey()), FileIdWithAdjustment.KeyOf(own, adj), "the key every cached conversion was stored under");
        Assert.IsNull(FileIdWithAdjustment.Of(own, adj, path).SharedSourceFormat);
        var sharedId = Guid.ParseExact(hash, "N");
        FileValue shared(string name) => FileValue.CreateNew(name, 3, hash, Guid.Empty, sharedId, [], path);
        Assert.IsTrue(FileValue.IsKeptByHash(shared("a.png")));
        Assert.AreEqual(FileIdWithAdjustment.KeyOf(shared("a.png"), adj), FileIdWithAdjustment.KeyOf(shared("another name.png"), adj), "same bytes, same format: one conversion");
        Assert.AreNotEqual(FileIdWithAdjustment.KeyOf(shared("a.png"), adj), FileIdWithAdjustment.KeyOf(shared("a.bin"), adj), "same bytes named as another format");
        Assert.AreEqual(FileIdWithAdjustment.KeyOf(shared("a.bin"), adj), FileIdWithAdjustment.Of(shared("a.bin"), adj, path).GetKey(), "the request and the engine's status lookup agree");
    }

    [TestMethod]
    public async Task ThroughTheDatabase_SharedBytesShareConversions_ButACopyNamedAsAnotherFormatDoesNotPoisonThem() {
        var (store, data, _) = open(FileHashAlgorithm.MD5);
        using (store) {
            var propertyId = attachmentId(data);
            byte[] png;
            using (var image = NativeImage.Create(64, 48)) png = image.Encode(FileFormat.Png);
            var first = addDoc(store, "first");
            var second = addDoc(store, "second");
            var misnamed = addDoc(store, "misnamed");
            await store.FileUploadAsync(first, propertyId, new MemoryStream(png), "first.png");
            await store.FileUploadAsync(second, propertyId, new MemoryStream(png), "second.png");
            var bin = await store.FileUploadAsync(misnamed, propertyId, new MemoryStream(png), "picture.bin");
            Assert.IsTrue(FileValue.IsKeptByHash(bin));
            static FileAdjustmentImage thumbnail() => new() { RequestedFormat = FileFormat.Png, Width = 16 };

            // nothing converts a .bin: the failure is cached under the conversion's key
            var failed = await data.GetFileStreamAndState(new PropertyPath(misnamed, propertyId), thumbnail(), 2000);
            Assert.IsFalse(failed.IsReady);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            FileConversionProgressInfo? progress = null;
            while (waited.ElapsedMilliseconds < 10_000 && !(data.TryGetConversionInfo(new PropertyPath(misnamed, propertyId), thumbnail(), false, out progress) && progress.Status == FileConversionStatus.Error))
                await Task.Delay(20);
            Assert.AreEqual(FileConversionStatus.Error, progress?.Status, "the failure should be cached by now");

            var converted = await data.GetFileStreamAndState(new PropertyPath(first, propertyId), thumbnail(), 10_000);
            Assert.IsTrue(converted.IsReady, "the png must convert, not be served the failure cached for its .bin twin");
            Assert.AreNotEqual(failed.ConversionId, converted.ConversionId);
            var shared = await data.GetFileStreamAndState(new PropertyPath(second, propertyId), thumbnail(), 10_000);
            Assert.IsTrue(shared.IsReady);
            Assert.AreEqual(converted.ConversionId, shared.ConversionId, "same bytes, same format: the cached conversion is shared");
            using (var thumb = NativeImage.Load(new MemoryStream(shared.GetBytes()))) Assert.AreEqual(16, thumb.Width);
        }
    }

    /// <summary>A provider that cannot rename files, like blob storage.</summary>
    sealed class NoRenameIO(IIOProvider inner) : IIOProvider {
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public IAppendStream OpenAppend(string[] path) => inner.OpenAppend(path);
        public bool Exists(string[] path) => inner.Exists(path);
        public bool DoesNotExistOrIsEmpty(string[] path) => inner.DoesNotExistOrIsEmpty(path);
        public void DeleteFileIfItExists(string[] path) => inner.DeleteFileIfItExists(path);
        public FileMeta[] GetFiles() => inner.GetFiles();
        public long GetFileSizeOrZeroIfUnknown(string[] path) => inner.GetFileSizeOrZeroIfUnknown(path);
        public bool CanRenameFile => false;
        public void RenameFile(string[] path, string[] newPath) => throw new NotSupportedException();
        public bool CanRenameFolder => false;
        public void RenameFolder(string[] path, string[] newPath) => throw new NotSupportedException();
        public bool SupportsEmptyFolders => inner.SupportsEmptyFolders;
        public bool CanTruncate => false;
        public void TruncateFile(string[] path, long newLength) => throw new NotSupportedException();
        public void CloseAllOpenStreams() => inner.CloseAllOpenStreams();
        public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
        public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
        public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => false;
        public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
        public bool DeleteFolderIfEmpty(string[] path) => inner.DeleteFolderIfEmpty(path);
        public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
        public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);
    }
}
