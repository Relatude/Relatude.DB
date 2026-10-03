using Microsoft.AspNetCore.Http;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Settings;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.Server;

/// <summary>
/// relatude.db.overrides.json: the admin UI saves the difference from relatude.db.json there, never
/// relatude.db.json itself; the file is merged back at every start; values from the configuration
/// section and from the application's code never reach either file; and entries can be moved into
/// relatude.db.json, or discarded, from the settings page.
/// </summary>
[TestClass]
public class SettingsOverridesFileTests {

    // ---- the patch: what a difference looks like, and that merging it gives the target back ----

    static JsonObject toJson(RelatudeDBServerSettings settings) => SettingsOverridesFile.ToJson(settings);

    static (RelatudeDBServerSettings Base, RelatudeDBServerSettings Changed) changedSettings() {
        var original = RelatudeDBServerSettings.CreateDefault();
        original.ContainerSettings![0].LocalSettings!.LogRecording = [new LogRecordingSettings { Key = "queries", Log = true }];
        var changed = TestServerHost.Copy(original);
        var container = changed.ContainerSettings![0];
        changed.Name = "Changed";
        container.LocalSettings!.NodeCacheSizeGb = 3.5;
        container.IOSettings![0].Name = "Renamed disk";
        container.IOSettings = [.. container.IOSettings, new IOSettings { Id = Guid.NewGuid(), Name = "Second disk", IOType = IOTypes.LocalDisk, Path = "second" }];
        container.DatamodelSources = [];
        container.AISettings = new() { TypeName = "Mock", ApiKey = "key" };
        container.LocalSettings.LogRecording = [new LogRecordingSettings { Key = "queries", Log = false, Statistics = true }];
        return (original, changed);
    }

    [TestMethod]
    public void Diff_OfEqualSettings_IsEmpty() {
        var settings = RelatudeDBServerSettings.CreateDefault();
        Assert.AreEqual(0, SettingsPatch.Diff(toJson(settings), toJson(TestServerHost.Copy(settings))).Count);
    }

    [TestMethod]
    public void Diff_ThenMerge_GivesTheChangedSettingsBack() {
        var (original, changed) = changedSettings();
        var patch = SettingsPatch.Diff(toJson(original), toJson(changed));
        var merged = toJson(original);
        SettingsPatch.Merge(merged, patch);
        Assert.IsTrue(JsonNode.DeepEquals(toJson(changed), merged), merged.ToJsonString());
    }

    [TestMethod]
    public void Diff_WritesOnlyWhatChanged_ElementsByIdWithMarkers() {
        var (original, changed) = changedSettings();
        var patch = SettingsPatch.Diff(toJson(original), toJson(changed));
        Assert.AreEqual("Changed", patch["Name"]!.GetValue<string>());
        Assert.IsFalse(patch.ContainsKey("MasterUserName"), "an unchanged setting is not written");
        var container = (JsonObject)patch["ContainerSettings"]![0]!;
        Assert.AreEqual(original.ContainerSettings![0].Id.ToString(), container["Id"]!.GetValue<string>());
        Assert.IsFalse(container.ContainsKey("Name"));
        var io = (JsonArray)container["IOSettings"]!;
        Assert.AreEqual(2, io.Count);
        Assert.AreEqual("Renamed disk", io[0]!["Name"]!.GetValue<string>());
        Assert.IsFalse(((JsonObject)io[0]!).ContainsKey("Path"), "only the field that changed");
        Assert.IsTrue(io[1]![SettingsPatch.AddedMarker]!.GetValue<bool>());
        var sources = (JsonArray)container["DatamodelSources"]!;
        Assert.AreEqual(1, sources.Count, "a removed element is written as removed, even when the list is left empty");
        Assert.IsTrue(sources[0]![SettingsPatch.RemovedMarker]!.GetValue<bool>());
        Assert.IsNotNull(container["AISettings"]!["ApiKey"], "an object the file did not have is written whole");
        Assert.AreEqual(1, ((JsonArray)container["LocalSettings"]!["LogRecording"]!).Count, "a list without ids is written whole");
    }

