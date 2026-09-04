using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relatude.DB.CodeGeneration;

/// <summary>
/// Finds which C# file declares which type in a folder of source files, by syntax alone: the files
/// are not compiled, so this works on a whole application project whose code could not be built on
/// its own. Used to stamp the declaring file on model types read from a compiled assembly whose
/// source folder is known (<see cref="Relatude.DB.Datamodels.DatamodelSource.SourceCodePath"/>),
/// which is what lets the datamodel editor write a changed type back into the right file.
/// </summary>
public static class ModelSourceFiles {
    /// <summary>All .cs files below the folder, recursively, skipping bin and obj folders.</summary>
    public static List<string> EnumerateCsFiles(string folder) {
        if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
    /// <summary>
    /// Full type name (namespace + dotted nesting, the form NodeTypeModel.FullName uses) to the
    /// absolute path of the file declaring it. A partial type declared in several files maps to the
    /// file holding the declaration with the most members.
    /// </summary>
    public static Dictionary<string, string> MapTypesToFiles(string folder) {
        var best = new Dictionary<string, (string file, int members)>(StringComparer.Ordinal);
        foreach (var file in EnumerateCsFiles(folder)) {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var root = CSharpSyntaxTree.ParseText(text, path: file).GetRoot();
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                var name = fullName(declaration);
                var members = declaration is TypeDeclarationSyntax t ? t.Members.Count : (declaration as EnumDeclarationSyntax)?.Members.Count ?? 0;
                if (!best.TryGetValue(name, out var existing) || members > existing.members) best[name] = (file, members);
            }
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.file, StringComparer.Ordinal);
    }
    static string fullName(BaseTypeDeclarationSyntax declaration) {
        var parts = new List<string>();
        SyntaxNode? node = declaration;
        while (node != null) {
            switch (node) {
                case BaseTypeDeclarationSyntax type: parts.Insert(0, type.Identifier.Text); break;
                case BaseNamespaceDeclarationSyntax ns: parts.Insert(0, ns.Name.ToString()); break;
            }
            node = node.Parent;
        }
        return string.Join(".", parts);
    }
}
