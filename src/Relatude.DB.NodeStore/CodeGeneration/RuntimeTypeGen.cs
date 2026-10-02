using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.CodeGeneration;

/// <summary>
/// Model code for the types of a runtime types (JSON) source that the application has no class for. Such a
/// type is defined by its JSON file alone, but the mapper code a store compiles when it opens casts to and
/// creates the type by name. The class (or interface, or relation class) is therefore generated from the
/// model and compiled together with the mappers, so the database opens and the type can be used by name -
/// <see cref="NodeStore.Create(string)"/>, untyped queries, the admin UI - though not from compiled
/// application code, which cannot name it. A type the application does have a class for (a plain class with
/// the same full name, found when the source was loaded) keeps using that class.
/// </summary>
internal static class RuntimeTypeGen {
    public static List<(string className, string code)> Generate(Datamodel datamodel) {
        var (nodeTypes, relations) = Find(datamodel);
        var code = new List<(string className, string code)>();
        // one file per type, so a compile error names the type it belongs to:
        foreach (var t in nodeTypes) code.Add((t.FullName + ".runtime", ModelGen.GenerateCSharpModelCode(datamodel, true, n => n.Id == t.Id, _ => false)));
        foreach (var r in relations) code.Add((r.FullName() + ".runtime", ModelGen.GenerateCSharpModelCode(datamodel, true, _ => false, x => x.Id == r.Id)));
        return code;
    }
    /// <summary>
    /// The node types and relations of runtime types sources with no CLR type in the model's assemblies. Types
    /// of other sources always come from a class (that is how they got into the model), so they are never
    /// generated, whatever a lookup by name says.
    /// </summary>
    public static (List<NodeTypeModel> nodeTypes, List<RelationModel> relations) Find(Datamodel datamodel) {
        datamodel.EnsureInitalization();
        var runtimeSources = datamodel.Sources.Where(s => s.Type == DatamodelSourceType.RuntimeTypes).Select(s => s.Id).ToHashSet();
        if (runtimeSources.Count == 0) return ([], []);
        var nodeTypes = datamodel.NodeTypes.Values
            .Where(t => t.Id != NodeConstants.BaseNodeTypeId && runtimeSources.Contains(t.DatamodelSourceId) && !hasClrType(datamodel, t.FullName))
            .ToList();
        var relations = datamodel.Relations.Values
            .Where(r => r.RelationClassType == null && runtimeSources.Contains(r.DatamodelSourceId) && !hasClrType(datamodel, r.FullName()))
            .ToList();
        return (nodeTypes, relations);
    }
    // the assemblies the mapper code is compiled against: a class anywhere else could not be used by it anyway
    static bool hasClrType(Datamodel datamodel, string fullName) {
        foreach (var assembly in datamodel.Assemblies) {
            try {
                if (assembly.GetType(fullName, throwOnError: false) != null) return true;
            } catch { }
        }
        return false;
    }
}
