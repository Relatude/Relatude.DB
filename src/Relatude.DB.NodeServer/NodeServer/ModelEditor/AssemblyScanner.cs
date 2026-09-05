using System.Reflection;
using Relatude.DB.Datamodels;

namespace Relatude.DB.NodeServer.ModelEditor;

/// <summary>
/// The lookups behind the type reference form of the data model editor: which assemblies the running
/// application can see, which namespaces one of them holds, and what a given assembly and namespace
/// would load as model types. All three are on demand - reflecting over every loaded assembly is not
/// free - and none of them touches the database: they answer from the process alone, resolving the
/// assembly exactly the way <see cref="DatamodelSourceLoader"/> does when the database opens, so what
/// the form shows is what the open will find.
/// </summary>
static class AssemblyScanner {
    // assemblies that never hold model classes; keeps the list to what a user could mean
    static readonly string[] frameworkPrefixes = ["System", "Microsoft", "netstandard", "mscorlib", "WindowsBase", "PresentationCore", "PresentationFramework", "Accessibility", "Newtonsoft", "Humanizer"];
    static bool isFramework(string name) => frameworkPrefixes.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase) || name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entry assembly (the current project) by name, and every other assembly the application can
    /// load by name: those already loaded into the process, and the managed .dll files next to the
    /// application that nothing has loaded yet. Framework assemblies are left out.
    /// </summary>
    public static object ScanAssemblies() {
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // name -> loaded
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
            if (a.IsDynamic) continue;
            var name = a.GetName().Name;
            if (string.IsNullOrEmpty(name) || isFramework(name)) continue;
            found[name] = true;
        }
        try {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")) {
                var name = Path.GetFileNameWithoutExtension(file);
                if (isFramework(name) || found.ContainsKey(name)) continue;
                try { AssemblyName.GetAssemblyName(file); } catch { continue; } // a native dll, or not an assembly at all
                found[name] = false;
            }
        } catch { }
        return new {
            Current = entryName,
            Assemblies = found
                .Where(kv => !string.Equals(kv.Key, entryName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { Name = kv.Key, Loaded = kv.Value })
                .ToArray(),
        };
    }

    /// <summary>
    /// The namespaces of one assembly that hold types a type reference could take, each with how many.
    /// Throws when the assembly cannot be loaded, with the loader's message.
    /// </summary>
    public static object ScanNamespaces(string? reference) {
        var assembly = DatamodelSourceLoader.ResolveAssembly(reference);
        var namespaces = types(assembly)
            .Where(isCandidate)
            .GroupBy(t => t.Namespace ?? "")
            .Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { Name = g.Key, Types = g.Count() })
            .ToArray();
        return new { Assembly = assembly.GetName().Name, Namespaces = namespaces };
    }

    /// <summary>
    /// What a type reference with this assembly and namespace loads: the node types and relations, built
    /// the way the loader builds them, into a scratch model. Types the namespace pulls in from elsewhere
    /// (a base class or a related type in another namespace) are marked as not direct. A model that
    /// cannot be built - two types with one id, a class without a parameterless constructor - reports the
    /// error next to what was read before it.
    /// </summary>
    public static object Probe(string? reference, string? ns) {
        var assembly = DatamodelSourceLoader.ResolveAssembly(reference);
        var name = assembly.GetName().Name;
        if (string.IsNullOrEmpty(ns)) return new { Assembly = name, NodeTypes = Array.Empty<object>(), Relations = Array.Empty<object>(), Error = (string?)null };
        var dm = new Datamodel();
        string? error = null;
        try {
            dm.AddAssembly(assembly, ns, DatamodelSource.AutoDeduceRelations);
        } catch (Exception ex) {
            error = ex.Message;
        }
        var nodeTypes = dm.NodeTypes.Values
            .Where(t => t.Id != NodeConstants.BaseNodeTypeId)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => new { t.Id, t.CodeName, t.Namespace, Kind = t.ModelType.ToString(), t.IsInnerNode, Direct = DatamodelSource.NamespaceMatches(ns, t.Namespace), Properties = t.Properties.Count })
            .ToArray();
        var relations = dm.Relations.Values
            .OrderBy(r => r.FullName(), StringComparer.Ordinal)
            .Select(r => new { r.Id, r.CodeName, r.Namespace, Kind = r.RelationType.ToString(), Direct = DatamodelSource.NamespaceMatches(ns, r.Namespace) })
            .ToArray();
        return new { Assembly = name, NodeTypes = nodeTypes, Relations = relations, Error = error };
    }

    static IEnumerable<Type> types(Assembly assembly) {
        try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }
    // the same sieve the loader applies before handing a type to the model, minus the model's own checks
    static bool isCandidate(Type t) {
        if (t.IsNested || t.IsEnum || t.Name.StartsWith('<')) return false;
        if (t.IsAbstract && t.IsSealed) return false; // static class
        if (typeof(Delegate).IsAssignableFrom(t) || typeof(Attribute).IsAssignableFrom(t) || typeof(Exception).IsAssignableFrom(t)) return false;
        return t.IsClass || t.IsInterface || t.IsValueType;
    }
}
