using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.ModelEditor;

namespace Relatude.Server;

/// <summary>
/// Where the data model editor puts the files of a runtime types source that names no path: a folder of
/// its own, relatude.data/modelsources/{name}, below the settings folder - and that renaming the source
/// does not move it away from its files.
/// </summary>
[TestClass]
public class RuntimeSourceFolderTests {
    string _root = "";
    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "SourceFolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_root, true); } catch { }
    }

    static readonly Guid sourceId = new("44444444-0000-0000-0000-000000000001");
    const string ns = "Relatude.RuntimeSourceFolderTests.Shop"; // no class anywhere: the store generates one

    static string draftWith(NodeStoreContainer c, Action<Datamodel> change) {
        var dm = DatamodelJson.Deserialize(c.DatamodelAsLoadedJson!);
        change(dm);
        return DatamodelJson.Serialize(dm);
    }

    [TestMethod]
    public async Task NewSourceWithoutPath_IsWrittenIntoItsOwnFolder_AndARenameKeepsReadingIt() {
        var host = TestServerHost.Start(_root, configure: s => s.ContainerSettings![0].DatamodelSources = []);
        try {
            var c = host.Server.Containers.Values.First();
            var drafts = new DatamodelDrafts(host.Server.GetIO(c.Settings.IoDatabase!.Value));
            var productId = Guid.NewGuid();

            // a new source with a type in it, the way the editor's "Add source" makes it: no Filepath
            var json = draftWith(c, dm => {
                dm.Sources.Add(new DatamodelSource { Id = sourceId, Name = "Shop Models", Type = DatamodelSourceType.RuntimeTypes });
                var product = new NodeTypeModel { Id = productId, CodeName = "Product", Namespace = ns, ModelType = ModelType.Class, DatamodelSourceId = sourceId };
                var name = new StringPropertyModel { Id = Guid.NewGuid(), CodeName = "Name" };
                product.Properties.Add(name.Id, name);
                dm.NodeTypes.Add(product.Id, product);
            });
            var result = new DatamodelActivator(host.Server, c, drafts).Activate(json, acceptWarnings: true, note: null);
            Assert.IsTrue(result.Activated, result.Message + string.Join("\n", result.Validation.Issues.Select(i => i.Code + ": " + i.Message)));
            Assert.AreEqual(true, result.ChecksumMatches, "the files read back as the draft");
            var folder = Path.Combine(_root, "relatude.data", "modelsources", "Shop Models");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "Product.json")), "written into the source's own folder: " + string.Join(", ", result.FilesWritten));
            var configured = c.Settings.DatamodelSources!.Single(s => s.Id == sourceId);
            Assert.IsNull(configured.Filepath, "the default folder is not written into the settings of a new source");
            Assert.AreEqual(0, c.Store!.QueryType(productId).Count(), "the reopened database has the type");

            // renamed: the folder is named after the old name, so the source is given it as its path
            var renamed = draftWith(c, dm => dm.Sources.Single(s => s.Id == sourceId).Name = "Catalog");
            var second = new DatamodelActivator(host.Server, c, drafts).Activate(renamed, acceptWarnings: true, note: null);
            Assert.IsTrue(second.Activated, second.Message + string.Join("\n", second.Validation.Issues.Select(i => i.Code + ": " + i.Message)));
            Assert.AreEqual(true, second.ChecksumMatches, "the type still comes back from its files");
            configured = c.Settings.DatamodelSources!.Single(s => s.Id == sourceId);
            Assert.AreEqual("Catalog", configured.Name);
            Assert.AreEqual("relatude.data/modelsources/Shop Models", configured.Filepath);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "relatude.data", "modelsources", "Catalog")), "nothing was written under the new name");
            Assert.AreEqual(0, c.Store!.QueryType(productId).Count());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public void KeepDefaultPaths_GivesOnlyRenamedSourcesWithoutAPathTheirFolder() {
        DatamodelSource runtime(string name, string? filepath = null, Guid? io = null) => new() {
            Id = Guid.NewGuid(), Name = name, Type = DatamodelSourceType.RuntimeTypes, Filepath = filepath, FileIO = io,
        };
        var renamed = runtime("Shop");
        var same = runtime("Blog");
        var withPath = runtime("Docs", "Models/Json");
        var throughProvider = runtime("Remote", io: Guid.NewGuid());
        var compiled = new DatamodelSource { Id = Guid.NewGuid(), Name = "Code", Type = DatamodelSourceType.CompiledTypes, Namespace = "X" };
        var before = DatamodelSourceWriter.DefaultPaths([renamed, same, withPath, throughProvider, compiled]);
        CollectionAssert.AreEquivalent(new[] { renamed.Id, same.Id }, before.Keys.ToArray(), "only sources reading from their default folder");

        renamed.Name = "Store";
        withPath.Name = "Manuals";
        throughProvider.Name = "Far away";
        compiled.Name = "Compiled";
        var added = runtime("Added");
        var kept = DatamodelSourceWriter.KeepDefaultPaths(before, [renamed, same, withPath, throughProvider, compiled, added]);

        CollectionAssert.AreEqual(new[] { renamed }, kept);
        Assert.AreEqual("relatude.data/modelsources/Shop", renamed.Filepath);
        Assert.IsNull(same.Filepath, "a source that was not renamed keeps reading its default folder");
        Assert.AreEqual("Models/Json", withPath.Filepath);
        Assert.IsNull(throughProvider.Filepath);
        Assert.IsNull(added.Filepath, "a new source gets the folder of the name it is given");
    }
}
