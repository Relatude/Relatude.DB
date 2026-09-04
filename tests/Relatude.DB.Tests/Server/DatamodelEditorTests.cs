using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.ModelEditor;
using Relatude.SourceLoaderModels;

namespace Relatude.Server;

/// <summary>
/// The pieces of the data model editor that do not need a running server: the draft and history
/// store, the write plan for the different source kinds, and the catalog the editors are built from.
/// </summary>
[TestClass]
public class DatamodelEditorTests {
    string _root = "";
    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "ModelEditor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_root, true); } catch { }
    }

    static readonly Guid sourceId = new("22222222-0000-0000-0000-000000000001");

    /// <summary>The library model (authors, books, a relation) tagged as coming from one source.</summary>
    static Datamodel libraryModel(DatamodelSourceType type) {
        var dm = new Datamodel();
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = type, Namespace = "Relatude.SourceLoaderModels" };
        dm.Sources.Add(source);
        dm.CurrentSourceId = sourceId;
        dm.Add<SlAuthor>();
        dm.Add<SlBook>();
        dm.Add<SlBooksRel>();
        dm.CurrentSourceId = DatamodelSource.CodeSourceId;
        return dm;
    }
    static Datamodel copy(Datamodel dm) => DatamodelJson.Deserialize(DatamodelJson.Serialize(dm));
    static Datamodel initialized(Datamodel dm) {
        var c = copy(dm);
        c.EnsureInitalization();
        return c;
    }
    static NodeTypeModel typeNamed(Datamodel dm, string codeName) => dm.NodeTypes.Values.First(t => t.CodeName == codeName);

    // ---- keys ----

    [TestMethod]
    public void FileKeys_DraftAndHistoryAreTold_Apart() {
        var draft = FileKeyUtility.Datamodel_DraftFileKey;
        Assert.AreEqual(FileKeyUtility.DatamodelsFolderName, draft[0]);
        Assert.IsFalse(FileKeyUtility.Datamodel_IsHistoryFileKey(draft), "the draft is not a history entry");
        var when = new DateTime(2026, 9, 3, 12, 30, 45, DateTimeKind.Utc);
        var history = FileKeyUtility.Datamodel_GetHistoryFileKey(when);
        Assert.IsTrue(FileKeyUtility.Datamodel_IsHistoryFileKey(history));
        Assert.AreEqual(when, FileKeyUtility.Datamodel_GetHistoryDateTimeFromFileKey(history));
        CollectionAssert.Contains(FileKeyUtility.SystemFolderNames, FileKeyUtility.DatamodelsFolderName, "the folder must be listed, or history files are invisible to Search");
    }

    // ---- drafts and history ----

    [TestMethod]
    public void Drafts_SaveLoadPeekDelete() {
        var io = new IOProviderDisk(Path.Combine(_root, "db"));
        var drafts = new DatamodelDrafts(io);
        Assert.IsNull(drafts.LoadDraft());
        var model = libraryModel(DatamodelSourceType.JsonFile);
        var checksum = DatamodelJson.Checksum(initialized(model));
        drafts.SaveDraft(new DatamodelDraft { Model = model, Checksum = checksum, BaseChecksum = Guid.NewGuid(), Note = "first" });
        Assert.IsTrue(drafts.HasDraft);
        var peeked = drafts.PeekDraft()!;
        Assert.AreEqual(checksum, peeked.Checksum);
        Assert.AreEqual("first", peeked.Note);
        Assert.IsFalse(peeked.AwaitingRebuild);
        var loaded = drafts.LoadDraft()!;
        Assert.AreEqual(checksum, DatamodelJson.Checksum(initialized(loaded.Model)), "the model survives the envelope");
        Assert.AreEqual(1, loaded.Model.Sources.Count, "the draft carries its source list");
        Assert.AreEqual(sourceId, typeNamed(loaded.Model, "SlAuthor").DatamodelSourceId, "provenance is kept in a draft");
        drafts.DeleteDraft();
        Assert.IsNull(drafts.LoadDraft());
        Assert.IsFalse(drafts.HasDraft);
    }

    [TestMethod]
    public void History_SkipsUnchangedModels_AndKeepsAtMostFifty() {
        var io = new IOProviderDisk(Path.Combine(_root, "db"));
        var drafts = new DatamodelDrafts(io);
        var model = initialized(libraryModel(DatamodelSourceType.JsonFile));
        Assert.IsNotNull(drafts.Snapshot(model, "open"));
        Assert.IsNull(drafts.Snapshot(model, "open"), "the same model again is not a new entry");
        Assert.AreEqual(1, drafts.ListHistory().Count);
        var listed = drafts.ListHistory()[0];
        Assert.AreEqual("open", listed.Reason);
        Assert.AreEqual(2, listed.NodeTypes);
        Assert.AreEqual(1, listed.Relations);
        Assert.AreEqual(DatamodelJson.Checksum(model), listed.Checksum);
        Assert.IsNull(listed.Model, "the listing does not deserialize models");
        var full = drafts.LoadHistory(listed.Key)!;
        Assert.IsNotNull(full.Model);
        Assert.AreEqual(listed.Checksum, DatamodelJson.Checksum(initialized(full.Model!)));
        // fifty five different models: the oldest five go
        for (var i = 0; i < 55; i++) {
            var changed = copy(model);
            typeNamed(changed, "SlAuthor").Properties.Values.First(p => p.CodeName == "Name").IndexBoost = i + 1;
            changed.EnsureInitalization();
            Assert.IsNotNull(drafts.Snapshot(changed, "replaced"), "model " + i + " differs from the newest entry");
        }
        var history = drafts.ListHistory();
        Assert.AreEqual(DatamodelDrafts.HistoryRetention, history.Count);
        Assert.IsTrue(history[0].SavedUtc >= history[^1].SavedUtc, "newest first");
        Assert.IsTrue(drafts.DeleteHistory(history[^1].Key));
        Assert.AreEqual(DatamodelDrafts.HistoryRetention - 1, drafts.ListHistory().Count);
        Assert.IsFalse(drafts.DeleteHistory("datamodels/not.there.json"));
    }

    // ---- the write plan ----

    [TestMethod]
    public void Plan_JsonSource_RewritesTheFileHoldingAChangedType() {
        var folder = Path.Combine(_root, "Models", "Json");
        Directory.CreateDirectory(folder);
        var seed = libraryModel(DatamodelSourceType.JsonFile);
        File.WriteAllText(Path.Combine(folder, "library.json"), DatamodelJson.SerializeForSourceFile(seed, seed.NodeTypes.Keys, seed.Relations.Keys));
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.JsonFile, Filepath = "Models/Json" };
        var active = new Datamodel();
        DatamodelSourceLoader.Load(active, source, _root);
        active.EnsureInitalization();
        Assert.AreEqual("library.json", typeNamed(active, "SlAuthor").DatamodelSourceFilename);

        var draft = copy(active);
        var author = typeNamed(draft, "SlAuthor");
        var newProperty = new StringPropertyModel { Id = Guid.NewGuid(), CodeName = "Bio", IndexedByWords = true };
        author.Properties.Add(newProperty.Id, newProperty);
        draft.EnsureInitalization();

        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.AreEqual(0, plan.Issues.Count, string.Join("\n", plan.Issues.Select(i => i.Message)));
        Assert.IsFalse(plan.RequiresRebuild);
        Assert.IsFalse(plan.SettingsChange);
        var change = plan.Sources.Single(s => s.SourceId == sourceId);
        Assert.IsTrue(change.Writable);
        CollectionAssert.AreEquivalent(new[] { author.Id }, change.ChangedTypes);
        var file = plan.Files.Single();
        Assert.AreEqual(PlannedFileAction.Write, file.Action);
        Assert.AreEqual("library.json", file.RelativePath);
        Assert.IsTrue(file.Exists);
        Assert.IsTrue(file.Changed);
        var written = DatamodelJson.Deserialize(file.Content!);
        Assert.IsTrue(written.NodeTypes.Values.First(t => t.CodeName == "SlAuthor").Properties.ContainsKey(newProperty.Id), "the new property is in the file");
        Assert.AreEqual(0, written.Sources.Count, "a source file carries no source list");
        Assert.IsFalse(written.NodeTypes.ContainsKey(NodeConstants.BaseNodeTypeId) && file.Content!.Contains("INode"), "the base type is implied");

        // an unchanged draft plans nothing
        var same = DatamodelSourceWriter.Plan(active, initialized(active), _root, _ => null);
        Assert.AreEqual(0, same.Files.Count);
        Assert.IsFalse(same.Sources.Single().HasModelChanges);
    }

    [TestMethod]
    public void Plan_JsonSource_DeletesAFileWhoseTypesAreAllRemoved_AndAddsNewTypesInTheirOwnFile() {
        var folder = Path.Combine(_root, "Models", "Json");
        Directory.CreateDirectory(folder);
        var seed = libraryModel(DatamodelSourceType.JsonFile);
        var review = new Datamodel();
        review.CurrentSourceId = sourceId;
        review.Add<Relatude.SourceLoaderModels.JsonGen.SlReview>();
        File.WriteAllText(Path.Combine(folder, "library.json"), DatamodelJson.SerializeForSourceFile(seed, seed.NodeTypes.Keys, seed.Relations.Keys));
        File.WriteAllText(Path.Combine(folder, "review.json"), DatamodelJson.SerializeForSourceFile(review, review.NodeTypes.Keys, review.Relations.Keys));
        File.WriteAllText(Path.Combine(folder, "Tag.json"), "{ \"NodeTypes\": {}, \"Relations\": {} }"); // an empty model file that happens to carry a future type's name
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.JsonFile, Filepath = "Models/Json" };
        var active = new Datamodel();
        DatamodelSourceLoader.Load(active, source, _root);
        active.EnsureInitalization();

        var draft = copy(active);
        var reviewType = typeNamed(draft, "SlReview");
        draft.NodeTypes.Remove(reviewType.Id);
        var tag = new NodeTypeModel { Id = Guid.NewGuid(), CodeName = "Tag", Namespace = "Relatude.SourceLoaderModels", ModelType = ModelType.Class, DatamodelSourceId = sourceId };
        var tagName = new StringPropertyModel { Id = Guid.NewGuid(), CodeName = "Name", Indexed = true };
        tag.Properties.Add(tagName.Id, tagName);
        draft.NodeTypes.Add(tag.Id, tag);
        draft.EnsureInitalization();

        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.AreEqual(0, plan.Issues.Count, string.Join("\n", plan.Issues.Select(i => i.Message)));
        var change = plan.Sources.Single();
        CollectionAssert.AreEquivalent(new[] { tag.Id }, change.AddedTypes);
        CollectionAssert.AreEquivalent(new[] { reviewType.Id }, change.RemovedTypes);
        var delete = plan.Files.Single(f => f.Action == PlannedFileAction.Delete);
        Assert.AreEqual("review.json", delete.RelativePath, "the file that would put SlReview straight back is deleted");
        var write = plan.Files.Single(f => f.Action == PlannedFileAction.Write);
        Assert.AreEqual("Tag.model.json", write.RelativePath, "a file of the type's name exists and holds no model type of ours, so the new type gets a name of its own");
        Assert.IsFalse(write.Exists);
        Assert.IsTrue(plan.Files.All(f => f.RelativePath != "library.json"), "the untouched file is not in the plan");
    }

    [TestMethod]
    public void Plan_ReadOnlySource_ReportsEveryDifferenceAsAnError() {
        var active = initialized(libraryModel(DatamodelSourceType.AssemblyNameReference)); // no SourceCodePath: read only
        var draft = copy(active);
        var book = typeNamed(draft, "SlBook");
        book.Hidden = true;
        var extra = new NodeTypeModel { Id = Guid.NewGuid(), CodeName = "Shelf", Namespace = "Relatude.SourceLoaderModels", ModelType = ModelType.Class, DatamodelSourceId = sourceId };
        draft.NodeTypes.Add(extra.Id, extra);
        draft.EnsureInitalization();
        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.IsTrue(plan.HasErrors);
        Assert.AreEqual(2, plan.Issues.Count(i => i.Code == "read-only-source"), string.Join("\n", plan.Issues.Select(i => i.Message)));
        Assert.IsTrue(plan.Issues.Any(i => i.NodeTypeId == book.Id));
        Assert.IsTrue(plan.Issues.Any(i => i.NodeTypeId == extra.Id));
        Assert.AreEqual(0, plan.Files.Count, "nothing is planned for a source that cannot be written");
        var change = plan.Sources.Single();
        Assert.IsFalse(change.Writable);
        Assert.IsNotNull(change.ReadOnlyReason);
    }

    [TestMethod]
    public void Plan_CodeTypesAreReadOnly_AndTypesWithoutASourceAreErrors() {
        var active = initialized(libraryModel(DatamodelSourceType.JsonFile));
        var draft = copy(active);
        var orphan = new NodeTypeModel { Id = Guid.NewGuid(), CodeName = "Orphan", ModelType = ModelType.Class, DatamodelSourceId = Guid.NewGuid() };
        draft.NodeTypes.Add(orphan.Id, orphan);
        var fromCode = new NodeTypeModel { Id = Guid.NewGuid(), CodeName = "FromCode", ModelType = ModelType.Class, DatamodelSourceId = DatamodelSource.CodeSourceId };
        draft.NodeTypes.Add(fromCode.Id, fromCode);
        draft.EnsureInitalization();
        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.IsTrue(plan.Issues.Any(i => i.Code == "unknown-source" && i.NodeTypeId == orphan.Id));
        Assert.IsTrue(plan.Issues.Any(i => i.Code == "read-only-source" && i.NodeTypeId == fromCode.Id));
    }

    [TestMethod]
    public void Plan_CSharpSource_WritesCodeTheLoaderReadsBack() {
        var folder = Path.Combine(_root, "Models", "CSharp");
        Directory.CreateDirectory(folder);
        var seed = initialized(libraryModel(DatamodelSourceType.CSharpCodeFile));
        File.WriteAllText(Path.Combine(folder, "Library.cs"), ModelGen.GenerateCSharpModelCode(seed, true));
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.CSharpCodeFile, Filepath = "Models/CSharp" };
        var active = new Datamodel();
        DatamodelSourceLoader.Load(active, source, _root);
        active.EnsureInitalization();
        Assert.AreEqual("Library.cs", typeNamed(active, "SlAuthor").DatamodelSourceFilename);

        var draft = copy(active);
        var author = typeNamed(draft, "SlAuthor");
        var born = new IntegerPropertyModel { Id = Guid.NewGuid(), CodeName = "Born", Indexed = true };
        author.Properties.Add(born.Id, born);
        draft.EnsureInitalization();
        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.AreEqual(0, plan.Issues.Count, string.Join("\n", plan.Issues.Select(i => i.Message)));
        var file = plan.Files.Single();
        Assert.AreEqual("Library.cs", file.RelativePath);
        Assert.IsTrue(file.Content!.Contains("Born"), "the generated code declares the new property");

        // apply it and load the source again: the new property is there
        File.WriteAllText(file.Path, file.Content);
        var reloaded = new Datamodel();
        DatamodelSourceLoader.Load(reloaded, source, _root);
        reloaded.EnsureInitalization();
        Assert.IsTrue(typeNamed(reloaded, "SlAuthor").Properties.ContainsKey(born.Id));
        Assert.AreEqual(DatamodelJson.Checksum(draft), DatamodelJson.Checksum(reloaded), "the written code loads back as exactly the draft");
    }

    [TestMethod]
    public void Plan_AssemblySourceWithSourceCode_MapsTypesToTheirFiles_AndRequiresARebuild() {
        // a "project" folder holding the model classes as C# files, the way an application would
        var project = Path.Combine(_root, "MyApp");
        Directory.CreateDirectory(Path.Combine(project, "Models"));
        var seed = initialized(libraryModel(DatamodelSourceType.AssemblyNameReference));
        File.WriteAllText(Path.Combine(project, "Models", "Library.cs"), ModelGen.GenerateCSharpModelCode(seed, true));
        File.WriteAllText(Path.Combine(project, "Program.cs"), "namespace MyApp { public static class Program { public static void Main() { } } }");
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.AssemblyNameReference, Namespace = "Relatude.SourceLoaderModels", SourceCodePath = "MyApp" };
        var (writable, reason, rebuild) = DatamodelSourceWriter.Writability(source, _root);
        Assert.IsTrue(writable, reason);
        Assert.IsTrue(rebuild);

        var files = ModelSourceFiles.MapTypesToFiles(project);
        Assert.IsTrue(files.ContainsKey("Relatude.SourceLoaderModels.SlAuthor"));
        Assert.IsTrue(files.ContainsKey("MyApp.Program"));
        Assert.IsTrue(files["Relatude.SourceLoaderModels.SlAuthor"].EndsWith("Library.cs"));

        // the active model as the loader would stamp it: the assembly's types with the file they sit in
        var active = copy(seed);
        active.Sources[0].SourceCodePath = "MyApp";
        foreach (var t in active.NodeTypes.Values) if (t.DatamodelSourceId == sourceId) t.DatamodelSourceFilename = Path.Combine("Models", "Library.cs");
        foreach (var r in active.Relations.Values) if (r.DatamodelSourceId == sourceId) r.DatamodelSourceFilename = Path.Combine("Models", "Library.cs");
        active.EnsureInitalization();
        var draft = copy(active);
        var pages = new IntegerPropertyModel { Id = Guid.NewGuid(), CodeName = "Pages", Indexed = true };
        typeNamed(draft, "SlBook").Properties.Add(pages.Id, pages);
        var shelf = new NodeTypeModel { Id = Guid.NewGuid(), CodeName = "Shelf", Namespace = "Relatude.SourceLoaderModels", ModelType = ModelType.Class, DatamodelSourceId = sourceId };
        draft.NodeTypes.Add(shelf.Id, shelf);
        draft.EnsureInitalization();
        var unchangedSources = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.IsFalse(unchangedSources.SettingsChange, "the same source definitions mean no settings change");
        draft.Sources[0].Name = "Library (renamed)";
        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.AreEqual(0, plan.Issues.Count, string.Join("\n", plan.Issues.Select(i => i.Message)));
        Assert.IsTrue(plan.RequiresRebuild);
        Assert.IsTrue(plan.SettingsChange, "a renamed source is a settings change");
        var library = plan.Files.Single(f => f.RelativePath == Path.Combine("Models", "Library.cs"));
        Assert.IsTrue(library.Changed);
        var shelfFile = plan.Files.Single(f => f.NodeTypeIds.Contains(shelf.Id));
        Assert.AreEqual(Path.Combine("Models", "Shelf.cs"), shelfFile.RelativePath, "a new type goes next to the other types of its namespace");
        Assert.IsFalse(plan.Files.Any(f => f.RelativePath == "Program.cs"), "files that hold no model types are never touched");
    }

    // ---- validation helpers ----

    [TestMethod]
    public void Validator_IdentifierRule() {
        Assert.IsTrue(DatamodelValidator.IsValidIdentifier("Person"));
        Assert.IsTrue(DatamodelValidator.IsValidIdentifier("_x1"));
        Assert.IsFalse(DatamodelValidator.IsValidIdentifier("1Person"));
        Assert.IsFalse(DatamodelValidator.IsValidIdentifier("class"));
        Assert.IsFalse(DatamodelValidator.IsValidIdentifier("My Type"));
        Assert.IsFalse(DatamodelValidator.IsValidIdentifier(""));
    }

    // ---- the catalog ----

    [TestMethod]
    public void Catalog_EveryEntryNamesARealProperty_AndEveryPropertyIsCataloguedOrHidden() {
        foreach (var type in new[] { typeof(NodeTypeModel), typeof(RelationModel), typeof(DatamodelSource) }) {
            var names = type.GetProperties().Select(p => p.Name).ToHashSet();
            foreach (var path in DatamodelCatalog.CataloguedPaths(type)) Assert.IsTrue(names.Contains(path), type.Name + " has no property " + path);
            var covered = DatamodelCatalog.CataloguedPaths(type).Concat(DatamodelCatalog.HiddenPaths(type)).ToHashSet();
            var missing = type.GetProperties().Where(p => p.SetMethod?.IsPublic == true && !covered.Contains(p.Name)).Select(p => p.Name).ToArray();
            Assert.AreEqual(0, missing.Length, type.Name + " properties without a catalog entry: " + string.Join(", ", missing));
        }
        var propertyTypes = Enum.GetValues<PropertyType>().Where(pt => pt != PropertyType.Any).Select(PropertyModelJsonConverter.GetModelType).ToList();
        var allNames = propertyTypes.SelectMany(t => t.GetProperties().Select(p => p.Name)).ToHashSet();
        foreach (var path in DatamodelCatalog.CataloguedPaths(typeof(PropertyModel))) Assert.IsTrue(allNames.Contains(path), "no property model has a property " + path);
        var coveredProperties = DatamodelCatalog.CataloguedPaths(typeof(PropertyModel)).Concat(DatamodelCatalog.HiddenPaths(typeof(PropertyModel))).ToHashSet();
        var uncatalogued = propertyTypes.SelectMany(t => t.GetProperties()).Where(p => p.SetMethod?.IsPublic == true && !coveredProperties.Contains(p.Name)).Select(p => p.DeclaringType!.Name + "." + p.Name).Distinct().ToArray();
        Assert.AreEqual(0, uncatalogued.Length, "property model fields without a catalog entry: " + string.Join(", ", uncatalogued));
        Assert.IsNotNull(DatamodelCatalog.Schema, "the schema builds");
    }
}
