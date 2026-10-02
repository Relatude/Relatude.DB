using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.ModelEditor;
using Relatude.DB.NodeServer.Settings;
using Relatude.OverrideModels;

namespace Relatude.Server;

/// <summary>
/// The data model editor and the database's overrides, end to end on a server with memory storage: the
/// model is compiled into this assembly, so its source is read only, and overrides are the only way the
/// editor can change it.
/// </summary>
[TestClass]
public class DatamodelOverridesEditorTests {
    string _root = "";
    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "Overrides_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_root, true); } catch { }
    }

    static readonly Guid compiledSourceId = new("33333333-0000-0000-0000-000000000001");
    static TestServerHost start(string root) => TestServerHost.Start(root, configure: s => {
        var c = s.ContainerSettings![0];
        c.DatamodelSources = [new DatamodelSource {
            Id = compiledSourceId, Name = "Compiled", Type = DatamodelSourceType.CompiledTypes,
            Reference = typeof(IOvContent).Assembly.GetName().Name, Namespace = typeof(IOvContent).Namespace,
        }];
        c.LocalSettings!.EnableInstantTextIndexingByDefault = true; // searchable right after the insert
    });
    static NodeStoreContainer container(TestServerHost host) => host.Server.Containers.Values.First();
    static Guid typeId<T>() => BuildUtils.GetOrCreateNodeTypeId(typeof(T));

    // the active model as the editor gets it, with the overrides the test wants
    static string draftWith(NodeStoreContainer c, Action<Datamodel> change) {
        var dm = DatamodelJson.Deserialize(c.DatamodelAsLoadedJson!);
        change(dm);
        return DatamodelJson.Serialize(dm);
    }
    static PropertyModel property(Datamodel dm, string name) => dm.NodeTypes[typeId<IOvContent>()].Properties.Values.First(p => p.CodeName == name);

    [TestMethod]
    public async Task Overrides_OnAReadOnlySource_Validate_Activate_AndTakeEffect() {
        var host = start(_root);
        try {
            var c = container(host);
            var drafts = new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value));
            var page = c.Store!.Create<OvPage>();
            page.Title = "giraffe";
            page.Body = "zebra";
            c.Store.Insert(page);

            var json = draftWith(c, dm => {
                var title = property(dm, "Title");
                var body = property(dm, "Body");
                dm.Overrides = new DatamodelOverrides();
                var o = dm.Overrides.ForType(dm.NodeTypes[typeId<OvPage>()]);
                o.Properties = new() {
                    [title.Id] = new PropertyOverride { DefaultValue = "From the overrides" },
                    [body.Id] = new PropertyOverride { ExcludeFromTextIndex = true },
                };
            });
            var validation = new DatamodelValidator(host.Server, c).Validate(json, dryRun: true);
            Assert.IsFalse(validation.HasErrors, string.Join("\n", validation.Issues.Select(i => i.Code + ": " + i.Message)));
            Assert.IsFalse(validation.Issues.Any(i => i.Code == "read-only-source"), "overrides are not a change to the source");
            Assert.IsTrue(validation.Plan!.OverridesChange);
            CollectionAssert.Contains(validation.TextReindexTypes, typeId<OvPage>(), "Body leaves OvPage's text");

            var result = new DatamodelActivator(host.Server, c, drafts).Activate(json, acceptWarnings: true, note: null);
            Assert.IsTrue(result.Activated, result.Message);
            Assert.IsTrue(result.Reopened);
            Assert.IsTrue(result.OverridesChanged);
            Assert.AreEqual(true, result.ChecksumMatches, "the overrides read back from the database are the draft's");
            Assert.IsTrue(result.TextReindexQueued >= 1, "the stored node is queued for text indexing again");

            var io = host.Server.GetIO(c.Settings.IoDatabase!.Value);
            Assert.IsTrue(io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_OverridesFileKey), "kept with the database");
            StringAssert.Contains(io.ReadAllTextUTF8(FileKeyUtility.Datamodel_OverridesFileKey), "\"Name\": \"Relatude.OverrideModels.OvPage\"", "the file names what it overrides");
            Assert.AreEqual("From the overrides", c.Store!.Create<OvPage>().Title, "the reopened database applies them");
            Assert.AreEqual("News", c.Store.Create<OvNews>().Title, "other types keep their own");

            // the same draft again changes nothing; without overrides the file goes
            var again = new DatamodelValidator(host.Server, c).Validate(json, dryRun: false);
            Assert.IsFalse(again.Plan!.OverridesChange);
            var none = draftWith(c, dm => dm.Overrides = null);
            var removed = new DatamodelActivator(host.Server, c, drafts).Activate(none, acceptWarnings: true, note: null);
            Assert.IsTrue(removed.Activated, removed.Message);
            Assert.IsFalse(io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_OverridesFileKey));
            Assert.AreEqual("Untitled", c.Store!.Create<OvPage>().Title);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TurningTextIndexingOff_TakesTheNodesOutOfTheTextIndex() {
        var host = start(_root);
        try {
            var c = container(host);
            var drafts = new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value));
            var page = c.Store!.Create<OvPage>();
            page.Title = "giraffe";
            c.Store.Insert(page);
            Assert.AreEqual(1, c.Store.Query<OvPage>().WhereSearch("giraffe").Count(), "indexed through the inherited switch");

            var json = draftWith(c, dm => {
                dm.Overrides = new DatamodelOverrides();
                dm.Overrides.ForType(dm.NodeTypes[typeId<OvPage>()]).TextIndex = false;
            });
            var result = new DatamodelActivator(host.Server, c, drafts).Activate(json, acceptWarnings: true, note: null);
            Assert.IsTrue(result.Activated, result.Message);
            Assert.AreEqual(1, result.TextCleared);
            Assert.AreEqual(false, c.Datamodel!.NodeTypes[typeId<OvPage>()].TextIndex);
            Assert.AreEqual(0, c.Store!.Query<IOvContent>().WhereSearch("giraffe").Count(), "no stale text left behind");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ChangingTheSource_IsStillRefused_WhenItCannotBeWritten() {
        var host = start(_root);
        try {
            var c = container(host);
            var json = draftWith(c, dm => property(dm, "Title").DisplayName = !property(dm, "Title").DisplayName);
            var validation = new DatamodelValidator(host.Server, c).Validate(json, dryRun: false);
            Assert.IsTrue(validation.Issues.Any(i => i.Code == "read-only-source"), "only overrides get past a compiled source");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task OverridesPath_PutsThemInAFileOfTheSite() {
        var host = TestServerHost.Start(_root, configure: s => {
            var c = s.ContainerSettings![0];
            c.DatamodelSources = [new DatamodelSource {
                Id = compiledSourceId, Name = "Compiled", Type = DatamodelSourceType.CompiledTypes,
                Reference = typeof(IOvContent).Assembly.GetName().Name, Namespace = typeof(IOvContent).Namespace,
            }];
            c.DatamodelOverridesPath = "Models/overrides.json";
        });
        try {
            var c = container(host);
            var json = draftWith(c, dm => {
                dm.Overrides = new DatamodelOverrides();
                dm.Overrides.ForType(dm.NodeTypes[typeId<OvNote>()]).Hidden = true;
            });
            var result = new DatamodelActivator(host.Server, c, new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value))).Activate(json, acceptWarnings: true, note: null);
            Assert.IsTrue(result.Activated, result.Message);
            var path = Path.Combine(_root, "Models", "overrides.json"); // the root data folder: relative paths resolve against it
            Assert.IsTrue(File.Exists(path), "written where the setting says: " + path);
            Assert.IsTrue(c.Datamodel!.NodeTypes[typeId<OvNote>()].Hidden);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public void FileKeys_TheOverridesAreNotAHistoryEntry() {
        Assert.IsFalse(FileKeyUtility.Datamodel_IsHistoryFileKey(FileKeyUtility.Datamodel_OverridesFileKey));
        Assert.AreEqual(FileKeyUtility.DatamodelsFolderName, FileKeyUtility.Datamodel_OverridesFileKey[0]);
    }

    [TestMethod]
    public void Same_IgnoresEntriesThatOverrideNothing() {
        var a = new DatamodelOverrides();
        a.NodeTypes[Guid.NewGuid()] = new NodeTypeOverride { Name = "Reset", Properties = new() { [Guid.NewGuid()] = new PropertyOverride { Name = "Gone" } } };
        Assert.IsTrue(DatamodelOverridesFile.Same(a, null));
        a.NodeTypes.Values.First().TextIndex = true;
        Assert.IsFalse(DatamodelOverridesFile.Same(a, null));
    }

    [TestMethod]
    public void Catalog_MarksWhatCanBeOverridden_AndEveryOverrideNamesARealAttribute() {
        var modelMembers = new[] { typeof(NodeTypeModel) }.Concat(Enum.GetValues<PropertyType>()
            .Where(pt => pt != PropertyType.Any).Select(PropertyModelJsonConverter.GetModelType))
            .SelectMany(t => t.GetProperties()).Select(p => p.Name).ToHashSet();
        foreach (var name in DatamodelOverrides.Scopes(typeof(NodeTypeOverride)).Keys) Assert.IsTrue(typeof(NodeTypeModel).GetProperty(name) != null, "NodeTypeModel has no " + name);
        foreach (var name in DatamodelOverrides.Scopes(typeof(PropertyOverride)).Keys) Assert.IsTrue(modelMembers.Contains(name), "no property model has " + name);
        var schema = System.Text.Json.JsonSerializer.SerializeToNode(DatamodelCatalog.Schema)!;
        var textIndex = schema["NodeType"]!.AsArray().First(f => (string)f!["Path"]! == "TextIndex")!;
        Assert.AreEqual("inherited", (string?)textIndex["Overridable"]);
        var codeName = schema["NodeType"]!.AsArray().First(f => (string)f!["Path"]! == "CodeName")!;
        Assert.IsNull((string?)codeName["Overridable"], "a name is not an attribute that can be overridden");
        var unique = schema["PropertyCommon"]!.AsArray().First(f => (string)f!["Path"]! == "UniqueValues")!;
        Assert.IsNull((string?)unique["Overridable"]);
        var indexed = schema["PropertyCommon"]!.AsArray().First(f => (string)f!["Path"]! == "Indexed")!;
        Assert.AreEqual("wholeProperty", (string?)indexed["Overridable"]);
    }
}