    [TestMethod]
    public void Diff_OfADictionaryThatLostAKey_ReplacesItWhole() {
        var original = RelatudeDBServerSettings.CreateDefault();
        original.ContainerSettings![0].AISettings = new() { TypeName = "Mock", CompletionModelsByKey = new() { ["fast"] = "a", ["slow"] = "b" } };
        var changed = TestServerHost.Copy(original);
        changed.ContainerSettings![0].AISettings!.CompletionModelsByKey!.Remove("slow");
        var patch = SettingsPatch.Diff(toJson(original), toJson(changed));
        var merged = toJson(original);
        SettingsPatch.Merge(merged, patch);
        Assert.IsTrue(JsonNode.DeepEquals(toJson(changed), merged), merged.ToJsonString());
    }

    [TestMethod]
    public void Entries_NameEachSettingAndElement() {
        var (original, changed) = changedSettings();
        var baseJson = toJson(original);
        var entries = SettingsPatch.Entries(SettingsPatch.Diff(baseJson, toJson(changed)), baseJson);
        var c = "ContainerSettings[" + original.ContainerSettings![0].Id + "]";
        var added = changed.ContainerSettings![0].IOSettings![1].Id;
        CollectionAssert.IsSubsetOf(new[] {
            "Name",
            c + ".LocalSettings.NodeCacheSizeGb",
            c + ".LocalSettings.LogRecording",
            c + ".IOSettings[" + original.ContainerSettings[0].IOSettings![0].Id + "].Name",
            c + ".IOSettings[" + added + "]",
            c + ".DatamodelSources[" + original.ContainerSettings[0].DatamodelSources![0].Id + "]",
            c + ".AISettings",
        }, entries.Select(e => e.Path).ToArray());
        Assert.AreEqual(SettingsPatch.EntryKind.Added, entries.Single(e => e.Path.EndsWith("[" + added + "]")).Kind);
    }

