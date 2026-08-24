using Relatude.DB.IO;
using System.Net.Sockets;
using System.Text;

namespace Relatude.Providers;

/// <summary>
/// Integration tests for the SDK-free Azure blob IO provider, running against Azurite.
/// The tests are inconclusive (skipped) when no Azurite is listening on 127.0.0.1:10000, start one with:
///   docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0
/// or: npx azurite-blob
/// The full connection string form (instead of UseDevelopmentStorage=true) is used on purpose,
/// so the account name/key/endpoint parsing and SharedKey signing paths are what is being tested.
/// </summary>
[TestClass]
public class AzureBlobProviderTests {
    const string _connectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1";
    static string newContainerName() => "relatude-tests-" + Guid.NewGuid().ToString("N")[..12];

    static void requireAzurite() {
        try {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", 10000).Wait(500)) throw new Exception();
        } catch {
            Assert.Inconclusive("Azurite is not running on 127.0.0.1:10000, blob integration tests skipped. ");
        }
    }
    static byte[] bytes(string s) => Encoding.UTF8.GetBytes(s);

    [TestMethod]
    public void AppendFlushAndReadBackRoundTrip() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: false);
        string[] key = ["db.00000001.bin"];

        using (var append = io.OpenAppend(key)) {
            append.Append(bytes("hello "));
            append.Append(bytes("blob "));
            // read back from the write buffer before anything is flushed
            var buffered = new byte[5];
            append.Get(6, 5, buffered);
            Assert.AreEqual("blob ", Encoding.UTF8.GetString(buffered));
            append.Flush(true);
            append.Append(bytes("world"));
            // read spanning flushed data and write buffer
            var spanning = new byte[10];
            append.Get(6, 10, spanning);
            Assert.AreEqual("blob world", Encoding.UTF8.GetString(spanning));
            Assert.AreEqual(16, append.Length);
        }

        Assert.IsTrue(io.Exists(key));
        Assert.IsFalse(io.DoesNotExistOrIsEmpty(key));
        Assert.AreEqual(16, io.GetFileSizeOrZeroIfUnknown(key));

        using (var read = io.OpenRead(key, 0)) {
            Assert.AreEqual(16, read.Length);
            Assert.AreEqual("hello blob world", Encoding.UTF8.GetString(read.Read(16)));
            Assert.IsFalse(read.More());
        }
        // reads from an offset, and reopening an existing blob for further appends
        using (var read = io.OpenRead(key, 6)) {
            Assert.AreEqual("blob", Encoding.UTF8.GetString(read.Read(4)));
        }
        using (var append = io.OpenAppend(key)) {
            Assert.AreEqual(16, append.Length);
            append.Append(bytes("!"));
        }
        using (var read = io.OpenRead(key, 0)) {
            Assert.AreEqual("hello blob world!", Encoding.UTF8.GetString(read.Read(17)));
        }
    }

    [TestMethod]
    public async Task AsyncAppendAndAsyncReadWork() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: false);
        string[] key = ["db.00000002.bin"];
        var payload = new byte[300_000]; // forces the read ahead buffer to refill at least once
        Random.Shared.NextBytes(payload);

        using (var append = (AzureBlobIOAppendStream)io.OpenAppend(key)) {
            await append.AppendAsyncNoChecksumOrLock(payload, payload.Length);
            append.Flush(true);
        }
        using (var read = io.OpenRead(key, 0)) {
            var readBack = new byte[payload.Length];
            var position = 0;
            while (position < readBack.Length) {
                var chunk = new byte[64_000];
                var n = await read.ReadAsync(chunk, chunk.Length);
                Assert.IsTrue(n > 0);
                Array.Copy(chunk, 0, readBack, position, n);
                position += n;
            }
            CollectionAssert.AreEqual(payload, readBack);
        }
    }

    [TestMethod]
    public void FilesAndVirtualFoldersAreListedAndDeleted() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: false);
        using (var a = io.OpenAppend(["state.bin"])) a.Append(bytes("root"));
        using (var b = io.OpenAppend(new[] { "backups", "2026", "state copy.bin" })) b.Append(bytes("nested"));

        var files = io.GetFiles();
        CollectionAssert.AreEquivalent(new[] { "state.bin", "backups/2026/state copy.bin" }, files.Select(f => f.Key).ToArray());
        Assert.AreEqual(6, files.Single(f => f.Key.EndsWith("copy.bin")).Size);
        Assert.IsTrue(io.Exists(new[] { "backups", "2026", "state copy.bin" }));

        var folders = io.GetFoldersAsync([], recursive: true, withFiles: true).Result;
        var backups = folders.Single(f => f.Name == "backups");
        Assert.IsTrue(backups.HasSubFolders);
        var year = backups.SubFolders.Single();
        Assert.AreEqual("2026", year.Name);
        Assert.AreEqual("backups/2026/state copy.bin", year.Files.Single().Key);

        using (var read = io.OpenRead(new[] { "backups", "2026", "state copy.bin" }, 0)) {
            Assert.AreEqual("nested", Encoding.UTF8.GetString(read.Read(6)));
        }

        io.DeleteFolderIfItExists(["backups"]);
        Assert.IsFalse(io.Exists(new[] { "backups", "2026", "state copy.bin" }));
        io.DeleteFileIfItExists(["state.bin"]);
        Assert.IsFalse(io.Exists(["state.bin"]));
        Assert.AreEqual(0, io.GetFiles().Length);
        io.DeleteFileIfItExists(["state.bin"]); // deleting a missing blob is a no-op
    }

    [TestMethod]
    public void LargeFlushIsSplitIntoMultipleAppendBlocks() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: false);
        string[] key = ["files.00000001.bin"];
        var payload = new byte[22 * 1024 * 1024]; // over the 20MB flush segmentation limit
        Random.Shared.NextBytes(payload);
        using (var append = io.OpenAppend(key)) {
            append.Append(payload);
        }
        Assert.AreEqual(payload.Length, io.GetFileSizeOrZeroIfUnknown(key));
        using var read = io.OpenRead(key, 21 * 1024 * 1024);
        var tail = read.Read(1024 * 1024);
        CollectionAssert.AreEqual(payload[^(1024 * 1024)..], tail);
    }

    [TestMethod]
    public void LeasesBlockOtherWritersUntilReleased() {
        requireAzurite();
        var container = newContainerName();
        var io = new AzureBlobIOProvider(container, _connectionString, lockBlob: false);
        var client = io.Client;
        var key = "db.00000009.bin";
        client.CreateAppendBlobIfNotExists(key);

        var leaseId = client.AcquireLease(key);
        var conflict = Assert.ThrowsException<AzureBlobRequestException>(() => client.AcquireLease(key));
        Assert.AreEqual("LeaseAlreadyPresent", conflict.ErrorCode);
        Assert.ThrowsException<AzureBlobRequestException>(() => client.AppendBlock(key, bytes("x"), 1, null, 0));
        client.AppendBlock(key, bytes("x"), 1, leaseId, 0);
        client.ReleaseLease(key, leaseId);
        client.AppendBlock(key, bytes("y"), 1, null, 1);
        Assert.AreEqual(2, client.GetProperties(key)!.ContentLength);

        // a locked provider stream leaves the blob unleased after dispose
        using (var append = io.OpenAppend(["db.00000010.bin"])) append.Append(bytes("data"));
        var io2 = new AzureBlobIOProvider(container, _connectionString, lockBlob: true);
        using (var append = io2.OpenAppend(["db.00000010.bin"])) append.Append(bytes(" more"));
        using (var read = io2.OpenRead(["db.00000010.bin"], 0)) {
            Assert.AreEqual("data more", Encoding.UTF8.GetString(read.Read(9)));
        }
    }

    [TestMethod]
    public void AppendPositionGuardDetectsPositionMismatch() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: false);
        var client = io.Client;
        var key = "db.00000011.bin";
        client.CreateAppendBlobIfNotExists(key);
        client.AppendBlock(key, bytes("12345"), 5, null, 0);
        // appending at an already committed position must not append twice when lengths reconcile...
        client.AppendBlock(key, bytes("12345"), 5, null, 0);
        Assert.AreEqual(5, client.GetProperties(key)!.ContentLength);
        // ...and must throw when they do not
        var mismatch = Assert.ThrowsException<AzureBlobRequestException>(() => client.AppendBlock(key, bytes("123"), 3, null, 0));
        Assert.AreEqual("AppendPositionConditionNotMet", mismatch.ErrorCode);
    }
}
