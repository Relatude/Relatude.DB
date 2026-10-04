using Microsoft.AspNetCore.Http;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Settings;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Relatude.Server;

/// <summary>
/// Moving files and folders between storages from the Files view (UIFileMove, the io-move-* commands):
/// every file arrives whole and leaves only once it has, a folder goes once it is empty, a file the
/// target has is skipped or replaced as asked, a copy leaves the originals, and the same file seen
/// through two storages - the project folder holds the database's own - is never copied onto itself.
/// </summary>
[TestClass]
public class UIFileMoveTests {

    sealed record Setup(TestServerHost Host, string Root, Guid MemoryIo, Guid DiskIo) {
        public IIOProvider Memory => Host.Server.GetIO(MemoryIo);
        public IIOProvider Disk => Host.Server.GetIO(DiskIo);
        public string DiskFolder => Path.Combine(Root, "disk");
    }

    // a database on memory, with a second storage on disk below the project folder, beside the
    // project folder's own storage every server has
    static Setup start(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-move-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "disk"));
        var diskIo = new IOSettings { Id = Guid.NewGuid(), Name = "Disk", IOType = IOTypes.LocalDisk, Path = "disk" };
        Guid memoryIo = Guid.Empty;
        var host = TestServerHost.Start(root, configure: settings => {
            var container = settings.ContainerSettings![0];
            memoryIo = container.IOSettings![0].Id;
            container.IOSettings = [.. container.IOSettings, diskIo];
        });
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return new Setup(host, root, memoryIo, diskIo.Id);
    }

    static async Task stop(Setup setup) {
        await setup.Host.DisposeAsync();
        try { Directory.Delete(setup.Root, true); } catch { }
    }

    static async Task<(int Status, JsonElement Body)> send(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }

    static async Task<Guid> startMove(TestServerHost host, object request) {
        var (status, json) = await send(host, "io-move-start", request);
        Assert.AreEqual(200, status, "io-move-start failed: " + json);
        return json.GetProperty("jobId").GetGuid();
    }

    // polls the job the way the dialog does until it has finished, keeping every sample it handed out
    static async Task<(JsonElement Final, List<long[]> Samples)> waitFor(TestServerHost host, Guid jobId) {
        var samples = new List<long[]>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true) {
            var (status, json) = await send(host, "io-move-progress", new { JobId = jobId, SamplesFrom = samples.Count });
            Assert.AreEqual(200, status, "io-move-progress failed: " + json);
            foreach (var sample in json.GetProperty("samples").EnumerateArray()) samples.Add([sample[0].GetInt64(), sample[1].GetInt64(), sample[2].GetInt64()]);
            var state = json.GetProperty("state").GetString();
            if (state is "done" or "cancelled" or "failed") return (json, samples);
            Assert.IsTrue(DateTime.UtcNow < deadline, "the move did not finish");
            await Task.Delay(20);
        }
    }

    static byte[] randomBytes(int size, int seed) {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static string[] key(string path) => path.SplitKey();

    [TestMethod]
    public async Task MovesFilesAndFolders_EveryByte_AndRemovesTheFoldersItEmptied() {
        var s = start("folders");
        try {
            // from the project folder (disk) into the database's memory storage
            var big = randomBytes(3_500_000, 1); // several copy buffers
            var small = randomBytes(1234, 2);
            var loose = randomBytes(10, 3);
            Directory.CreateDirectory(Path.Combine(s.Root, "src", "sub"));
            Directory.CreateDirectory(Path.Combine(s.Root, "src", "empty"));
            File.WriteAllBytes(Path.Combine(s.Root, "src", "sub", "big.bin"), big);
            File.WriteAllBytes(Path.Combine(s.Root, "src", "small.txt"), small);
            Directory.CreateDirectory(Path.Combine(s.Root, "open"));
            File.WriteAllBytes(Path.Combine(s.Root, "open", "loose.bin"), loose);

            var job = await startMove(s.Host, new {
                FromIoId = RelatudeDBServer.ProjectRootIOId, ToIoId = s.MemoryIo, BasePath = "open",
                Files = new[] { "open/loose.bin" }, Folders = new[] { "src" }, TargetPath = "into",
            });
            var (final, samples) = await waitFor(s.Host, job);

            Assert.AreEqual("done", final.GetProperty("state").GetString(), final.ToString());
            Assert.AreEqual(3, final.GetProperty("filesTotal").GetInt32());
            Assert.AreEqual(3, final.GetProperty("filesMoved").GetInt32());
            Assert.AreEqual(0, final.GetProperty("filesFailed").GetInt32(), final.ToString());
            Assert.AreEqual(1, final.GetProperty("foldersRemoved").GetInt32());
            var total = big.Length + small.Length + loose.Length;
            Assert.AreEqual(total, final.GetProperty("bytesTotal").GetInt64());
            Assert.AreEqual(total, final.GetProperty("bytesMoved").GetInt64());
            Assert.AreEqual(total, final.GetProperty("bytesTransferred").GetInt64());
            // a folder arrives under its own name, a file keeps its path below the open folder
            CollectionAssert.AreEqual(big, s.Memory.ReadAllBytes(key("into/src/sub/big.bin")));
            CollectionAssert.AreEqual(small, s.Memory.ReadAllBytes(key("into/src/small.txt")));
            CollectionAssert.AreEqual(loose, s.Memory.ReadAllBytes(key("into/loose.bin")));
            Assert.IsFalse(Directory.Exists(Path.Combine(s.Root, "src")), "the emptied folder should be gone, empty subfolders and all");
            Assert.IsFalse(File.Exists(Path.Combine(s.Root, "open", "loose.bin")));
            Assert.IsTrue(Directory.Exists(Path.Combine(s.Root, "open")), "the folder a selected file came from stays");
            // nothing staged is left behind in the target
            Assert.IsFalse(s.Memory.GetFiles().Any(f => f.Key.Contains(".part")), "a staged copy was left in the target");
            // the samples the speed graph is drawn from: from zero, never backwards, ending at the total
            Assert.IsTrue(samples.Count >= 2);
            CollectionAssert.AreEqual(new long[] { 0, 0, 0 }, samples[0]);
            for (var i = 1; i < samples.Count; i++) {
                Assert.IsTrue(samples[i][0] >= samples[i - 1][0] && samples[i][1] >= samples[i - 1][1] && samples[i][2] >= samples[i - 1][2], "samples must not go backwards");
            }
            Assert.AreEqual(total, samples[^1][1]);
            Assert.AreEqual(0, samples[^1][2], "nothing was passed over");
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task AFileTheTargetHas_IsSkippedOrReplacedAsAsked_AndACopyKeepsTheOriginals() {
        var s = start("existing");
        try {
            s.Memory.WriteAllBytes(key("a.txt"), Encoding.UTF8.GetBytes("new a"));
            s.Memory.WriteAllBytes(key("b.txt"), Encoding.UTF8.GetBytes("new b"));
            s.Memory.WriteAllBytes(key("c.txt"), Encoding.UTF8.GetBytes("copy c"));
            File.WriteAllText(Path.Combine(s.DiskFolder, "a.txt"), "old a");
            File.WriteAllText(Path.Combine(s.DiskFolder, "b.txt"), "old b");

            // skip: the target keeps its file, and this one stays where it was
            var (skip, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.MemoryIo, ToIoId = s.DiskIo, BasePath = "", Files = new[] { "a.txt" }, TargetPath = "",
            }));
            Assert.AreEqual("done", skip.GetProperty("state").GetString());
            Assert.AreEqual(1, skip.GetProperty("filesSkipped").GetInt32());
            Assert.AreEqual(0, skip.GetProperty("filesMoved").GetInt32());
            CollectionAssert.AreEqual(new[] { "a.txt" }, skip.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.AreEqual("old a", File.ReadAllText(Path.Combine(s.DiskFolder, "a.txt")));
            Assert.AreEqual("new a", s.Memory.ReadAllTextUTF8(key("a.txt")));

            // replace: the target's file goes, this one takes its place and leaves here
            var (replace, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.MemoryIo, ToIoId = s.DiskIo, BasePath = "", Files = new[] { "b.txt" }, TargetPath = "", Overwrite = true,
            }));
            Assert.AreEqual(1, replace.GetProperty("filesMoved").GetInt32(), replace.ToString());
            Assert.AreEqual("new b", File.ReadAllText(Path.Combine(s.DiskFolder, "b.txt")));
            Assert.IsFalse(s.Memory.Exists(key("b.txt")));

            // a copy: both have it afterwards
            var (copy, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.MemoryIo, ToIoId = s.DiskIo, BasePath = "", Files = new[] { "c.txt" }, TargetPath = "copies", KeepOriginals = true,
            }));
            Assert.AreEqual(1, copy.GetProperty("filesMoved").GetInt32(), copy.ToString());
            Assert.AreEqual("copy c", File.ReadAllText(Path.Combine(s.DiskFolder, "copies", "c.txt")));
            Assert.AreEqual("copy c", s.Memory.ReadAllTextUTF8(key("c.txt")));
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task TheSameFileSeenThroughTwoStorages_IsNeverCopiedOntoItself() {
        var s = start("same");
        try {
            // the disk storage is the project folder's "disk" folder: two names for one file
            var bytes = randomBytes(5000, 4);
            File.WriteAllBytes(Path.Combine(s.DiskFolder, "x.bin"), bytes);

            // the file onto itself: refused for that file, and the file is untouched
            var (final, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = RelatudeDBServer.ProjectRootIOId, ToIoId = s.DiskIo, BasePath = "disk", Files = new[] { "disk/x.bin" }, TargetPath = "", Overwrite = true,
            }));
            Assert.AreEqual("done", final.GetProperty("state").GetString());
            Assert.AreEqual(1, final.GetProperty("filesFailed").GetInt32(), final.ToString());
            StringAssert.Contains(final.GetProperty("errors")[0].GetString(), "same file");
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(s.DiskFolder, "x.bin")));

            // a folder into itself: refused before anything starts
            var (status, json) = await send(s.Host, "io-move-start", new {
                FromIoId = RelatudeDBServer.ProjectRootIOId, ToIoId = s.DiskIo, BasePath = "", Folders = new[] { "disk" }, TargetPath = "inner",
            });
            Assert.AreEqual(500, status);
            StringAssert.Contains(json.GetProperty("error").GetString(), "inside");
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(s.DiskFolder, "x.bin")));
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task WithinOneStorage_MovesToAnotherFolder_AndCopiesBesideTheOriginalsGetNamesOfTheirOwn() {
        var s = start("within");
        try {
            File.WriteAllText(Path.Combine(s.DiskFolder, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(s.DiskFolder, "docs"));
            File.WriteAllText(Path.Combine(s.DiskFolder, "docs", "x.txt"), "x");

            // a move to another folder of the same disk storage: renamed across
            var (moved, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.DiskIo, ToIoId = s.DiskIo, BasePath = "", Files = new[] { "a.txt" }, TargetPath = "sub",
            }));
            Assert.AreEqual(1, moved.GetProperty("filesMoved").GetInt32(), moved.ToString());
            Assert.AreEqual("a", File.ReadAllText(Path.Combine(s.DiskFolder, "sub", "a.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(s.DiskFolder, "a.txt")));

            // a copy into the folder it is in: a name of its own, and another one the next time
            for (var i = 0; i < 2; i++) {
                var (copied, _) = await waitFor(s.Host, await startMove(s.Host, new {
                    FromIoId = s.DiskIo, ToIoId = s.DiskIo, BasePath = "sub", Files = new[] { "sub/a.txt" }, TargetPath = "sub", KeepOriginals = true,
                }));
                Assert.AreEqual(1, copied.GetProperty("filesMoved").GetInt32(), copied.ToString());
            }
            Assert.AreEqual("a", File.ReadAllText(Path.Combine(s.DiskFolder, "sub", "a - Copy.txt")));
            Assert.AreEqual("a", File.ReadAllText(Path.Combine(s.DiskFolder, "sub", "a - Copy (2).txt")));
            Assert.AreEqual("a", File.ReadAllText(Path.Combine(s.DiskFolder, "sub", "a.txt")), "the original stays");

            // a folder copied into its own parent
            var (folderCopy, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.DiskIo, ToIoId = s.DiskIo, BasePath = "", Folders = new[] { "docs" }, TargetPath = "", KeepOriginals = true,
            }));
            Assert.AreEqual("done", folderCopy.GetProperty("state").GetString(), folderCopy.ToString());
            Assert.AreEqual("x", File.ReadAllText(Path.Combine(s.DiskFolder, "docs - Copy", "x.txt")));
            Assert.AreEqual("x", File.ReadAllText(Path.Combine(s.DiskFolder, "docs", "x.txt")));

            // a folder into itself, and a move that would leave everything where it is: refused at the start
            var (status, json) = await send(s.Host, "io-move-start", new {
                FromIoId = s.DiskIo, ToIoId = s.DiskIo, BasePath = "", Folders = new[] { "docs" }, TargetPath = "docs/deeper", KeepOriginals = true,
            });
            Assert.AreEqual(500, status);
            StringAssert.Contains(json.GetProperty("error").GetString(), "inside");
            (status, json) = await send(s.Host, "io-move-start", new {
                FromIoId = s.DiskIo, ToIoId = s.DiskIo, BasePath = "sub", Files = new[] { "sub/a.txt" }, TargetPath = "sub",
            });
            Assert.AreEqual(500, status);
            StringAssert.Contains(json.GetProperty("error").GetString(), "already in that folder");

            // within a storage with no folders on disk (memory): renamed by key
            s.Memory.WriteAllBytes(key("m/one.bin"), [1, 2, 3]);
            var (inMemory, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.MemoryIo, ToIoId = s.MemoryIo, BasePath = "m", Folders = new[] { "m" }, TargetPath = "n",
            }));
            Assert.AreEqual(1, inMemory.GetProperty("filesMoved").GetInt32(), inMemory.ToString());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, s.Memory.ReadAllBytes(key("n/m/one.bin")));
            Assert.IsFalse(s.Memory.Exists(key("m/one.bin")));
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task AFileBeingWritten_StaysWhereItIs() {
        var s = start("held");
        try {
            File.WriteAllText(Path.Combine(s.DiskFolder, "held.txt"), "held");
            File.WriteAllText(Path.Combine(s.DiskFolder, "free.txt"), "free");
            // a writer the storage itself knows about, the way the database holds its log open
            using var writer = s.Disk.OpenAppend(key("held.txt"));
            var (final, _) = await waitFor(s.Host, await startMove(s.Host, new {
                FromIoId = s.DiskIo, ToIoId = s.MemoryIo, BasePath = "", Files = new[] { "held.txt", "free.txt" }, TargetPath = "",
            }));
            Assert.AreEqual(1, final.GetProperty("filesMoved").GetInt32(), final.ToString());
            Assert.AreEqual(1, final.GetProperty("filesFailed").GetInt32());
            StringAssert.Contains(final.GetProperty("errors")[0].GetString(), "held.txt");
            Assert.IsTrue(File.Exists(Path.Combine(s.DiskFolder, "held.txt")));
            Assert.IsFalse(s.Memory.Exists(key("held.txt")));
            Assert.AreEqual("free", s.Memory.ReadAllTextUTF8(key("free.txt")));
            // the bar ends at the end: what was moved and what was passed over add up to the whole
            Assert.AreEqual(final.GetProperty("bytesTotal").GetInt64(),
                final.GetProperty("bytesTransferred").GetInt64() + final.GetProperty("bytesPassed").GetInt64());
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task Cancel_RollsBackTheFileInFlight_AndKeepsWhatWasMoved() {
        var s = start("cancel");
        try {
            // enough files, and big enough, that the cancel lands while a copy is going
            for (var i = 0; i < 30; i++) File.WriteAllBytes(Path.Combine(s.DiskFolder, $"f{i:00}.bin"), randomBytes(6_000_000, i));
            var job = await startMove(s.Host, new {
                FromIoId = s.DiskIo, ToIoId = s.MemoryIo, BasePath = "", Files = Enumerable.Range(0, 30).Select(i => $"f{i:00}.bin").ToArray(), TargetPath = "",
            });
            var cancelled = false;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!cancelled && DateTime.UtcNow < deadline) {
                var (_, json) = await send(s.Host, "io-move-progress", new { JobId = job });
                if (json.GetProperty("state").GetString() != "moving") {
                    if (json.GetProperty("state").GetString() == "done") Assert.Inconclusive("the move finished before it could be cancelled");
                } else if (json.GetProperty("bytesTransferred").GetInt64() > 0) {
                    await send(s.Host, "io-move-cancel", new { JobId = job });
                    cancelled = true;
                }
                await Task.Delay(1);
            }
            var (final, _) = await waitFor(s.Host, job);
            if (final.GetProperty("state").GetString() == "done") Assert.Inconclusive("the move finished before the cancel reached it");
            Assert.AreEqual("cancelled", final.GetProperty("state").GetString(), final.ToString());
            // every file is in exactly one place, and whole wherever it is
            var moved = 0;
            for (var i = 0; i < 30; i++) {
                var name = $"f{i:00}.bin";
                var onDisk = File.Exists(Path.Combine(s.DiskFolder, name));
                var inMemory = s.Memory.Exists(key(name));
                Assert.IsTrue(onDisk ^ inMemory, name + " should be in exactly one of the two storages");
                var bytes = onDisk ? File.ReadAllBytes(Path.Combine(s.DiskFolder, name)) : s.Memory.ReadAllBytes(key(name));
                CollectionAssert.AreEqual(randomBytes(6_000_000, i), bytes, name + " is not whole");
                if (inMemory) moved++;
            }
            Assert.AreEqual(final.GetProperty("filesMoved").GetInt32(), moved);
            Assert.IsTrue(moved < 30);
            Assert.IsFalse(s.Memory.GetFiles().Any(f => f.Key.Contains(".part")), "a staged copy was left in the target");
        } finally {
            await stop(s);
        }
    }
}
