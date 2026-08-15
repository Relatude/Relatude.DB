using Relatude.DB.DataStores.Indexes;
using Relatude.DB.NodeServer;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Relatude.Server;

/// <summary>
/// The optional engines are resolved by type name written as a string, so renaming one or moving it
/// to another namespace still compiles and only fails when a store is opened - which is how
/// NativeVectorIndexEngine stayed unreachable after it moved into the ISV namespace. The bindings are
/// read back out of the source here and resolved, because that is the check the compiler cannot do.
/// </summary>
[TestClass]
public class LateBindingsTests {

    static string lateBindingsSource([CallerFilePath] string thisFile = "") => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..",
            "src", "Relatude.DB.NodeServer", "NodeServer", "LateBindings.cs"));

    static (string TypeName, string Module, string Nuget)[] bindings() {
        var source = File.ReadAllText(lateBindingsSource());
        var pattern = new Regex("""create<[^>]+>\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,""");
        return [.. pattern.Matches(source)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .Distinct()];
    }

    [TestMethod]
    public void EveryLateBoundTypeResolves() {
        var all = bindings();
        Assert.IsTrue(all.Length >= 7, "Only " + all.Length + " bindings were read from LateBindings.cs, "
            + "so this test is not checking what it is meant to check. Has the call shape changed?");
        var failures = new List<string>();
        foreach (var (typeName, module, nuget) in all) {
            try {
                var assembly = Assembly.Load(new AssemblyName(module));
                if (assembly.GetType(typeName) == null) {
                    failures.Add($"\"{typeName}\" is not in {module} (nuget {nuget}) - it was renamed or moved.");
                }
            } catch (Exception err) {
                failures.Add($"{module} could not be loaded: {err.Message}");
            }
        }
        Assert.AreEqual(0, failures.Count, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void NativeSemanticIndexEngineCanBeCreated() {
        var folder = Path.Combine(Path.GetTempPath(), "relatude.db.tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var engine = LateBindings.CreateNativeSemanticIndexEngine(folder);
            Assert.IsInstanceOfType(engine, typeof(ISemanticIndexEngine));
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
