using System.Text.Json;
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
        var model = libraryModel(DatamodelSourceType.RuntimeTypes);
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
    public void Drafts_AwaitingRebuild_CarriesTheFilesItWrote() {
        var io = new IOProviderDisk(Path.Combine(_root, "db"));
        var drafts = new DatamodelDrafts(io);
        var model = libraryModel(DatamodelSourceType.CompiledTypes);
        drafts.SaveDraft(new DatamodelDraft { Model = model, AwaitingRebuild = true, AwaitingRebuildSinceUtc = DateTime.UtcNow, FilesWritten = [@"C:\app\Models\SlAuthor.cs"], FilesDeleted = [@"C:\app\Models\Old.cs"] });
        var peeked = drafts.PeekDraft()!;
        Assert.IsTrue(peeked.AwaitingRebuild);
        CollectionAssert.AreEqual(new[] { @"C:\app\Models\SlAuthor.cs" }, peeked.FilesWritten);
        CollectionAssert.AreEqual(new[] { @"C:\app\Models\Old.cs" }, peeked.FilesDeleted);
        var loaded = drafts.LoadDraft()!;
        CollectionAssert.AreEqual(peeked.FilesWritten, loaded.FilesWritten);
        CollectionAssert.AreEqual(peeked.FilesDeleted, loaded.FilesDeleted);
    }

    [TestMethod]
    public void Drafts_LoadRecomputesTheChecksum_PeekReportsTheSavedOne() {
        var io = new IOProviderDisk(Path.Combine(_root, "db"));
        var drafts = new DatamodelDrafts(io);
        var model = libraryModel(DatamodelSourceType.RuntimeTypes);
        var stale = Guid.NewGuid(); // what an older way of making checksums might have written
        drafts.SaveDraft(new DatamodelDraft { Model = model, Checksum = stale });
        Assert.AreEqual(stale, drafts.PeekDraft()!.Checksum);
        Assert.AreEqual(DatamodelDrafts.ChecksumOf(model), drafts.LoadDraft()!.Checksum);
        Assert.AreEqual(DatamodelJson.Checksum(initialized(model)), DatamodelDrafts.ChecksumOf(model), "ChecksumOf is the checksum of the initialized model");
    }

    /// <summary>
    /// The situation from the bug report: a new type activated into compiled code, the application rebuilt.
    /// The editor had appended the type at the end of the type list; the loader placed it after the other
    /// types of its source. Same model, another order - the draft must be recognized as active and removed.
    /// </summary>
    [TestMethod]
    public void Drafts_AwaitingRebuild_IsRemovedWhenTheStoreOpensWithItsModel_InAnotherOrder() {
        var io = new IOProviderDisk(Path.Combine(_root, "db"));
        var drafts = new DatamodelDrafts(io);
        var draftModel = libraryModel(DatamodelSourceType.CompiledTypes); // raw, as the editor saves it
        drafts.SaveDraft(new DatamodelDraft { Model = draftModel, AwaitingRebuild = true, AwaitingRebuildSinceUtc = DateTime.UtcNow });
        var opened = copy(draftModel);
        reverse(opened.NodeTypes);
        reverse(typeNamed(opened, "SlBook").Properties);
        opened.EnsureInitalization();
        Assert.AreNotEqual(JsonSerializer.Serialize(initialized(draftModel), DatamodelJson.CompareOptions), JsonSerializer.Serialize(opened, DatamodelJson.CompareOptions), "the plain serializations differ in order");
        Assert.IsTrue(drafts.RemoveIfActivated(drafts.LoadDraft()!, opened), "same model in another order: the rebuild has happened");
        Assert.IsFalse(drafts.HasDraft);

        // a model that really differs keeps the draft waiting
        drafts.SaveDraft(new DatamodelDraft { Model = draftModel, AwaitingRebuild = true, AwaitingRebuildSinceUtc = DateTime.UtcNow });
        var other = initialized(draftModel);
        typeNamed(other, "SlBook").Properties.Values.First().IndexBoost = 3;
        Assert.IsFalse(drafts.RemoveIfActivated(drafts.LoadDraft()!, other));
        Assert.IsTrue(drafts.HasDraft);

        // a draft that is not waiting for a rebuild is never removed this way, however equal
        drafts.SaveDraft(new DatamodelDraft { Model = draftModel, AwaitingRebuild = false });
        Assert.IsFalse(drafts.RemoveIfActivated(drafts.LoadDraft()!, initialized(draftModel)));
        Assert.IsTrue(drafts.HasDraft);
    }
    static void reverse<T>(Dictionary<Guid, T> dictionary) {
        var reversed = dictionary.Reverse().ToList();
        dictionary.Clear();
        foreach (var (key, value) in reversed) dictionary.Add(key, value);
    }

    // ---- checksums ----

    [TestMethod]
    public void Checksum_DoesNotDependOnInsertionOrder_OnlyOnContent() {
        var a = initialized(libraryModel(DatamodelSourceType.RuntimeTypes));
        var b = copy(a);
        reverse(b.NodeTypes);
        reverse(b.Relations);
        foreach (var t in b.NodeTypes.Values) reverse(t.Properties);
        b.EnsureInitalization();
        Assert.AreEqual(DatamodelJson.Checksum(a), DatamodelJson.Checksum(b));
        Assert.AreEqual(DatamodelSourceWriter.Fingerprint(typeNamed(a, "SlBook")), DatamodelSourceWriter.Fingerprint(typeNamed(b, "SlBook")), "the per type fingerprint is canonical too");
        typeNamed(b, "SlBook").Properties.Values.First().IndexBoost = 7;
        Assert.AreNotEqual(DatamodelJson.Checksum(a), DatamodelJson.Checksum(b), "a real difference is still a difference");
    }

    [TestMethod]
    public void CanonicalJson_TreatsTypeIdListsAsSets() {
        var g1 = new Guid("11111111-0000-0000-0000-000000000001");
        var g2 = new Guid("11111111-0000-0000-0000-000000000002");
        string canonical(NodeTypeModel t) => DatamodelJson.CanonicalJson(t, DatamodelJson.CompareOptions);
        Assert.AreEqual(canonical(new NodeTypeModel { Id = g1, Parents = [g1, g2] }), canonical(new NodeTypeModel { Id = g1, Parents = [g2, g1] }), "reflection lists the base class first, the editor keeps the order picked");
        Assert.AreNotEqual(canonical(new NodeTypeModel { Id = g1, Parents = [g1, g2] }), canonical(new NodeTypeModel { Id = g1, Parents = [g1] }));
        Assert.IsFalse(canonical(new NodeTypeModel { Id = g1 }).Contains('\n'), "compact: whitespace would only add to what is hashed");
    }

    [TestMethod]
    public void History_SkipsUnchangedModels_AndKeepsAtMostFifty() {
        var io = new IOProviderDisk(Path.Combine(_root, "db"));
        var drafts = new DatamodelDrafts(io);
        var model = initialized(libraryModel(DatamodelSourceType.RuntimeTypes));
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
        var seed = libraryModel(DatamodelSourceType.RuntimeTypes);
        File.WriteAllText(Path.Combine(folder, "library.json"), DatamodelJson.SerializeForSourceFile(seed, seed.NodeTypes.Keys, seed.Relations.Keys));
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.RuntimeTypes, Filepath = "Models/Json" };
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
        var seed = libraryModel(DatamodelSourceType.RuntimeTypes);
        var review = new Datamodel();
        review.CurrentSourceId = sourceId;
        review.Add<Relatude.SourceLoaderModels.JsonGen.SlReview>();
        File.WriteAllText(Path.Combine(folder, "library.json"), DatamodelJson.SerializeForSourceFile(seed, seed.NodeTypes.Keys, seed.Relations.Keys));
        File.WriteAllText(Path.Combine(folder, "review.json"), DatamodelJson.SerializeForSourceFile(review, review.NodeTypes.Keys, review.Relations.Keys));
        File.WriteAllText(Path.Combine(folder, "Tag.json"), "{ \"NodeTypes\": {}, \"Relations\": {} }"); // an empty model file that happens to carry a future type's name
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.RuntimeTypes, Filepath = "Models/Json" };
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
        var active = initialized(libraryModel(DatamodelSourceType.CompiledTypes)); // no SourceCodePath: read only
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
        var active = initialized(libraryModel(DatamodelSourceType.RuntimeTypes));
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

    /// <summary>
    /// What the dry run leans on: model code generated from a model, compiled on its own, comes back as
    /// exactly that model. C# files are not a source kind any more - this is the path DatamodelValidator
    /// takes to prove the code it is about to write into a compiled source is right, without waiting for
    /// the application to be rebuilt.
    /// </summary>
    [TestMethod]
    public void LoadCSharpFiles_ReadsBackTheModelTheCodeWasGeneratedFrom() {
        var folder = Path.Combine(_root, "Models", "CSharp");
        Directory.CreateDirectory(folder);
        var seed = initialized(libraryModel(DatamodelSourceType.CompiledTypes));
        var author = typeNamed(seed, "SlAuthor");
        var born = new IntegerPropertyModel { Id = Guid.NewGuid(), CodeName = "Born", Indexed = true, NodeType = author.Id };
        author.Properties.Add(born.Id, born);
        seed.EnsureInitalization();
        var code = ModelGen.GenerateCSharpModelCode(seed, true);
        Assert.IsTrue(code.Contains("Born"), "the generated code declares the property");
        File.WriteAllText(Path.Combine(folder, "Library.cs"), code);

        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.RuntimeTypes, Filepath = "Models/CSharp" };
        var reloaded = new Datamodel();
        DatamodelSourceLoader.LoadCSharpFiles(reloaded, source, _root);
        reloaded.EnsureInitalization();
        Assert.AreEqual("Library.cs", typeNamed(reloaded, "SlAuthor").DatamodelSourceFilename);
        Assert.IsTrue(typeNamed(reloaded, "SlAuthor").Properties.ContainsKey(born.Id));
        Assert.AreEqual(DatamodelJson.Checksum(seed), DatamodelJson.Checksum(reloaded), "the written code loads back as exactly the model");
    }

    [TestMethod]
    public void Plan_AssemblySourceWithSourceCode_MapsTypesToTheirFiles_AndRequiresARebuild() {
        // a "project" folder holding the model classes as C# files, the way an application would
        var project = Path.Combine(_root, "MyApp");
        Directory.CreateDirectory(Path.Combine(project, "Models"));
        var seed = initialized(libraryModel(DatamodelSourceType.CompiledTypes));
        File.WriteAllText(Path.Combine(project, "Models", "Library.cs"), ModelGen.GenerateCSharpModelCode(seed, true));
        File.WriteAllText(Path.Combine(project, "Program.cs"), "namespace MyApp { public static class Program { public static void Main() { } } }");
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.CompiledTypes, Namespace = "Relatude.SourceLoaderModels", SourceCodePath = "MyApp" };
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

    [TestMethod]
    public void Plan_GeneratedFolder_HoldsExactlyTheGeneratedFiles_AndWarnsBeforeDeletingHandWrittenOnes() {
        var project = Path.Combine(_root, "MyApp", "Models");
        Directory.CreateDirectory(Path.Combine(project, "Sub"));
        var seed = initialized(libraryModel(DatamodelSourceType.CompiledTypes));
        File.WriteAllText(Path.Combine(project, "Library.cs"), ModelGen.GenerateCSharpModelCode(seed, true)); // by hand: no marker
        File.WriteAllText(Path.Combine(project, "Sub", "Helper.cs"), "namespace MyApp { public static class Helper { } }");
        File.WriteAllText(Path.Combine(project, "Stale.cs"), ModelGen.AutoGeneratedHeader("Library") + "// left over from an earlier activation");
        var active = copy(seed);
        active.Sources[0].SourceCodePath = Path.Combine("MyApp", "Models");
        active.Sources[0].GenerateModelFile = true;
        foreach (var t in active.NodeTypes.Values) if (t.DatamodelSourceId == sourceId) t.DatamodelSourceFilename = "Library.cs";
        foreach (var r in active.Relations.Values) if (r.DatamodelSourceId == sourceId) r.DatamodelSourceFilename = "Library.cs";
        active.EnsureInitalization();
        Assert.IsTrue(DatamodelSourceWriter.IsGeneratedFolder(active.Sources[0]));

        // no model change at all: the folder is still brought into shape
        var draft = copy(active);
        draft.EnsureInitalization();
        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.IsFalse(plan.HasErrors, string.Join("\n", plan.Issues.Select(i => i.Message)));
        var writes = plan.Files.Where(f => f.Action == PlannedFileAction.Write).ToList();
        CollectionAssert.AreEquivalent(new[] { "SlAuthor.cs", "SlBook.cs", "SlBooksRel.cs" }, writes.Select(f => f.RelativePath).ToArray(), "one file per type and relation, stamped file names ignored");
        Assert.IsTrue(writes.All(f => f.Changed && !f.Exists && !f.HandWritten && ModelGen.IsAutoGenerated(f.Content!)), "every generated file starts with the marker");
        StringAssert.Contains(writes[0].Content!, "generated again");
        var deletes = plan.Files.Where(f => f.Action == PlannedFileAction.Delete).ToDictionary(f => f.RelativePath);
        CollectionAssert.AreEquivalent(new[] { "Library.cs", "Stale.cs", Path.Combine("Sub", "Helper.cs") }, deletes.Keys.ToArray(), "everything else in the folder goes, however deep");
        Assert.IsTrue(deletes["Library.cs"].HandWritten);
        Assert.IsTrue(deletes[Path.Combine("Sub", "Helper.cs")].HandWritten);
        Assert.IsFalse(deletes["Stale.cs"].HandWritten, "a file generated earlier goes without asking");
        var warning = plan.Issues.Single(i => i.Code == "hand-written-files");
        Assert.AreEqual(IssueSeverity.Warning, warning.Severity, "a warning: the activation needs it accepted");
        StringAssert.Contains(warning.Message, "Library.cs");
        StringAssert.Contains(warning.Message, "Helper.cs");
        Assert.IsFalse(warning.Message.Contains("Stale.cs"));
        Assert.IsTrue(plan.RequiresRebuild, "the compiled folder changes, so the application has to be rebuilt");

        // carry the plan out the way the activator does: deletes first, then writes
        foreach (var f in plan.Files.Where(f => f.Changed).OrderBy(f => f.Action == PlannedFileAction.Delete ? 0 : 1)) {
            if (f.Action == PlannedFileAction.Delete) File.Delete(f.Path);
            else File.WriteAllText(f.Path, f.Content);
        }
        CollectionAssert.AreEquivalent(new[] { "SlAuthor.cs", "SlBook.cs", "SlBooksRel.cs" },
            Directory.GetFiles(project, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(project, f)).ToArray());
        // what the folder now says is what the draft says
        var reloaded = new Datamodel();
        DatamodelSourceLoader.LoadCSharpFiles(reloaded, new DatamodelSource { Id = sourceId, Type = DatamodelSourceType.RuntimeTypes, Filepath = project, Namespace = "Relatude.SourceLoaderModels" }, _root);
        reloaded.EnsureInitalization();
        foreach (var t in draft.NodeTypes.Values.Where(t => t.DatamodelSourceId == sourceId))
            Assert.AreEqual(DatamodelSourceWriter.Fingerprint(t), DatamodelSourceWriter.Fingerprint(reloaded.NodeTypes[t.Id]), t.CodeName);
        foreach (var r in draft.Relations.Values.Where(r => r.DatamodelSourceId == sourceId))
            Assert.AreEqual(DatamodelSourceWriter.Fingerprint(r), DatamodelSourceWriter.Fingerprint(reloaded.Relations[r.Id]), r.CodeName);

        // a second pass over the generated folder has nothing to do and nothing to warn about
        var again = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.AreEqual(0, again.Issues.Count, string.Join("\n", again.Issues.Select(i => i.Message)));
        Assert.IsFalse(again.Files.Any(f => f.Changed));
        Assert.IsFalse(again.RequiresRebuild);

        // a file dropped into the folder later is deleted at the next activation, with the same warning
        File.WriteAllText(Path.Combine(project, "Notes.txt"), "remember to ...");
        var stray = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        var strayDelete = stray.Files.Single(f => f.Changed);
        Assert.AreEqual(PlannedFileAction.Delete, strayDelete.Action);
        Assert.AreEqual("Notes.txt", strayDelete.RelativePath);
        Assert.IsTrue(strayDelete.HandWritten);
        Assert.IsTrue(stray.Issues.Any(i => i.Code == "hand-written-files"));
        Assert.IsTrue(stray.RequiresRebuild, "a deleted file may have declared a type the running application still has");
    }

    [TestMethod]
    public void Plan_GeneratedFolder_IsWritableBeforeItExists_AndNamesClashingTypesByFullName() {
        var source = new DatamodelSource { Id = sourceId, Name = "Library", Type = DatamodelSourceType.CompiledTypes, Namespace = "Relatude.SourceLoaderModels", SourceCodePath = "NotThereYet", GenerateModelFile = true };
        var (writable, reason, rebuild) = DatamodelSourceWriter.Writability(source, _root);
        Assert.IsTrue(writable, reason);
        Assert.IsTrue(rebuild);
        source.GenerateModelFile = false;
        Assert.IsFalse(DatamodelSourceWriter.Writability(source, _root).writable, "a folder edited in place has to exist");

        var active = initialized(libraryModel(DatamodelSourceType.CompiledTypes));
        active.Sources[0].SourceCodePath = "NotThereYet";
        active.Sources[0].GenerateModelFile = true;
        var draft = copy(active);
        var twin = new NodeTypeModel { Id = Guid.NewGuid(), CodeName = "SlBook", Namespace = "Other.Models", ModelType = ModelType.Class, DatamodelSourceId = sourceId };
        draft.NodeTypes.Add(twin.Id, twin);
        draft.EnsureInitalization();
        var plan = DatamodelSourceWriter.Plan(active, draft, _root, _ => null);
        Assert.IsFalse(plan.HasErrors, string.Join("\n", plan.Issues.Select(i => i.Message)));
        CollectionAssert.AreEquivalent(new[] { "SlAuthor.cs", "Relatude.SourceLoaderModels.SlBook.cs", "Other.Models.SlBook.cs", "SlBooksRel.cs" }, plan.Files.Select(f => f.RelativePath).ToArray());
        Assert.IsTrue(plan.Files.All(f => f.Action == PlannedFileAction.Write && f.Changed));
        Assert.IsFalse(plan.Issues.Any(i => i.Code == "hand-written-files"), "a folder that is not there has nothing to warn about");
    }

    [TestMethod]
    public void SourceType_ReadsTheOldNames_AndRefusesTheRemovedOne() {
        // every name the kind now called CompiledTypes has been written under
        Assert.AreEqual(DatamodelSourceType.CompiledTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"AssemblyNameReference\"}")!.Type, "settings files written before the rename keep loading");
        Assert.AreEqual(DatamodelSourceType.CompiledTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"typereference\"}")!.Type);
        Assert.AreEqual(DatamodelSourceType.CompiledTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":0}")!.Type);
        // and the file kinds, which are all one kind now: model files holding JSON
        Assert.AreEqual(DatamodelSourceType.RuntimeTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":3}")!.Type);
        var oldJson = System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"JsonFile\",\"Filepath\":\"Models/Json\"}")!;
        Assert.AreEqual(DatamodelSourceType.RuntimeTypes, oldJson.Type);
        Assert.AreEqual("Models/Json", oldJson.Filepath);
        var oldCSharpFiles = System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"type\":\"CSharpCodeFile\",\"namespace\":\"X\",\"enabled\":false}", Relatude.DB.NodeServer.LocalSettingsLoaderFile.JsonOptions)!;
        Assert.AreEqual(DatamodelSourceType.RuntimeTypes, oldCSharpFiles.Type);
        Assert.AreEqual("X", oldCSharpFiles.Namespace);
        Assert.IsFalse(oldCSharpFiles.Enabled);
        // the removed FileFormat is read over, not choked on
        Assert.AreEqual(DatamodelSourceType.RuntimeTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"TextFiles\",\"FileFormat\":\"CSharpCode\"}")!.Type);

        var roundTrip = new DatamodelSource { Id = Guid.NewGuid(), Name = "T", Type = DatamodelSourceType.RuntimeTypes, Filepath = "Models/Json", FileIO = null, GenerateModelFile = true, Enabled = false, Color = "#2f7fd6" };
        var json = System.Text.Json.JsonSerializer.Serialize(roundTrip, DatamodelJson.Options);
        StringAssert.Contains(json, "\"RuntimeTypes\"");
        Assert.IsFalse(json.Contains("FileFormat"), "the format that chose between JSON and C# files is gone");
        var back = System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>(json, DatamodelJson.Options)!;
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(roundTrip, DatamodelJson.Options), System.Text.Json.JsonSerializer.Serialize(back, DatamodelJson.Options), "every property survives the converter");
        Assert.AreEqual("#2f7fd6", back.Color, "the colour of a source is one of them");
        Assert.IsNull(System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"CompiledTypes\"}")!.Color, "a source written before there was a colour has none, and takes the palette's");
        var error = Assert.ThrowsException<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"TypeNameReference\"}"));
        StringAssert.Contains(error.Message, "CompiledTypes", "the message says what to use instead");
        StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(new DatamodelSource { Type = DatamodelSourceType.CompiledTypes }), "\"CompiledTypes\"");
        StringAssert.Contains(DatamodelJson.Serialize(libraryModel(DatamodelSourceType.CompiledTypes)), "\"CompiledTypes\"");
        Assert.IsFalse(Enum.IsDefined(typeof(DatamodelSourceType), 1), "the removed value's number is not reused");
        Assert.IsFalse(Enum.IsDefined(typeof(DatamodelSourceType), 3), "nor the C# file kind's");
        // the serializer options the settings file and the model files are read with carry a JsonStringEnumConverter,
        // which outranks a converter on the enum type - the property attribute is what makes the old name load there
        var withStringEnums = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        withStringEnums.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        Assert.AreEqual(DatamodelSourceType.CompiledTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"AssemblyNameReference\"}", withStringEnums)!.Type);
        Assert.AreEqual(DatamodelSourceType.CompiledTypes, System.Text.Json.JsonSerializer.Deserialize<DatamodelSource>("{\"Type\":\"AssemblyNameReference\"}", Relatude.DB.NodeServer.LocalSettingsLoaderFile.JsonOptions)!.Type, "the settings file reader");
        var legacyModel = DatamodelJson.Serialize(libraryModel(DatamodelSourceType.CompiledTypes)).Replace("\"CompiledTypes\"", "\"AssemblyNameReference\"");
        Assert.AreEqual(DatamodelSourceType.CompiledTypes, DatamodelJson.Deserialize(legacyModel).Sources[0].Type, "history and draft envelopes written before the rename");
    }

    /// <summary>
    /// What a settings file written before the kinds were cut down turns into: compiled sources renamed,
    /// the file based kinds and the reserved Code kind dropped, and the removed FileFormat gone from
    /// every source. A file already on the new names is left exactly as it is.
    /// </summary>
    [TestMethod]
    public void SettingsMigration_RenamesCompiledSources_AndDropsTheRemovedKinds() {
        const string legacy = """
            {
              "ContainerSettings": [
                {
                  "DatamodelSources": [
                    { "Id": "11111111-0000-0000-0000-000000000001", "Name": "Model", "Type": "TypeReference", "Namespace": "MyApp.Models", "FileFormat": "Json" },
                    { "Id": "11111111-0000-0000-0000-000000000002", "Name": "Old assembly", "Type": "AssemblyNameReference", "Namespace": "MyApp.More" },
                    { "Id": "11111111-0000-0000-0000-000000000003", "Name": "Json files", "Type": "TextFiles", "FileFormat": "Json", "Filepath": "Models/Json" },
                    { "Id": "11111111-0000-0000-0000-000000000004", "Name": "C# files", "Type": "CSharpCodeFile", "Filepath": "Models/CSharp" },
                    { "Id": "11111111-0000-0000-0000-000000000005", "Name": "Numbered", "Type": 2 },
                    { "id": "11111111-0000-0000-0000-000000000006", "name": "camelCased", "type": "typeReference", "fileFormat": "Json" },
                    { "Id": "00000000-0000-0000-0000-00000000c0de", "Name": "Code", "Type": "Code" }
                  ]
                }
              ]
            }
            """;
        // parsed the way the settings file reader parses it: names are matched without regard to case,
        // since settings files have been written both camelCased and not
        var root = System.Text.Json.Nodes.JsonNode.Parse(legacy,
            new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = true }) as System.Text.Json.Nodes.JsonObject;
        Assert.IsTrue(Relatude.DB.NodeServer.LocalSettingsLoaderFile.MigrateLegacyDatamodelSources(root!));
        var settings = System.Text.Json.JsonSerializer.Deserialize<Relatude.DB.NodeServer.Settings.RelatudeDBServerSettings>(
            root!.ToJsonString(), Relatude.DB.NodeServer.LocalSettingsLoaderFile.JsonOptions)!;
        var sources = settings.ContainerSettings![0].DatamodelSources!;
        CollectionAssert.AreEqual(new[] { "Model", "Old assembly", "camelCased" }, sources.Select(s => s.Name).ToArray(),
            "the file based kinds and the reserved Code kind leave the settings file");
        Assert.IsTrue(sources.All(s => s.Type == DatamodelSourceType.CompiledTypes));
        Assert.AreEqual("MyApp.Models", sources[0].Namespace, "the rest of a renamed source is untouched");
        Assert.IsFalse(root!.ToJsonString().Contains("FileFormat", StringComparison.OrdinalIgnoreCase));
        // a camelCased source is rewritten in place rather than gaining a second kind under another casing
        var camelCased = (System.Text.Json.Nodes.JsonObject)(root["ContainerSettings"]![0]!["DatamodelSources"] as System.Text.Json.Nodes.JsonArray)![2]!;
        Assert.AreEqual(1, camelCased.Count(kv => string.Equals(kv.Key, "Type", StringComparison.OrdinalIgnoreCase)));

        // nothing to do twice, and a file already on the new names is left alone
        Assert.IsFalse(Relatude.DB.NodeServer.LocalSettingsLoaderFile.MigrateLegacyDatamodelSources(root!));
        var current = System.Text.Json.Nodes.JsonNode.Parse("""
            { "ContainerSettings": [ { "DatamodelSources": [ { "Id": "11111111-0000-0000-0000-000000000009", "Type": "RuntimeTypes" } ] } ] }
            """) as System.Text.Json.Nodes.JsonObject;
        Assert.IsFalse(Relatude.DB.NodeServer.LocalSettingsLoaderFile.MigrateLegacyDatamodelSources(current!));
        Assert.AreEqual(1, (current!["ContainerSettings"]![0]!["DatamodelSources"] as System.Text.Json.Nodes.JsonArray)!.Count,
            "a runtime source written under the new name stays");
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
            // instance properties only: a static flag (DatamodelSource.AutoDeduceRelations) is process wide, not a setting of one source
            var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var names = properties.Select(p => p.Name).ToHashSet();
            foreach (var path in DatamodelCatalog.CataloguedPaths(type)) Assert.IsTrue(names.Contains(path), type.Name + " has no property " + path);
            var covered = DatamodelCatalog.CataloguedPaths(type).Concat(DatamodelCatalog.HiddenPaths(type)).ToHashSet();
            var missing = properties.Where(p => p.SetMethod?.IsPublic == true && !covered.Contains(p.Name)).Select(p => p.Name).ToArray();
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