    [TestMethod]
    public void Merge_SkipsAChangeToAnElementThatIsGone() {
        var settings = RelatudeDBServerSettings.CreateDefault();
        var gone = Guid.NewGuid();
        var patch = JsonNode.Parse("{\"ContainerSettings\":[{\"Id\":\"" + gone + "\",\"Name\":\"Ghost\"}]}")!.AsObject();
        var warnings = new List<string>();
        var merged = toJson(settings);
        SettingsPatch.Merge(merged, patch, warnings.Add);
        Assert.AreEqual(1, ((JsonArray)merged["ContainerSettings"]!).Count, "a half element is never added");
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], gone.ToString());
    }

    [TestMethod]
    public void Merge_AnAddedElementTheFileNowHas_ChangesThatElement() {
        var settings = RelatudeDBServerSettings.CreateDefault();
        var id = settings.ContainerSettings![0].Id;
        var patch = JsonNode.Parse("{\"ContainerSettings\":[{\"Id\":\"" + id + "\",\"$added\":true,\"Name\":\"Moved\"}]}")!.AsObject();
        var merged = toJson(settings);
        SettingsPatch.Merge(merged, patch);
        var containers = (JsonArray)merged["ContainerSettings"]!;
        Assert.AreEqual(1, containers.Count);
        Assert.AreEqual("Moved", containers[0]!["Name"]!.GetValue<string>());
        Assert.IsFalse(((JsonObject)containers[0]!).ContainsKey(SettingsPatch.AddedMarker));
    }

    [TestMethod]
    public void Select_KeepsOnlyTheChosenEntries() {
        var (original, changed) = changedSettings();
        var baseJson = toJson(original);
        var patch = SettingsPatch.Diff(baseJson, toJson(changed));
        var cache = "ContainerSettings[" + original.ContainerSettings![0].Id + "].LocalSettings.NodeCacheSizeGb";
        var selected = SettingsPatch.Select(patch, baseJson, e => e.Path == "Name" || e.Path == cache);
        var entries = SettingsPatch.Entries(selected, baseJson);
        CollectionAssert.AreEquivalent(new[] { "Name", cache }, entries.Select(e => e.Path).ToArray());
    }

    // ---- the file ----

    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-overrides-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    static string overridesPath(string root) => Path.Combine(root, SettingsOverridesFile.FallbackRelativePath);
    static void writeOverrides(string root, string json) {
        var path = overridesPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [TestMethod]
    public void Open_MatchesKeysWithoutCase_AndSkipsWhatItCannotUse() {
        var root = newRoot("open");
        var settings = RelatudeDBServerSettings.CreateDefault();
        var id = settings.ContainerSettings![0].Id;
        writeOverrides(root, """
            // written by hand
            {
              "name": "From the file",
              "NoSuchSetting": 1,
              "ContainerSettings": [ { "Id": "<id>", "localsettings": { "NodeCacheSizeGb": "not a number", "AutoBackUp": <backup> } } ],
            }
            """.Replace("<id>", id.ToString()).Replace("<backup>", settings.ContainerSettings[0].LocalSettings!.AutoBackUp ? "false" : "true"));
        var warnings = new List<string>();
        var file = SettingsOverridesFile.Open(overridesPath(root), settings, _ => { }, warnings.Add, out var effective);
        Assert.AreEqual("From the file", effective.Name);
        Assert.AreNotEqual(settings.ContainerSettings[0].LocalSettings!.AutoBackUp, effective.ContainerSettings![0].LocalSettings!.AutoBackUp);
        Assert.AreEqual(settings.ContainerSettings[0].LocalSettings!.NodeCacheSizeGb, effective.ContainerSettings[0].LocalSettings!.NodeCacheSizeGb);
        Assert.AreEqual(2, warnings.Count, string.Join(Environment.NewLine, warnings));
        CollectionAssert.AreEquivalent(new[] { "Name", "ContainerSettings[" + id + "].LocalSettings.AutoBackUp" }, file.Entries.Select(e => e.Path).ToArray());
    }

    [TestMethod]
    public void Open_RefusesAFileThatIsNotJson() {
        var root = newRoot("broken");
        writeOverrides(root, "{ \"Name\": ");
        var error = Assert.ThrowsExactly<SettingsOverridesFileInvalidException>(() => SettingsOverridesFile.Open(overridesPath(root), RelatudeDBServerSettings.CreateDefault(), _ => { }, _ => { }, out _));
        StringAssert.Contains(error.Message, "not valid JSON");
    }

    [TestMethod]
    public void Save_WritesTheDifference_AndDeletesTheFileWhenThereIsNone() {
        var root = newRoot("save");
        var settings = RelatudeDBServerSettings.CreateDefault();
        var file = SettingsOverridesFile.Open(overridesPath(root), settings, _ => { }, _ => { }, out _);
        var changed = TestServerHost.Copy(settings);
        changed.Description = "Changed";
        Assert.IsTrue(file.Save(toJson(changed)));
        StringAssert.Contains(File.ReadAllText(overridesPath(root)), "\"Description\": \"Changed\"");
        Assert.IsFalse(file.Save(toJson(changed)), "the same difference is not written again");
        Assert.IsTrue(file.Save(toJson(settings)));
        Assert.IsFalse(File.Exists(overridesPath(root)));
    }

    // ---- the server ----

    static TestServerHost start(string root, RelatudeDBServerSettings? settings = null, Dictionary<string, string?>? configuration = null,
        Action<ServerOptions>? options = null, bool overridesFile = true, bool withDatabase = false) {
        var host = TestServerHost.Start(root, settings: settings, overridesFile: overridesFile, configuration: configuration, options: options,
            overridesWithDatabase: withDatabase, configure: s => { foreach (var c in s.ContainerSettings!) c.AutoOpen = false; });
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }

    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var json = JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
    static string[] rejected(JsonElement result) => [.. prop(result, "rejected").EnumerateArray().Select(r => prop(r, "path").GetString()!)];
    static JsonElement[] overrideEntries(JsonElement overrides)
        => [.. prop(overrides, "groups").EnumerateArray().SelectMany(g => prop(g, "entries").EnumerateArray())];
    static JsonElement setting(JsonElement page, string path)
        => prop(page, "sections").EnumerateArray()
            .SelectMany(s => prop(s, "groups").EnumerateArray())
            .SelectMany(g => prop(g, "settings").EnumerateArray())
            .First(s => prop(s, "path").GetString() == path);

    [TestMethod]
    public async Task AdminSave_WritesTheOverridesFile_AndNotRelatudeDbJson() {
        var root = newRoot("server-save");
        var original = TestServerHost.MemorySettings(1);
        var host = start(root, TestServerHost.Copy(original));
        try {
            await command(host, "settings-server-save", new { values = new { Description = "Changed here" } });
            Assert.AreEqual(0, host.Settings.Writes.Count, "relatude.db.json is not written");
            var text = File.ReadAllText(overridesPath(root));
            StringAssert.Contains(text, "Changed here");
            var page = await command(host, "settings-server-get", new { });
            var description = setting(page, "Description");
            Assert.IsTrue(prop(description, "inOverrides").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, prop(description, "fileValue").ValueKind, "relatude.db.json has no description");
            Assert.AreEqual(1, prop(prop(page, "overrides"), "count").GetInt32());
        } finally {
            await host.DisposeAsync();
        }
        // a second start reads relatude.db.json as it was, and the change comes back from the file
        var again = start(root, TestServerHost.Copy(original));
        try {
            Assert.AreEqual("Changed here", again.Server.Settings.Description);
        } finally {
            await again.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_ReadsTheOverridesFileAgain() {
        var root = newRoot("restart");
        var original = TestServerHost.MemorySettings(1);
        var host = start(root, TestServerHost.Copy(original));
        try {
            var storeId = original.ContainerSettings![0].Id;
            await command(host, "settings-db-save", new { storeId, values = new Dictionary<string, object> { ["LocalSettings.NodeCacheSizeGb"] = "2.5" }, reopen = false });
            host.Settings.Settings = TestServerHost.Copy(original); // relatude.db.json as it is on disk
            Assert.IsTrue(await host.Server.SoftRestartAsync());
            Assert.AreEqual(2.5, host.Server.Containers[storeId].Settings.LocalSettings!.NodeCacheSizeGb);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ConfigurationValues_NeverReachEitherFile() {
        var root = newRoot("config");
        var original = TestServerHost.MemorySettings(1);
        original.MasterPassword = "from the file";
        var host = start(root, TestServerHost.Copy(original), configuration: new() { ["RelatudeDB:MasterPassword"] = "from configuration" });
        try {
            Assert.AreEqual("from configuration", host.Server.Settings.MasterPassword);
            var result = await command(host, "settings-server-save", new { values = new Dictionary<string, object> { ["Description"] = "Changed", ["MasterPassword"] = "typed" } });
            CollectionAssert.AreEqual(new[] { "MasterPassword" }, rejected(result), "a configured setting cannot be edited");
            Assert.IsFalse(File.ReadAllText(overridesPath(root)).Contains("from configuration"));
            var moved = await command(host, "settings-overrides-move", new { paths = (string[]?)null });
            Assert.AreEqual(1, host.Settings.Writes.Count);
            Assert.AreEqual("from the file", host.Settings.Writes[0].MasterPassword);
            Assert.AreEqual("Changed", host.Settings.Writes[0].Description);
            Assert.AreEqual(1, prop(moved, "moved").GetArrayLength());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Move_LeavesWhatConfigurationDecidesInTheOverridesFile() {
        var root = newRoot("move-config");
        writeOverrides(root, "{ \"MasterUserName\": \"from the overrides file\", \"Description\": \"described\" }");
        var original = TestServerHost.MemorySettings(1);
        original.MasterUserName = "from the file";
        var host = start(root, TestServerHost.Copy(original), configuration: new() { ["RelatudeDB:MasterUserName"] = "from configuration" });
        try {
            var list = await command(host, "settings-overrides-get", new { });
            var userName = overrideEntries(list).Single(e => prop(e, "path").GetString() == "MasterUserName");
            Assert.IsFalse(prop(userName, "canMove").GetBoolean());
            Assert.IsFalse(prop(userName, "canDiscard").GetBoolean());
            await command(host, "settings-overrides-move", new { paths = new[] { "MasterUserName", "Description" } });
            Assert.AreEqual("from the file", host.Settings.Writes.Single().MasterUserName);
            Assert.AreEqual("described", host.Settings.Writes.Single().Description);
            var text = File.ReadAllText(overridesPath(root));
            StringAssert.Contains(text, "from the overrides file");
            Assert.IsFalse(text.Contains("described"));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ValuesSetByCode_AreLocked_AndNeverSaved() {
        var root = newRoot("code");
        var original = TestServerHost.MemorySettings(1);
        var storeId = original.ContainerSettings![0].Id;
        var host = start(root, TestServerHost.Copy(original), options: o => o.OnStoreSettingsInit = (local, _) => local.NodeCacheSizeGb = 7);
        try {
            Assert.AreEqual(7, host.Server.Containers[storeId].Settings.LocalSettings!.NodeCacheSizeGb);
            var page = await command(host, "settings-db-get", new { storeId });
            Assert.IsTrue(prop(setting(page, "LocalSettings.NodeCacheSizeGb"), "codeSet").GetBoolean());
            var result = await command(host, "settings-db-save", new { storeId, values = new Dictionary<string, object> { ["Description"] = "Changed", ["LocalSettings.NodeCacheSizeGb"] = "2" }, reopen = false });
            CollectionAssert.AreEqual(new[] { "LocalSettings.NodeCacheSizeGb" }, rejected(result));
            var text = File.ReadAllText(overridesPath(root));
            StringAssert.Contains(text, "Changed");
            Assert.IsFalse(text.Contains("NodeCacheSizeGb"), text);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task WithTheOverridesFileOff_SavesGoToRelatudeDbJson_WithoutTheCodesValues() {
        var root = newRoot("off");
        var original = TestServerHost.MemorySettings(1);
        var host = start(root, TestServerHost.Copy(original), overridesFile: false, options: o => o.OnServerSettingsInit = s => s.TokenCookieName = "set-by-code");
        try {
            await command(host, "settings-server-save", new { values = new { Description = "Changed" } });
            Assert.AreEqual(1, host.Settings.Writes.Count);
            Assert.AreEqual("Changed", host.Settings.Writes[0].Description);
            Assert.AreEqual(original.TokenCookieName, host.Settings.Writes[0].TokenCookieName, "the code's value is not baked into the file");
            Assert.AreEqual("set-by-code", host.Server.Settings.TokenCookieName);
            Assert.IsFalse(File.Exists(overridesPath(root)));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Move_PutsTheChosenEntriesIntoRelatudeDbJson() {
        var root = newRoot("move");
        var original = TestServerHost.MemorySettings(1);
        var host = start(root, TestServerHost.Copy(original));
        try {
            await command(host, "settings-server-save", new { values = new { Description = "Described", Name = "Renamed" } });
            var result = await command(host, "settings-overrides-move", new { paths = new[] { "Description" } });
            Assert.AreEqual("Described", host.Settings.Writes.Single().Description);
            Assert.AreEqual(original.Name, host.Settings.Writes.Single().Name);
            var left = overrideEntries(prop(result, "overrides")).Select(e => prop(e, "path").GetString()).ToArray();
            CollectionAssert.AreEqual(new[] { "Name" }, left);
            Assert.AreEqual("Described", host.Server.Settings.Description, "nothing in force changes");
            Assert.AreEqual("Renamed", host.Server.Settings.Name);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Discard_PutsBackWhatRelatudeDbJsonSays() {
        var root = newRoot("discard");
        var original = TestServerHost.MemorySettings(1);
        original.Description = "";
        var host = start(root, TestServerHost.Copy(original));
        try {
            await command(host, "settings-server-save", new { values = new { Description = "Described" } });
            await command(host, "settings-overrides-discard", new { paths = new[] { "Description" } });
            Assert.AreEqual("", host.Server.Settings.Description, "an empty string comes back as it was, not as null");
            Assert.IsFalse(File.Exists(overridesPath(root)));
            Assert.AreEqual(0, host.Settings.Writes.Count);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ListElements_AddedAndRemovedHere_AreKeptAsSuch_AndCanBeTakenBack() {
        var root = newRoot("lists");
        var original = TestServerHost.MemorySettings(1);
        var storeId = original.ContainerSettings![0].Id;
        var source = original.ContainerSettings[0].DatamodelSources![0].Id;
        var host = start(root, TestServerHost.Copy(original));
        try {
            var added = await command(host, "settings-db-list-add", new { storeId, path = "IOSettings" });
            var addedId = prop(added, "added").GetGuid();
            await command(host, "settings-db-list-remove", new { storeId, path = "DatamodelSources", id = source });
            var c = "ContainerSettings[" + storeId + "]";
            var list = await command(host, "settings-overrides-get", new { });
            var kinds = overrideEntries(list).ToDictionary(e => prop(e, "path").GetString()!, e => prop(e, "kind").GetString());
            Assert.AreEqual("added", kinds[c + ".IOSettings[" + addedId + "]"]);
            Assert.AreEqual("removed", kinds[c + ".DatamodelSources[" + source + "]"]);
            var page = await command(host, "settings-db-get", new { storeId });
            var lists = prop(page, "sections").EnumerateArray().SelectMany(s => prop(s, "groups").EnumerateArray())
                .Where(g => prop(g, "list").ValueKind == JsonValueKind.Object).Select(g => prop(g, "list")).ToArray();
            Assert.AreEqual(1, prop(lists.Single(l => prop(l, "path").GetString() == "DatamodelSources"), "removedHere").GetArrayLength());
            Assert.IsTrue(prop(lists.Single(l => prop(l, "path").GetString() == "IOSettings"), "items").EnumerateArray()
                .Single(i => prop(i, "id").GetGuid() == addedId).GetProperty("addedHere").GetBoolean());

            var discarded = await command(host, "settings-overrides-discard", new { paths = kinds.Keys.ToArray() });
            Assert.AreEqual(0, prop(discarded, "rejected").GetArrayLength(), discarded.ToString());
            var settings = host.Server.Containers[storeId].Settings;
            Assert.AreEqual(1, settings.IOSettings!.Length);
            Assert.AreEqual(source, settings.DatamodelSources!.Single().Id);
            Assert.IsFalse(File.Exists(overridesPath(root)));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task NewDatabase_IsAnAddedElement_AndMovesIntoRelatudeDbJsonWhole() {
        var root = newRoot("database");
        var original = TestServerHost.MemorySettings(1);
        var host = start(root, TestServerHost.Copy(original));
        try {
            var created = await command(host, "database-create", new { name = "Second", autoOpen = false });
            var id = prop(created, "storeId").GetGuid();
            var list = await command(host, "settings-overrides-get", new { });
            var entry = overrideEntries(list).Single(e => prop(e, "path").GetString() == "ContainerSettings[" + id + "]");
            Assert.AreEqual("added", prop(entry, "kind").GetString());
            Assert.IsFalse(prop(entry, "canDiscard").GetBoolean());
            await command(host, "settings-overrides-move", new { paths = new[] { "ContainerSettings[" + id + "]" } });
            Assert.AreEqual(2, host.Settings.Writes.Single().ContainerSettings!.Length);
            Assert.IsFalse(File.Exists(overridesPath(root)));
        } finally {
            await host.DisposeAsync();
        }
    }
}
