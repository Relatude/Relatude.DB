using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using System.Net.Sockets;
using System.Text;

namespace Relatude.Providers;

[Node]
public class AzLeaseNote {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

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
    public async Task FolderListingShowsTheSizeOfAFileJustWritten() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: false);
        // the listing prefers the provider's tracked meta, which the closing stream updates: it used
        // to reset the size to 0 there, so an upload read "0 KB" until something re-synced the list
        using (var a = io.OpenAppend(["uploads", "photo.jpg"])) a.Append(bytes("twelve bytes"));
        var folder = await io.GetFolderAsync(["uploads"], recursive: false, withFiles: true);
        Assert.AreEqual(12, folder.Files.Single().Size);
        io.CopyFile(["uploads", "photo.jpg"], ["uploads", "copy.jpg"]);
        folder = await io.GetFolderAsync(["uploads"], recursive: false, withFiles: true);
        Assert.AreEqual(12, folder.Files.Single(f => f.Key.EndsWith("copy.jpg")).Size);
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
        var conflict = Assert.ThrowsExactly<AzureBlobRequestException>(() => client.AcquireLease(key));
        Assert.AreEqual("LeaseAlreadyPresent", conflict.ErrorCode);
        Assert.ThrowsExactly<AzureBlobRequestException>(() => client.AppendBlock(key, bytes("x"), 1, null, 0));
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

    // ---- leases ----

    static string? leaseState(AzureBlobIOProvider io, string blob) => io.Client.GetProperties(blob)?.LeaseState;

    [TestMethod]
    public void LeaseOnABlobInAFolder_IsTakenAndLetGoOf() {
        // every database file is in a folder (data/, state/, indexes/...). The old provider wrote the lease
        // id to a file named after the blob next to the executable, which threw for a name with a folder in
        // it - right after an infinite lease was taken - so the blob stayed leased for good and the stream
        // was never handed out
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: true);
        string[] key = ["data", "db.00000001.bin"];
        using (var append = io.OpenAppend(key)) {
            append.Append(bytes("log"));
            Assert.AreEqual("leased", leaseState(io, "data/db.00000001.bin"));
            Assert.AreEqual("fixed", io.Client.GetProperties("data/db.00000001.bin")!.LeaseDuration, "a lease that ends by itself");
        }
        Assert.AreEqual("available", leaseState(io, "data/db.00000001.bin"));
        using (var read = io.OpenRead(key, 0)) {
            Assert.AreEqual("leased", leaseState(io, "data/db.00000001.bin"));
            Assert.AreEqual("log", Encoding.UTF8.GetString(read.Read(3)));
        }
        Assert.AreEqual("available", leaseState(io, "data/db.00000001.bin"));
        Assert.AreEqual(0, io.GetLeasedFiles().Length);
    }

    [TestMethod]
    public void ReadersOfOneBlob_ShareOneLease() {
        // a lease is exclusive: a second reader taking one of its own used to fail as "locked by another process"
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: true);
        string[] key = ["files", "ab", "cd", "photo.jpg"];
        using (var append = io.OpenAppend(key)) append.Append(bytes("pixels"));
        var first = io.OpenRead(key, 0);
        var second = io.OpenRead(key, 0);
        Assert.AreEqual("pixels", Encoding.UTF8.GetString(second.Read(6)));
        first.Dispose();
        Assert.AreEqual("leased", leaseState(io, "files/ab/cd/photo.jpg"), "the other reader still holds it");
        second.Dispose();
        Assert.AreEqual("available", leaseState(io, "files/ab/cd/photo.jpg"));
    }

    [TestMethod]
    public void AnotherProcess_WaitsForTheLease_ThenGetsAFileLock() {
        requireAzurite();
        var container = newContainerName();
        var holder = new AzureBlobIOProvider(container, _connectionString, lockBlob: true);
        var other = new AzureBlobIOProvider(container, _connectionString, lockBlob: true) { LeaseWaitTimeout = TimeSpan.FromSeconds(1) };
        string[] key = ["data", "db.00000001.bin"];
        var held = holder.OpenAppend(key);
        held.Append(bytes("x"));
        held.Flush(true);
        var error = Assert.ThrowsExactly<FileLockedException>(() => other.OpenAppend(key));
        // the same exception the disk provider throws for a held file: the open retry and the startup
        // restart treat it as a lock, never as corruption
        Assert.IsTrue(FileOpenRetry.IsSharingViolation(error));
        StringAssert.Contains(error.Message, "still running");
        other.GetFiles(); // a provider knows the blobs of its last listing
        Assert.ThrowsExactly<FileLockedException>(() => other.OpenRead(key, 0));
        held.Dispose();
        using (var append = other.OpenAppend(key)) append.Append(bytes("y")); // let go of: the other process gets it
        using (var read = other.OpenRead(key, 0)) Assert.AreEqual("xy", Encoding.UTF8.GetString(read.Read(2)));
    }

    [TestMethod]
    public void TheLeaseOfAProcessThatDied_EndsByItself() {
        // a process killed while it held a lease never lets go of it; a lease that is not renewed ends,
        // and the next process waits it out instead of failing
        requireAzurite();
        var container = newContainerName();
        var io = new AzureBlobIOProvider(container, _connectionString, lockBlob: true) { LeaseWaitTimeout = TimeSpan.FromSeconds(40) };
        io.Client.CreateAppendBlobIfNotExists("data/db.00000001.bin");
        io.Client.AcquireLease("data/db.00000001.bin", 15, Guid.NewGuid().ToString()); // the dead process's, nobody renews it
        var waited = System.Diagnostics.Stopwatch.StartNew();
        using (var append = io.OpenAppend(["data", "db.00000001.bin"])) append.Append(bytes("after"));
        Assert.IsTrue(waited.Elapsed > TimeSpan.FromSeconds(5), "it was held when the open began");
        Assert.AreEqual(5, io.GetFileSizeOrZeroIfUnknown(["data", "db.00000001.bin"]));
    }

    [TestMethod]
    public void AHeldLease_IsRenewedPastItsDuration() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: true) { LeaseDurationSeconds = 15 };
        using var append = io.OpenAppend(["data", "db.00000001.bin"]);
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Assert.AreEqual("leased", leaseState(io, "data/db.00000001.bin"), "renewed every 5 s");
        var conflict = Assert.ThrowsExactly<AzureBlobRequestException>(() => io.Client.AcquireLease("data/db.00000001.bin", 15, Guid.NewGuid().ToString()));
        Assert.AreEqual(409, conflict.StatusCode);
        append.Append(bytes("still mine"));
        append.Flush(true);
    }

    [TestMethod]
    public void AnExpiredLease_IsRenewedByTheNextWrite() {
        // a process paused past its lease (a debugger, renewals that could not get through): nobody took
        // the blob meanwhile, so the write renews the lease and goes ahead
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: true);
        var blob = "data/db.00000001.bin";
        io.Client.CreateAppendBlobIfNotExists(blob);
        // a lease nothing renews, handed straight to a stream
        var lease = new AzureBlobIOProvider.HeldLease(blob, io.Client.AcquireLease(blob, 15, Guid.NewGuid().ToString())) { Users = 1 };
        using var append = new AzureBlobIOAppendStream(io, io.Client, blob, lease, _ => { });
        Thread.Sleep(TimeSpan.FromSeconds(17));
        Assert.AreEqual("expired", leaseState(io, blob));
        append.Append(bytes("late"));
        append.Flush(true);
        Assert.AreEqual(4, io.Client.GetProperties(blob)!.ContentLength);
        Assert.AreEqual("leased", leaseState(io, blob));
    }

    [TestMethod]
    public void ALostLease_StopsTheWrites_AndStillLetsGoOfTheFile() {
        requireAzurite();
        var container = newContainerName();
        var io = new AzureBlobIOProvider(container, _connectionString, lockBlob: true);
        string[] key = ["data", "db.00000001.bin"];
        var append = io.OpenAppend(key);
        append.Append(bytes("first"));
        append.Flush(true);
        // someone breaks the lease and another process takes the blob
        io.Client.BreakLease("data/db.00000001.bin");
        var theirs = io.Client.AcquireLease("data/db.00000001.bin", 60, Guid.NewGuid().ToString());
        append.Append(bytes("second"));
        var error = Assert.ThrowsExactly<IOException>(() => append.Flush(true));
        StringAssert.Contains(error.Message, "no longer this process's");
        try { append.Dispose(); } catch (IOException) { } // the buffered write cannot go through
        Assert.AreEqual(5, io.Client.GetProperties("data/db.00000001.bin")!.ContentLength, "nothing written without the lease");
        io.Client.ReleaseLease("data/db.00000001.bin", theirs);
        // the failed close still gave the file back in this process: it is not "locked for writing"
        using (var again = io.OpenAppend(key)) Assert.AreEqual(5, again.Length);
    }

    [TestMethod]
    public void ALeaseThatNeverEnds_IsReported_AndCanBeBroken() {
        // what the old provider left behind
        requireAzurite();
        var container = newContainerName();
        var io = new AzureBlobIOProvider(container, _connectionString, lockBlob: true) { LeaseWaitTimeout = TimeSpan.FromMilliseconds(500) };
        io.Client.CreateAppendBlobIfNotExists("state/state.00000001.bin");
        io.Client.AcquireLease("state/state.00000001.bin"); // infinite, its id lost
        using var mine = io.OpenAppend(["data", "db.00000001.bin"]);
        io.GetFiles(); // a provider knows the blobs of its last listing

        var error = Assert.ThrowsExactly<FileLockedException>(() => io.OpenRead(["state", "state.00000001.bin"], 0));
        StringAssert.Contains(error.Message, "never ends");
        var leased = io.GetLeasedFiles().OrderBy(l => l.Key).ToArray();
        Assert.AreEqual(2, leased.Length);
        Assert.IsTrue(leased[0].HeldHere && !leased[0].Infinite, "data/db.00000001.bin, this provider's own");
        Assert.IsTrue(!leased[1].HeldHere && leased[1].Infinite, "state/state.00000001.bin, the stranded one");
        Assert.AreEqual(1, io.GetLeasedFiles(["state"]).Length);

        var (broken, skipped, errors) = io.BreakLeases(leased.Select(l => l.Key));
        Assert.AreEqual(1, broken);
        Assert.AreEqual(1, skipped, "its own lease is left alone");
        Assert.AreEqual(0, errors.Length);
        Assert.AreEqual("leased", leaseState(io, "data/db.00000001.bin"));
        using (io.OpenRead(["state", "state.00000001.bin"], 0)) { }
    }

    [TestMethod]
    public void DeletingABlobAnotherProcessLeases_IsRefused_UnlessTheLeaseNeverEnds() {
        requireAzurite();
        var io = new AzureBlobIOProvider(newContainerName(), _connectionString, lockBlob: true);
        io.Client.CreateAppendBlobIfNotExists("state/live.bin");
        io.Client.CreateAppendBlobIfNotExists("state/stranded.bin");
        io.Client.AcquireLease("state/live.bin", 60, Guid.NewGuid().ToString());
        io.Client.AcquireLease("state/stranded.bin");
        io.GetFiles();
        Assert.ThrowsExactly<FileLockedException>(() => io.DeleteFileIfItExists(["state", "live.bin"]));
        Assert.IsTrue(io.Exists(["state", "live.bin"]));
        io.DeleteFileIfItExists(["state", "stranded.bin"]);
        Assert.IsFalse(io.Exists(["state", "stranded.bin"]));
    }

    [TestMethod]
    public void ADatabaseOnLeasedBlobs_OpensWritesAndOpensAgain() {
        // the whole of it: the log, the state files and the indexes, all in folders, all leased
        requireAzurite();
        var container = newContainerName();
        var datamodel = new Datamodel();
        datamodel.Add<AzLeaseNote>();
        var settings = new SettingsLocal { AutoDequeTasks = false };
        using (var store = new NodeStore(DataStoreLocal.Open(datamodel, settings,
            new AzureBlobIOProvider(container, _connectionString, lockBlob: true), null))) {
            store.Insert(new AzLeaseNote { Id = Guid.NewGuid(), Text = "kept" });
        }
        var io = new AzureBlobIOProvider(container, _connectionString, lockBlob: true);
        Assert.AreEqual(0, io.GetLeasedFiles().Length, "closing let go of every lease");
        using (var store = new NodeStore(DataStoreLocal.Open(datamodel, settings, io, null))) {
            Assert.AreEqual("kept", store.Query<AzLeaseNote>().Execute().Single().Text);
            Assert.IsTrue(io.GetLeasedFiles().Any(l => l.Key.StartsWith("data/") && l.HeldHere), "the open log is leased");
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
        var mismatch = Assert.ThrowsExactly<AzureBlobRequestException>(() => client.AppendBlock(key, bytes("123"), 3, null, 0));
        Assert.AreEqual("AppendPositionConditionNotMet", mismatch.ErrorCode);
    }
}
