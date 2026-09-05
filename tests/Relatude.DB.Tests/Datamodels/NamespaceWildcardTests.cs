using Relatude.DB.Datamodels;
using Relatude.SourceLoaderModels;

namespace Relatude.Datamodels;

/// <summary>
/// The wildcard form of a source namespace (September 2026): <c>*</c> stands for any run of characters,
/// and a trailing <c>.*</c> also takes the namespace itself. Matching is
/// <see cref="DatamodelSource.NamespaceMatches"/>; the loader and <c>AddAssembly</c> go through it.
/// </summary>
[TestClass]
public class NamespaceWildcardTests {
    [TestMethod]
    public void ExactPattern_MatchesOnlyItself() {
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp.Models", "MyApp.Models"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.Models", "MyApp.Models.Sub"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.Models", "MyApp"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.Models", "myapp.models"), "namespaces are case sensitive");
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.Models", null));
        Assert.IsFalse(DatamodelSource.NamespaceMatches(null, "MyApp.Models"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("", "MyApp.Models"));
    }
    [TestMethod]
    public void TrailingDotStar_TakesTheNamespaceAndEverythingUnderIt() {
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp.Models.*", "MyApp.Models"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp.Models.*", "MyApp.Models.Sub"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp.Models.*", "MyApp.Models.Sub.Deeper"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.Models.*", "MyApp.ModelsX"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.Models.*", "MyApp"));
    }
    [TestMethod]
    public void StarInTheMiddle_MatchesAnyRun() {
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp.*.Models", "MyApp.Web.Models"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp.*.Models", "MyApp.Web.Admin.Models"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("MyApp.*.Models", "MyApp.Models"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("*.Models", "A.B.Models"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("*", "Anything.At.All"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp*", "MyApp"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("MyApp*", "MyAppX.Models"));
        Assert.IsTrue(DatamodelSource.NamespaceMatches("A*B*C", "AxxByyC"));
        Assert.IsFalse(DatamodelSource.NamespaceMatches("A*B*C", "AxxByy"));
    }
    [TestMethod]
    public void NamespaceBase_IsWhereANewTypeGoes() {
        Assert.AreEqual("MyApp.Models", DatamodelSource.NamespaceBase("MyApp.Models"));
        Assert.AreEqual("MyApp.Models", DatamodelSource.NamespaceBase("MyApp.Models.*"));
        Assert.AreEqual("MyApp", DatamodelSource.NamespaceBase("MyApp.*.Models"));
        Assert.AreEqual("MyApp", DatamodelSource.NamespaceBase("MyApp*"));
        Assert.IsNull(DatamodelSource.NamespaceBase("*.Models"));
        Assert.IsNull(DatamodelSource.NamespaceBase(""));
        Assert.IsNull(DatamodelSource.NamespaceBase(null));
        Assert.IsTrue(DatamodelSource.HasWildcard("A.*"));
        Assert.IsFalse(DatamodelSource.HasWildcard("A.B"));
    }

    [TestMethod]
    public void AddAssembly_WithWildcard_TakesTheSubNamespacesToo() {
        var assembly = typeof(SlAuthor).Assembly;
        var exact = new Datamodel();
        exact.AddAssembly(assembly, "Relatude.SourceLoaderModels");
        Assert.IsTrue(exact.NodeTypes.Values.Any(t => t.FullName == "Relatude.SourceLoaderModels.SlAuthor"));
        Assert.IsFalse(exact.NodeTypes.Values.Any(t => t.FullName == "Relatude.SourceLoaderModels.JsonGen.SlReview"), "an exact namespace does not take the namespaces under it");

        var wild = new Datamodel();
        wild.AddAssembly(assembly, "Relatude.SourceLoaderModels.*");
        Assert.IsTrue(wild.NodeTypes.Values.Any(t => t.FullName == "Relatude.SourceLoaderModels.SlAuthor"), "a trailing .* still takes the namespace itself");
        Assert.IsTrue(wild.NodeTypes.Values.Any(t => t.FullName == "Relatude.SourceLoaderModels.JsonGen.SlReview"));
        Assert.IsTrue(wild.NodeTypes.Values.Any(t => t.FullName == "Relatude.SourceLoaderModels.JsonPoco.SlReview"));
        Assert.IsTrue(wild.Relations.Values.Any(r => r.CodeName == "SlBooksRel"));
    }
    [TestMethod]
    public void Loader_WithWildcardNamespace_LoadsAndTagsEveryMatchingType() {
        var root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "NsWildcard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var source = new DatamodelSource {
                Id = Guid.NewGuid(), Name = "Wild", Type = DatamodelSourceType.TypeReference,
                Reference = typeof(SlAuthor).Assembly.GetName().Name, Namespace = "Relatude.SourceLoaderModels.Json*",
            };
            var dm = new Datamodel();
            DatamodelSourceLoader.Load(dm, source, root);
            var loaded = dm.NodeTypes.Values.Where(t => t.DatamodelSourceId == source.Id).Select(t => t.FullName).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "Relatude.SourceLoaderModels.JsonGen.SlReview", "Relatude.SourceLoaderModels.JsonPoco.SlReview" }, loaded);
            Assert.AreEqual(0, dm.SourceNotices.Count, string.Join("\n", dm.SourceNotices));
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }
    [TestMethod]
    public void Loader_EmptyReference_MeansTheEntryAssembly() {
        // the form writes null for "current project"; an empty string from a hand edited settings file means the same
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        if (entry == null) Assert.Inconclusive("no entry assembly in this host");
        Assert.AreSame(entry, DatamodelSourceLoader.ResolveAssembly(null));
        Assert.AreSame(entry, DatamodelSourceLoader.ResolveAssembly(""));
        Assert.AreSame(typeof(SlAuthor).Assembly, DatamodelSourceLoader.ResolveAssembly(typeof(SlAuthor).Assembly.GetName().Name));
        var error = Assert.ThrowsException<Exception>(() => DatamodelSourceLoader.ResolveAssembly("No.Such.Assembly.Here"));
        StringAssert.Contains(error.Message, "No.Such.Assembly.Here");
    }
}
