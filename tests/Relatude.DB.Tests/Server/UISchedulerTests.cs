using Microsoft.AspNetCore.Http;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Tasks;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Relatude.Server;

/// <summary>
/// The scheduler page: schedules are settings, so what the page adds, changes and removes lands in
/// relatude.db.overrides.json exactly as the settings page's edits do - and goes when it is all taken
/// back. A list the application's code sets is shown and locked. Runs started from the page go through
/// the task queue.
/// </summary>
[TestClass]
public class UISchedulerTests {
    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-scheduler-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    static string overridesPath(string root) => Path.Combine(root, SettingsOverridesFile.FallbackRelativePath);

    static TestServerHost start(string root, Action<ServerOptions>? options = null) {
        var host = TestServerHost.Start(root, overridesFile: true, options: options);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }

    static async Task<(int Status, JsonElement Json)> send(TestServerHost host, string type, object payload) {
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
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }

    [TestMethod]
    public async Task Schedules_AreSavedAsOverrides_AndGoneWhenTakenBack() {
        var root = newRoot("overrides");
        var host = start(root);
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var page = await command(host, "scheduler", new { storeId });
            Assert.IsTrue(prop(page, "open").GetBoolean());
            Assert.AreEqual(0, prop(page, "schedules").GetArrayLength());
            CollectionAssert.Contains(prop(page, "jobs").EnumerateArray().Select(t => prop(t, "name").GetString()).ToArray(), "save-state");

            var added = await command(host, "scheduler-add", new { storeId, values = new Dictionary<string, object> { ["Job"] = "save-state", ["Unit"] = "Days", ["At"] = "03:00", ["TimeZone"] = "UTC" } });
            Assert.AreEqual(0, prop(added, "rejected").GetArrayLength(), added.ToString());
            var id = prop(added, "id").GetGuid();
            var schedule = host.Server.Containers[storeId].Settings.LocalSettings!.Schedules!.Single();
            Assert.AreEqual(id, schedule.Id);
            Assert.AreEqual("03:00", schedule.At);
            var file = File.ReadAllText(overridesPath(root));
            StringAssert.Contains(file, "\"Schedules\"");
            StringAssert.Contains(file, "$added");

            await command(host, "scheduler-save", new { storeId, id, values = new Dictionary<string, object> { ["At"] = "04:30", ["Days"] = "Mon-Fri" } });
            page = await command(host, "scheduler", new { storeId });
            var view = prop(page, "schedules").EnumerateArray().Single();
            Assert.AreEqual("Every day at 04:30 on Mon-Fri (UTC)", prop(view, "summary").GetString());
            Assert.IsTrue(prop(view, "addedHere").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, prop(view, "problem").ValueKind, view.ToString());
            Assert.AreEqual(JsonValueKind.String, prop(view, "nextUtc").ValueKind);

            await command(host, "scheduler-remove", new { storeId, id });
            Assert.IsNull(host.Server.Containers[storeId].Settings.LocalSettings!.Schedules, "no list, as relatude.db.json has none - not an empty one");
            Assert.IsFalse(File.Exists(overridesPath(root)), "nothing differs from relatude.db.json any more");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AFieldThatIsNotAScheduleField_OrABadValue_IsRefused() {
        var root = newRoot("refused");
        var host = start(root);
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var (status, _) = await send(host, "scheduler-add", new { storeId, values = new Dictionary<string, object> { ["Id"] = Guid.NewGuid().ToString() } });
            Assert.AreEqual(500, status, "the id is the server's to give");
            var added = await command(host, "scheduler-add", new { storeId, values = new Dictionary<string, object> { ["Job"] = "save-state", ["Unit"] = "Fortnights" } });
            CollectionAssert.Contains(prop(added, "rejected").EnumerateArray().Select(r => prop(r, "path").GetString()).ToArray(), "Unit");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ThePreview_SaysWhenItWouldRun_OrWhatIsWrong() {
        var root = newRoot("preview");
        var host = start(root);
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var fine = await command(host, "scheduler-preview", new { storeId, values = new Dictionary<string, object> { ["Job"] = "save-state", ["Unit"] = "Hours", ["Every"] = 6 } });
            Assert.AreEqual(5, prop(fine, "upcoming").GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, prop(fine, "problem").ValueKind);
            var wrong = await command(host, "scheduler-preview", new { storeId, values = new Dictionary<string, object> { ["Job"] = "save-state", ["At"] = "half past two" } });
            StringAssert.Contains(prop(wrong, "problem").GetString(), "time of day");
            Assert.AreEqual(0, prop(wrong, "upcoming").GetArrayLength());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ARunStartedFromThePage_GoesThroughTheQueue() {
        var root = newRoot("run");
        var host = start(root);
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var added = await command(host, "scheduler-add", new { storeId, values = new Dictionary<string, object> { ["Job"] = "save-state", ["Name"] = "Nightly snapshot" } });
            var id = prop(added, "id").GetGuid();
            var started = await command(host, "scheduler-run", new { storeId, scheduleId = id });
            Assert.AreEqual("Queued", prop(prop(started, "run"), "status").GetString());
            // the queue page says which job a run is, by the run's id it was queued under
            var queue = await command(host, "tasks", new { storeId, queues = new[] { "memory" } });
            var batch = prop(queue, "batches").EnumerateArray().Single();
            Assert.AreEqual("save-state · Nightly snapshot", prop(batch, "detail").GetString());
            var store = host.Server.Containers[storeId].Store!.Datastore;
            // the test databases do not work through their queues on their own
            await store.TaskQueue.ExecuteTasksAsync(60_000, () => false);
            var page = await command(host, "scheduler", new { storeId });
            var run = prop(page, "runs").EnumerateArray().First();
            Assert.AreEqual("Succeeded", prop(run, "status").GetString(), run.ToString());
            Assert.AreEqual("Nightly snapshot", prop(run, "scheduleName").GetString());
            Assert.AreEqual("Manual", prop(run, "trigger").GetString());
            Assert.IsFalse(prop(page, "autoDequeue").GetBoolean(), "and the page says so");
        } finally {
            await host.DisposeAsync();
        }
    }

    /// <summary>The Speed slider on the Tasks page saves the database's setting, on THIS SERVER, and the queues follow it.</summary>
    [TestMethod]
    public async Task TheSpeedSlider_SavesTheSetting() {
        var root = newRoot("speed");
        var host = start(root);
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var result = await command(host, "tasks-throttle", new { storeId, throttle = 40 });
            Assert.AreEqual(40, prop(result, "throttle").GetInt32());
            Assert.AreEqual(40, host.Server.Containers[storeId].Settings.LocalSettings!.TaskQueueSpeed);
            StringAssert.Contains(File.ReadAllText(overridesPath(root)), "\"TaskQueueSpeed\": 40");
            var page = await command(host, "tasks", new { storeId });
            Assert.AreEqual(40, prop(page, "throttle").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, prop(page, "throttleLockedBy").ValueKind);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ASpeedTheApplicationsCodeSets_IsNotTheSlidersToChange() {
        var root = newRoot("speed-code");
        var host = start(root, options => options.OnStoreSettingsInit = (local, _) => local.TaskQueueSpeed = 50);
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var page = await command(host, "tasks", new { storeId });
            Assert.AreEqual(50, prop(page, "throttle").GetInt32());
            StringAssert.Contains(prop(page, "throttleLockedBy").GetString(), "the application's code");
            var (status, json) = await send(host, "tasks-throttle", new { storeId, throttle = 10 });
            Assert.AreEqual(500, status);
            StringAssert.Contains(prop(json, "error").GetString(), "the application's code");
            Assert.AreEqual(50, host.Server.Containers[storeId].Store!.Datastore.TaskQueueThrottle);
            Assert.IsFalse(File.Exists(overridesPath(root)));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SchedulesTheApplicationsCodeSets_AreShownAndLocked() {
        var root = newRoot("code");
        var host = start(root, options => options.OnStoreSettingsInit = (local, _) => {
            local.Schedules = [new ScheduleSettings { Id = Guid.NewGuid(), Job = "save-state", Unit = ScheduleUnit.Hours }];
        });
        try {
            var storeId = host.Server.Settings.ContainerSettings![0].Id;
            var page = await command(host, "scheduler", new { storeId });
            Assert.AreEqual("code", prop(page, "locked").GetString());
            Assert.AreEqual(1, prop(page, "schedules").GetArrayLength());
            var (status, json) = await send(host, "scheduler-add", new { storeId, values = new Dictionary<string, object> { ["Job"] = "backup" } });
            Assert.AreEqual(500, status);
            StringAssert.Contains(prop(json, "error").GetString(), "the application's code");
            Assert.IsFalse(File.Exists(overridesPath(root)));
        } finally {
            await host.DisposeAsync();
        }
    }
}
