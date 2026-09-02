using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
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
        // commented-out calls are not bindings: one is kept in the source as a note next to the
        // provider that replaced it, and resolving it would report on code that no longer runs
        var source = string.Join(Environment.NewLine, File.ReadAllLines(lateBindingsSource())
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var pattern = new Regex("""create<[^>]+>\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,""");
        return [.. pattern.Matches(source)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .Distinct()];
    }

    [TestMethod]
    public void EveryLateBoundTypeResolves() {
        var all = bindings();
        // the four engines still resolved by name: the Sqlite index store, queue store and embedding
        // cache, and the Lucene text index. The AI providers and the Azure blob provider used to be
        // here too and are now constructed directly, so this number went down with them - it is a
        // canary for the call shape, not a target
        Assert.IsTrue(all.Length >= 4, "Only " + all.Length + " bindings were read from LateBindings.cs, "
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

    /// <summary>
    /// The AI provider names the settings page offers are resolved by CreateAiProvider the same way a
    /// typed one is, so a name that list has and the switch does not is a suggestion that fails only
    /// when the database is opened - the same failure this fixture exists for, one step earlier.
    /// </summary>
    [TestMethod]
    public void EverySuggestedAiProviderNameIsRecognised() {
        var source = File.ReadAllText(lateBindingsSource());
        var start = source.IndexOf("CreateAiProvider", StringComparison.Ordinal);
        Assert.IsTrue(start > 0, "CreateAiProvider is no longer in LateBindings.cs.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal); // the method's own closing brace
        var body = end > start ? source[start..end] : source[start..];

        var suggested = SettingsCatalog.Database.SelectMany(s => s.Groups).SelectMany(g => g.Settings)
            .Single(s => s.Path == "AISettings.TypeName").Suggestions;
        Assert.IsNotNull(suggested, "The provider type setting no longer suggests anything, so nothing is being checked.");
        foreach (var name in suggested!.Select(s => s.Value)) {
            Assert.IsTrue(body.Contains("\"" + name + "\"", StringComparison.Ordinal),
                "\"" + name + "\" is offered as a provider type but CreateAiProvider does not recognise it, so it would be "
                + "taken as the type name of a custom provider and fail when the database opens.");
        }
    }

    [TestMethod]
    public void SemanticIndexEnginesCanBeCreated() {
        var folder = Path.Combine(Path.GetTempPath(), "relatude.db.tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var ivs = LateBindings.CreateSemanticIndexEngine(AIIndexType.IVS, folder, 64);
            Assert.IsInstanceOfType(ivs, typeof(ISemanticIndexEngine));
            var hnsw = LateBindings.CreateSemanticIndexEngine(AIIndexType.HNSW, folder, 64);
            Assert.IsInstanceOfType(hnsw, typeof(ISemanticIndexEngine));
            Assert.ThrowsException<Exception>(() => LateBindings.CreateSemanticIndexEngine(AIIndexType.Memory, folder, null));
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
