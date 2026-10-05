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
            var defaultChanged = validation.Issues.FirstOrDefault(i => i.Code == "default-changed");
            Assert.IsNotNull(defaultChanged, "the stored page reads the new default");
            StringAssert.Contains(defaultChanged.Message, "from \"Untitled\" to \"From the overrides\"", "string defaults are named in quotes");

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
    public async Task AnInheritingType_AsksForAnIndex_AndTheReopenedDatabaseHasIt() {
        var host = start(_root);
        try {
            var c = container(host);
            var drafts = new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value));
            var page = c.Store!.Create<OvPage>();
            page.Body = "zebra";
            c.Store.Insert(page);
            Guid body = Guid.Empty;
            var json = draftWith(c, dm => {
                body = property(dm, "Body").Id;
                dm.Overrides = new DatamodelOverrides();
                dm.Overrides.ForProperty(dm.NodeTypes[typeId<OvPage>()], property(dm, "Body")).Indexed = true;
            });
            var validation = new DatamodelValidator(host.Server, c).Validate(json, dryRun: false);
            var said = string.Join(Environment.NewLine, validation.Issues.Select(i => i.Code + ": " + i.Message));
            Assert.IsFalse(validation.HasErrors, said);
            Assert.IsFalse(validation.Issues.Any(i => i.Code == "override" && i.Message.Contains("OvPage")), "asking for an index is not a problem: " + said);
            Assert.IsTrue(validation.Issues.Any(i => i.Code == "indexes-change"), "the index is new, so the database rebuilds");
            Assert.IsFalse(c.Datamodel!.Properties[body].Indexed);

            var result = new DatamodelActivator(host.Server, c, drafts).Activate(json, acceptWarnings: true, note: null);
            Assert.IsTrue(result.Activated, result.Message);
            Assert.IsTrue(c.Datamodel!.Properties[body].Indexed, "the reopened database gives Body the index OvPage asks for");
            Assert.IsTrue(((DataStoreLocal)c.Store!.Datastore)._definition.Properties[body].Indexed);
            var facet = c.Store.Query<IOvContent>().Facets().AddValueFacet(nameof(IOvContent.Body)).Execute().Facets.First(f => f.CodeName == nameof(IOvContent.Body));
            Assert.AreEqual(1, facet.Values.First(v => Equals(v.Value, "zebra")).Count, "built over the nodes already stored");
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

    // the overrides every installation shares, in the application's settings folder
    string sharedPath => Path.Combine(_root, "relatude.settings", "datamodel.overrides.json");
    // a shared file as the application would have it, read when the database opens again
    void writeShared(NodeStoreContainer c, Action<DatamodelOverrides, Datamodel> fill) {
        var dm = DatamodelJson.Deserialize(c.DatamodelAsLoadedJson!);
        var o = new DatamodelOverrides();
        fill(o, dm);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
        File.WriteAllText(sharedPath, DatamodelOverridesFile.Serialize(o));
        c.ApplyNewSettings(c.Settings, reopenIfOpen: true);
    }
    static void sharedNoteHiddenAndPageTitle(DatamodelOverrides o, Datamodel dm) {
        o.ForType(dm.NodeTypes[typeId<OvNote>()]).Hidden = true;
        o.ForProperty(dm.NodeTypes[typeId<OvPage>()], property(dm, "Title")).DefaultValue = "Shared title";
    }

    [TestMethod]
    public async Task SharedOverrides_AreInForce_AndAnActivationWritesOnlyWhatDiffersFromThem() {
        var host = start(_root);
        try {
            var c = container(host);
            var drafts = new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value));
            writeShared(c, sharedNoteHiddenAndPageTitle);
            var sharedText = File.ReadAllText(sharedPath);
            Assert.IsTrue(c.Datamodel!.NodeTypes[typeId<OvNote>()].Hidden, "the shared file is read when the database opens");
            Assert.AreEqual("Shared title", c.Store!.Create<OvPage>().Title);
            var io = host.Server.GetIO(c.Settings.IoDatabase!.Value);
            Assert.IsFalse(io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_OverridesFileKey), "nothing of this installation's own");
            Assert.AreEqual("relatude.settings/datamodel.overrides.json", c.OverridesFile.SharedLocation, "a database without a short name keeps it in relatude.settings itself");

            // the draft takes the shared Hidden away, keeps the shared title and adds one of its own
            var json = draftWith(c, dm => {
                dm.Overrides!.NodeTypes[typeId<OvNote>()].Hidden = null;
                dm.Overrides.ForType(dm.NodeTypes[typeId<OvNews>()]).Hidden = true;
            });
            var validation = new DatamodelValidator(host.Server, c).Validate(json, dryRun: false);
            Assert.IsFalse(validation.HasErrors, string.Join("\n", validation.Issues.Select(i => i.Code + ": " + i.Message)));
            Assert.IsTrue(validation.Plan!.OverridesChange);
            var result = new DatamodelActivator(host.Server, c, drafts).Activate(json, acceptWarnings: true, note: null);
            Assert.IsTrue(result.Activated, result.Message);
            Assert.AreEqual(true, result.ChecksumMatches, "the two files read back as the draft");

            var text = io.ReadAllTextUTF8(FileKeyUtility.Datamodel_OverridesFileKey);
            StringAssert.StartsWith(text, "//", "the file says what it is");
            var entries = DatamodelOverridesLayers.Entries(DatamodelOverridesLayers.Parse(text, keepResets: true));
            Assert.AreEqual(2, entries.Count, string.Join(", ", entries.Select(e => e.Attribute)));
            Assert.IsTrue(entries.Any(e => e.TypeId == typeId<OvNote>() && e.Attribute == "Hidden" && e.Value == null), "the shared value taken away is a null");
            Assert.IsTrue(entries.Any(e => e.TypeId == typeId<OvNews>() && e.Attribute == "Hidden"));
            Assert.AreEqual(sharedText, File.ReadAllText(sharedPath), "an activation never writes the shared file");
            Assert.IsFalse(c.Datamodel!.NodeTypes[typeId<OvNote>()].Hidden, "taken away on this installation");
            Assert.IsTrue(c.Datamodel.NodeTypes[typeId<OvNews>()].Hidden);
            Assert.AreEqual("Shared title", c.Store!.Create<OvPage>().Title, "still from the shared file");

            // back to what the shared file says: nothing of this installation's is left
            var back = draftWith(c, dm => {
                dm.Overrides!.ForType(dm.NodeTypes[typeId<OvNote>()]).Hidden = true;
                dm.Overrides.NodeTypes[typeId<OvNews>()].Hidden = null;
            });
            Assert.IsTrue(new DatamodelActivator(host.Server, c, drafts).Activate(back, acceptWarnings: true, note: null).Activated);
            Assert.IsFalse(io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_OverridesFileKey));
            Assert.IsTrue(c.Datamodel!.NodeTypes[typeId<OvNote>()].Hidden);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Moving_TakesTheInstallationsOverridesIntoTheSharedFile_AndNothingInForceChanges() {
        var host = start(_root);
        try {
            var c = container(host);
            var drafts = new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value));
            var io = host.Server.GetIO(c.Settings.IoDatabase!.Value);
            writeShared(c, sharedNoteHiddenAndPageTitle);
            Guid title = Guid.Empty;
            var json = draftWith(c, dm => {
                title = property(dm, "Title").Id;
                dm.Overrides!.NodeTypes[typeId<OvNote>()].Hidden = null; // taken away here
                dm.Overrides.ForProperty(dm.NodeTypes[typeId<OvPage>()], property(dm, "Title")).DefaultValue = "Installation title"; // over the shared one
                dm.Overrides.ForType(dm.NodeTypes[typeId<OvNews>()]).Hidden = true; // this installation's own
            });
            Assert.IsTrue(new DatamodelActivator(host.Server, c, drafts).Activate(json, acceptWarnings: true, note: null).Activated);
            var file = c.OverridesFile;
            var before = file.Read(host.Server);
            var checksum = DatamodelJson.Checksum(new DatamodelValidator(host.Server, c).LoadActive());

            var moved = file.MoveToShared(host.Server, [new(typeId<OvNote>(), null, "Hidden"), new(typeId<OvPage>(), title, "DefaultValue"), new(Guid.NewGuid(), null, "Hidden")], null);
            Assert.AreEqual(2, moved, "an entry the file does not have is passed over");
            Assert.IsTrue(DatamodelOverridesFile.Same(before, file.Read(host.Server)), "what is in force is the same");
            var shared = DatamodelOverridesLayers.Entries(file.ReadSharedLayer());
            Assert.IsFalse(shared.Any(e => e.TypeId == typeId<OvNote>()), "the reset took the shared value away");
            Assert.AreEqual("Installation title", shared.Single(e => e.Attribute == "DefaultValue").Value!.GetValue<string>());
            var left = DatamodelOverridesLayers.Entries(file.ReadInstallationLayer(host.Server));
            Assert.AreEqual(1, left.Count);
            Assert.AreEqual(typeId<OvNews>(), left[0].TypeId);
            StringAssert.StartsWith(File.ReadAllText(sharedPath), "//", "the shared file says what it is");

            // the download shows what the move then writes; the last one empties this installation's file
            var preview = file.SharedTextWith(host.Server, [new(typeId<OvNews>(), null, "Hidden")], null);
            Assert.AreNotEqual(preview, File.ReadAllText(sharedPath), "the download writes nothing");
            Assert.AreEqual(1, file.MoveToShared(host.Server, [new(typeId<OvNews>(), null, "Hidden")], null));
            Assert.AreEqual(preview, File.ReadAllText(sharedPath));
            Assert.IsFalse(io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_OverridesFileKey));

            // and the database opens with the model it had before the moves
            c.ApplyNewSettings(c.Settings, reopenIfOpen: true);
            Assert.IsTrue(c.Datamodel!.NodeTypes[typeId<OvNews>()].Hidden);
            Assert.IsFalse(c.Datamodel.NodeTypes[typeId<OvNote>()].Hidden);
            Assert.AreEqual("Installation title", c.Store!.Create<OvPage>().Title);
            Assert.AreEqual(checksum, DatamodelJson.Checksum(new DatamodelValidator(host.Server, c).LoadActive()));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheOldOverridesPath_IsCopiedToTheSharedPlace() {
        // what the setting named in an earlier version: the overrides meant for source control
        var named = Path.Combine(_root, "Models", "overrides.json");
        Directory.CreateDirectory(Path.GetDirectoryName(named)!);
        var o = new DatamodelOverrides();
        o.NodeTypes[typeId<OvNote>()] = new NodeTypeOverride { Name = "OvNote", Hidden = true };
        File.WriteAllText(named, DatamodelOverridesFile.Serialize(o));
        var host = TestServerHost.Start(_root, configure: s => {
            var c = s.ContainerSettings![0];
            c.DatamodelSources = [new DatamodelSource {
                Id = compiledSourceId, Name = "Compiled", Type = DatamodelSourceType.CompiledTypes,
                Reference = typeof(IOvContent).Assembly.GetName().Name, Namespace = typeof(IOvContent).Namespace,
            }];
#pragma warning disable CS0618 // the setting an earlier version wrote
            c.DatamodelOverridesPath = "Models/overrides.json";
#pragma warning restore CS0618
        });
        try {
            var c = container(host);
            Assert.IsTrue(File.Exists(sharedPath), "copied to the shared place");
            Assert.IsTrue(File.Exists(named), "the old file is left for the team to remove with the setting");
            Assert.IsTrue(c.Datamodel!.NodeTypes[typeId<OvNote>()].Hidden, "nothing in force changed");
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("DatamodelOverridesPath") && l.Item2.Contains("copied")));
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(new NodeStoreContainerSettings()).Contains("DatamodelOverridesPath"), "kept only for reading: not written when unset");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ChangingTheShortName_TakesTheSharedFileAlong() {
        var host = start(_root);
        try {
            var c = container(host);
            var before = c.OverridesFile;
            Directory.CreateDirectory(Path.GetDirectoryName(before.SharedPath)!);
            File.WriteAllText(before.SharedPath, "{ \"NodeTypes\": {} }");
            c.Settings.ShortName = "shop";
            var after = c.OverridesFile;
            Assert.AreEqual("relatude.settings/shop/datamodel.overrides.json", after.SharedLocation);
            Assert.IsTrue(File.Exists(after.SharedPath), "moved with the short name");
            Assert.IsFalse(File.Exists(before.SharedPath));
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "relatude.settings")), "the settings folder itself stays");
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("Moved the shared datamodel overrides")));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public void FileKeys_TheOverridesAreNotAHistoryEntry() {
        Assert.IsFalse(FileKeyUtility.Datamodel_IsHistoryFileKey(FileKeyUtility.Datamodel_OverridesFileKey));
        // kept with the other changes made in the admin UI; an older version's file in datamodels/ is no history entry either
        Assert.AreEqual(FileKeyUtility.OverridesFolderName, FileKeyUtility.Datamodel_OverridesFileKey[0]);
        Assert.AreEqual(FileKeyUtility.DatamodelsFolderName, FileKeyUtility.Datamodel_LegacyOverridesFileKey[0]);
        Assert.IsFalse(FileKeyUtility.Datamodel_IsHistoryFileKey(FileKeyUtility.Datamodel_LegacyOverridesFileKey));
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
        Assert.AreEqual("anyType", (string?)indexed["Overridable"], "any type that has the property can ask for its index");
        Assert.AreEqual(true, (bool?)indexed["OverrideAsks"]);
        var notFacet = schema["PropertyCommon"]!.AsArray().First(f => (string)f!["Path"]! == "NotFacet")!;
        Assert.AreEqual("anyType", (string?)notFacet["Overridable"], "and for it to be a facet");
        Assert.AreEqual(false, (bool?)notFacet["OverrideAsks"], "NotFacet is an opt-out: false asks");
        var indexType = schema["PropertyCommon"]!.AsArray().First(f => (string)f!["Path"]! == "IndexType")!;
        Assert.AreEqual("wholeProperty", (string?)indexType["Overridable"]);
        Assert.IsNull((bool?)indexType["OverrideAsks"]);
    }
}
