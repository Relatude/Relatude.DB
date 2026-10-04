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
/// The admin UI's task board (UISharedTasks): what a tab reports is what every session sees, a job a
/// command attached goes on being reported after the tab that started it is gone, a Cancel reaches
/// the tab or the job that can act on it - and, in the Files view, the data folder warning only
/// where a database actually keeps its data.
/// </summary>
[TestClass]
public class UISharedTasksTests {

    sealed record Setup(TestServerHost Host, string Root, Guid MemoryIo, Guid DiskIo) {
        public IIOProvider Disk => Host.Server.GetIO(DiskIo);
        public string DiskFolder => Path.Combine(Root, "disk");
    }

    // a database on memory, with a second storage on disk it keeps nothing of its own in
    static Setup start(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-shared-" + name + "-" + Guid.NewGuid().ToString("N"));
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
    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var (status, json) = await send(host, type, payload);
        Assert.AreEqual(200, status, type + " failed: " + json);
        return json;
    }
    static async Task<JsonElement?> listed(TestServerHost host, Guid id) {
        var list = await command(host, "shared-tasks", new { });
        foreach (var task in list.EnumerateArray()) {
            if (task.GetProperty("id").GetGuid() == id) return task;
        }
        return null;
    }
    // the board until the task stops running, as a tab following it sees it
    static async Task<JsonElement> waitUntilFinished(TestServerHost host, Guid id) {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true) {
            var task = await listed(host, id);
            Assert.IsNotNull(task, "the task is not on the board");
            if (task.Value.GetProperty("status").GetString() != "running") return task.Value;
            Assert.IsTrue(DateTime.UtcNow < deadline, "the task did not finish");
            await Task.Delay(50);
        }
    }

    [TestMethod]
    public async Task WhatATabReports_IsOnTheBoard_AndATabThatLeftIsSaidToHaveStopped() {
        var s = start("report");
        try {
            var id = Guid.NewGuid();
            var answer = await command(s.Host, "shared-task-report", new {
                Id = id, Key = "upload:x", Title = "Upload 3 files", Status = "running", Label = "photo.jpg", Done = 10, Total = 100, Meta = "1 of 3",
            });
            Assert.IsFalse(answer.GetProperty("cancelRequested").GetBoolean());
            var task = (await listed(s.Host, id))!.Value;
            Assert.AreEqual("Upload 3 files", task.GetProperty("title").GetString());
            Assert.AreEqual("upload:x", task.GetProperty("key").GetString());
            Assert.AreEqual("running", task.GetProperty("status").GetString());
            Assert.AreEqual(10, task.GetProperty("done").GetDouble());
            Assert.IsTrue(task.GetProperty("cancellable").GetBoolean(), "a tab that is there can be asked to stop");

            // the tab goes away while it runs: nobody can finish it, and the board says so at once
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Upload 3 files", Status = "running", Label = "photo.jpg", Done = 20, Left = true });
            task = (await listed(s.Host, id))!.Value;
            Assert.AreEqual("error", task.GetProperty("status").GetString());
            StringAssert.Contains(task.GetProperty("message").GetString(), "closed or reloaded");
            Assert.AreNotEqual(JsonValueKind.Null, task.GetProperty("finishedUtc").ValueKind);
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task ACancelFromAnotherSession_ReachesTheTabOnItsNextReport() {
        var s = start("cancel");
        try {
            var id = Guid.NewGuid();
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Download", Status = "running", Label = "", Done = 0 });
            await command(s.Host, "shared-task-cancel", new { Id = id });
            var answer = await command(s.Host, "shared-task-report", new { Id = id, Title = "Download", Status = "running", Label = "", Done = 5 });
            Assert.IsTrue(answer.GetProperty("cancelRequested").GetBoolean());
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Download", Status = "cancelled", Label = "", Done = 5, Message = "Cancelled." });
            var task = (await listed(s.Host, id))!.Value;
            Assert.AreEqual("cancelled", task.GetProperty("status").GetString());
            Assert.IsFalse(task.GetProperty("cancellable").GetBoolean());
            var (status, _) = await send(s.Host, "shared-task-cancel", new { Id = id });
            Assert.AreNotEqual(200, status, "a finished task cannot be cancelled");
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task AJobACommandAttached_IsWhatTheTaskSays_AfterItsTabHasGone() {
        var s = start("job");
        try {
            for (var i = 0; i < 20; i++) File.WriteAllBytes(Path.Combine(s.DiskFolder, $"f{i:00}.bin"), new byte[200_000]);
            var id = Guid.NewGuid();
            // the tab reports first, then starts the move with the task's id, and is gone before it ends
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Copy 20 files", Status = "running", Label = "", Done = 0 });
            await command(s.Host, "io-move-start", new {
                FromIoId = s.DiskIo, ToIoId = s.MemoryIo, BasePath = "", Files = Enumerable.Range(0, 20).Select(i => $"f{i:00}.bin").ToArray(),
                TargetPath = "copies", KeepOriginals = true, TaskId = id,
            });
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Copy 20 files", Status = "running", Label = "", Done = 0, Left = true });
            var task = await waitUntilFinished(s.Host, id);
            Assert.AreEqual("done", task.GetProperty("status").GetString(), task.ToString());
            StringAssert.Contains(task.GetProperty("message").GetString(), "Copied 20 of 20 files");
            Assert.AreEqual("Copy 20 files", task.GetProperty("title").GetString(), "the title the dialog had");
            // the job alone, as the tab that started a truncation waits for it
            var job = await command(s.Host, "shared-task-job", new { Id = id });
            Assert.AreEqual("done", job.GetProperty("status").GetString());
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task ATabThatWentQuietBeforeTheJobEnded_DoesNotOutrankIt() {
        var s = start("quiet");
        try {
            File.WriteAllBytes(Path.Combine(s.DiskFolder, "one.bin"), new byte[1000]);
            var id = Guid.NewGuid();
            // the tab says "running" and is reloaded without a last word: its report is stale from then on
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Copy", Status = "running", Label = "half way", Done = 0, Seq = 1 });
            await command(s.Host, "io-move-start", new {
                FromIoId = s.DiskIo, ToIoId = s.MemoryIo, BasePath = "", Files = new[] { "one.bin" }, TargetPath = "", KeepOriginals = true, TaskId = id,
            });
            var task = await waitUntilFinished(s.Host, id);
            Assert.AreEqual("done", task.GetProperty("status").GetString(), task.ToString());
            // an older report overtaken by a newer one changes nothing
            await command(s.Host, "shared-task-report", new { Id = id, Title = "Copy", Status = "running", Label = "late", Done = 0, Seq = 1 });
            task = (await listed(s.Host, id))!.Value;
            Assert.AreEqual("done", task.GetProperty("status").GetString(), task.ToString());
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task ABackupQueuedOnTheServer_IsFollowedThroughTheQueue() {
        var s = start("backup");
        try {
            var storeId = s.Host.Server.Containers.Keys.First();
            var id = Guid.NewGuid();
            await command(s.Host, "backup-now", new { StoreId = storeId, Truncate = false, KeepForever = false, TaskId = id });
            var waiting = (await listed(s.Host, id))!.Value;
            Assert.AreEqual("running", waiting.GetProperty("status").GetString(), waiting.ToString());
            Assert.IsFalse(waiting.GetProperty("cancellable").GetBoolean(), "nothing stops a rewrite once it is queued");
            // the test host runs no scheduler: the queue is run here, the way the scheduler would
            var queue = s.Host.Server.Containers[storeId].Store!.Datastore.TaskQueue;
            await queue.ExecuteTasksAsync(30_000, () => false, 0);
            var task = await waitUntilFinished(s.Host, id);
            Assert.AreEqual("done", task.GetProperty("status").GetString(), task.ToString());
            Assert.AreEqual("Backup written.", task.GetProperty("message").GetString());
            var backups = await command(s.Host, "backup-list", new { StoreId = storeId });
            Assert.AreEqual(1, backups.GetProperty("files").GetArrayLength());
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task WorkDoneInTheCommand_IsATaskUntilItReturns() {
        var s = start("state");
        try {
            var storeId = s.Host.Server.Containers.Keys.First();
            var id = Guid.NewGuid();
            await command(s.Host, "db-save-state", new { StoreId = storeId, TaskId = id });
            var task = (await listed(s.Host, id))!.Value;
            Assert.AreEqual("done", task.GetProperty("status").GetString(), task.ToString());
            Assert.AreEqual("State snapshot updated.", task.GetProperty("message").GetString());
            Assert.AreEqual(storeId, task.GetProperty("storeId").GetGuid());
        } finally {
            await stop(s);
        }
    }

    [TestMethod]
    public async Task TheDataFolderIsPrimaryData_OnlyOnTheStorageTheDatabaseKeepsItOn() {
        var s = start("primary");
        try {
            var storeId = s.Host.Server.Containers.Keys.First();
            // the same folder names on a storage the database keeps nothing in: a copy, or left behind
            Directory.CreateDirectory(Path.Combine(s.DiskFolder, "data"));
            File.WriteAllText(Path.Combine(s.DiskFolder, "data", "db.00000001.bin"), "not the database");
            Directory.CreateDirectory(Path.Combine(s.DiskFolder, "files"));
            var disk = await command(s.Host, "io-folder", new { IoId = s.DiskIo, Path = "", Recursive = false });
            foreach (var sub in disk.GetProperty("subFolders").EnumerateArray()) {
                Assert.IsFalse(sub.GetProperty("isPrimaryData").GetBoolean(), sub.GetProperty("name").GetString() + " is not the database's data");
            }
            var dataOnDisk = await command(s.Host, "io-folder", new { IoId = s.DiskIo, Path = "data", Recursive = false });
            Assert.IsFalse(dataOnDisk.GetProperty("isPrimaryData").GetBoolean());

            // the database's own storage: its log is in data/, and the implicit file store in files/
            var memory = await command(s.Host, "io-folder", new { IoId = s.MemoryIo, Path = "data", Recursive = false });
            Assert.IsTrue(memory.GetProperty("isPrimaryData").GetBoolean());

            // and the Files view opens on that storage: io-list says which one it is
            var list = await command(s.Host, "io-list", new { StoreId = storeId });
            var roles = list.EnumerateArray().ToDictionary(io => io.GetProperty("id").GetGuid(), io => io.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
            CollectionAssert.Contains(roles[s.MemoryIo], "database");
            CollectionAssert.Contains(roles[s.MemoryIo], "files");
            Assert.AreEqual(0, roles[s.DiskIo].Length);
        } finally {
            await stop(s);
        }
    }
}
