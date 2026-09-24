using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.Utils;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

/// <summary>
/// The compiled mapper DLL cached in the state folder. It is derived data, so whatever is at its key
/// must never keep a database from opening, and a model that now lives in a differently named assembly
/// must not pick up a DLL bound to the old one. A renamed model project broke both: the generated code
/// was the same, so was the key, and the DLL at it asked for an assembly that was gone.
/// </summary>
[TestClass]
public class MapperCacheTests {

    string _root = "";
    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "RelatudeDBTests", "MapperCache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_root, true); } catch { }
    }

    [TestMethod]
    public void CachedMapperBoundToAMissingAssembly_IsCompiledAgain() {
        // what a renamed model project leaves behind: the DLL loads, but its types implement an interface
        // from an assembly this process does not have, so reading them fails
        assertCompiledAgainOver(dllBoundToAMissingAssembly(), expectedReason: "MapperCacheTests.Gone");
    }

    [TestMethod]
    public void CachedMapperThatIsNotADll_IsCompiledAgain() {
        assertCompiledAgainOver("not a dll"u8.ToArray());
    }

    [TestMethod]
    public void SameModelInARenamedAssembly_GetsAMapperOfItsOwn() {
        var io = new IOProviderMemory();
        var (before, beforeNote) = compileModel(Path.Combine(_root, "before"), "Before.Rename");
        Guid id;
        using (var store = new NodeStore(DataStoreLocal.Open(before, null, io))) {
            var note = Activator.CreateInstance(beforeNote)!;
            beforeNote.GetProperty("Text")!.SetValue(note, "kept");
            store.Insert(note, out id);
        }
        var keyBefore = FileKeyUtility.MapperDll_GetAllFileKeys(io).Single();

        var (after, afterNote) = compileModel(Path.Combine(_root, "after"), "After.Rename");
        Assert.AreNotEqual(beforeNote.Assembly.GetName().Name, afterNote.Assembly.GetName().Name);
        using (var store = new NodeStore(DataStoreLocal.Open(after, null, io))) {
            var read = store.Get(id);
            // a mapper bound to the old assembly hands back its types here - and in a process where
            // that assembly no longer exists it does not load at all
            Assert.IsInstanceOfType(read, afterNote, "the node must be read as the type of the assembly the model is in now");
            Assert.AreEqual("kept", afterNote.GetProperty("Text")!.GetValue(read));
        }
        var keyAfter = FileKeyUtility.MapperDll_GetAllFileKeys(io).Single();
        Assert.IsFalse(keyAfter.IsSameKey(keyBefore), "the same code in another assembly must get another key, not reuse the old DLL");
    }

    static void assertCompiledAgainOver(byte[] unloadable, string? expectedReason = null) {
        var io = new IOProviderMemory();
        var articles = Helper.GenerateArticles(20);
        using (var first = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), null, io))) first.Insert(articles);
        var key = FileKeyUtility.MapperDll_GetAllFileKeys(io).Single();
        io.WriteAllBytes(key, unloadable);

        var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        using (var store = new NodeStore(storeData)) {
            Assert.AreEqual(articles.Count, store.Query<Article>().Count(), "the store must open over the unloadable file, with its data intact");
            var warning = storeData.GetSystemTrace(0, 1000).SingleOrDefault(e => e.Type == SystemLogEntryType.Warning && e.Text.Contains("mapper DLL"));
            Assert.IsNotNull(warning, "throwing the cached DLL away is worth a warning");
            if (expectedReason != null) StringAssert.Contains(warning.Details, expectedReason, "the warning must say why the DLL did not load");
        }
        Assert.IsTrue(FileKeyUtility.MapperDll_GetAllFileKeys(io).Single().IsSameKey(key), "the model did not change, so neither does the key");
        CollectionAssert.AreNotEqual(unloadable, io.ReadAllBytes(key), "the unloadable file must be replaced by the new DLL");

        // the replacement is used from then on, rather than compiled at every start
        var againData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        using var again = new NodeStore(againData);
        Assert.AreEqual(articles.Count, again.Query<Article>().Count());
        var trace = againData.GetSystemTrace(0, 1000);
        Assert.IsTrue(trace.Any(e => e.Text.StartsWith("Loading mapper DLL from disk")));
        Assert.IsFalse(trace.Any(e => e.Type == SystemLogEntryType.Warning && e.Text.Contains("mapper DLL")));
    }

    // Two assemblies built here: one declaring an interface, and one with a class implementing it. Only
    // the second is handed back, so its reference to the first can never be resolved.
    static byte[] dllBoundToAMissingAssembly() {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] core = [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Runtime.dll")),
        ];
        var gone = emit("MapperCacheTests.Gone", "public interface IGone { }", core);
        return emit("MapperCacheTests.Stale", "public class Stale : IGone { }", [.. core, MetadataReference.CreateFromImage(gone)]);
    }
    static byte[] emit(string assemblyName, string source, IEnumerable<MetadataReference> references) {
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }

    const string noteSource = """
        using Relatude.DB.Nodes;
        namespace MapperCacheModels;
        [Node]
        public class McNote {
            [PublicIdProperty]
            public Guid Id { get; set; }
            [StringProperty]
            public string Text { get; set; } = "";
        }
        """;
    // The same model source compiled into an assembly of its own, the way a CSharpCodeFile source is. The
    // assembly is named after the prefix and the file's path, so another folder gives the same types in a
    // differently named assembly - which is all a renamed model project changes.
    static (Datamodel model, Type note) compileModel(string folder, string assemblyNamePrefix) {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "McNote.cs");
        File.WriteAllText(file, noteSource);
        var compiled = ModelCodeCompiler.CompileAndLoad([file], assemblyNamePrefix);
        var dm = new Datamodel();
        dm.AssemblyImages[compiled.Assembly.GetName().Name!] = compiled.Image; // no file on disk to reference when compiling mappers
        var note = compiled.Assembly.GetType("MapperCacheModels.McNote")!;
        dm.Add(note);
        return (dm, note);
    }
}
