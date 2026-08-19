using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relatude.DB.Common;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Relatude.DB.CodeGeneration;

/// <summary>The result of compiling model source files in memory.</summary>
/// <param name="Assembly">The loaded assembly holding the model types.</param>
/// <param name="Image">The raw emitted assembly bytes, needed as a metadata reference when compiling mappers.</param>
/// <param name="FileByTypeFullName">Full type name -> absolute path of the .cs file the type is declared in.</param>
public sealed record CompiledModelCode(Assembly Assembly, byte[] Image, IReadOnlyDictionary<string, string> FileByTypeFullName);

/// <summary>
/// Compiles CSharpCodeFile datamodel sources (loose .cs files, not part of the compiled project)
/// in memory and loads them into the default AssemblyLoadContext.
/// </summary>
public static class ModelCodeCompiler {
    // Re-initializing a store in the same process must reuse the identical loaded assembly for the
    // same source files, otherwise the datamodel would hold types from a second copy while compiled
    // mappers bind to the first. Keyed by a hash of the file names and contents.
    static readonly ConcurrentDictionary<Guid, CompiledModelCode> _cache = new();

    /// <summary>
    /// What &lt;ImplicitUsings&gt; adds to every file of a project. Model files are written against them,
    /// so the in memory compilation has to provide them too.
    /// </summary>
    const string implicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;
    const string implicitUsingsFileName = "ImplicitUsings.g.cs";

    public static CompiledModelCode CompileAndLoad(IReadOnlyList<string> csFilePaths, string assemblyNamePrefix) {
        if (csFilePaths.Count == 0) throw new Exception("No .cs files to compile. ");
        var files = csFilePaths.Select(f => (path: Path.GetFullPath(f), content: File.ReadAllText(f)))
            .OrderBy(f => f.path, StringComparer.OrdinalIgnoreCase).ToList();
        var key = string.Join("\n", files.Select(f => f.path + "\n" + f.content)).GenerateHashGuid();
        return _cache.GetOrAdd(key, _ => compileAndLoad(files, assemblyNamePrefix + "." + key.ToString("N")));
    }
    static CompiledModelCode compileAndLoad(List<(string path, string content)> files, string assemblyName) {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.content, parseOptions, f.path))
            .Prepend(CSharpSyntaxTree.ParseText(implicitUsings, parseOptions, implicitUsingsFileName))
            .ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable);
        var compilation = CSharpCompilation.Create(assemblyName, trees, references(), options);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("The model source files did not compile (" + errors.Count + " error" + (errors.Count == 1 ? "" : "s") + "). "
                + "The files are compiled on their own, so they can only use the .NET base libraries, Relatude.DB and assemblies already loaded by the application:");
            foreach (var e in errors.Take(20)) {
                var span = e.Location.GetLineSpan();
                sb.AppendLine("  " + span.Path + "(" + (span.StartLinePosition.Line + 1) + "): " + e.GetMessage());
            }
            if (errors.Count > 20) sb.AppendLine("  ... and " + (errors.Count - 20) + " more error(s).");
            throw new Exception(sb.ToString());
        }
        var fileByType = mapTypesToFiles(compilation);
        var image = stream.ToArray();
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image));
        return new CompiledModelCode(assembly, image, fileByType);
    }
    static Dictionary<string, string> mapTypesToFiles(CSharpCompilation compilation) {
        // several files compile as one unit, so which file declared a type is only known to Roslyn:
        var fileByType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees) {
            if (tree.FilePath == implicitUsingsFileName) continue;
            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                var symbol = semanticModel.GetDeclaredSymbol(declaration);
                if (symbol == null) continue;
                fileByType[symbol.ToDisplayString()] = tree.FilePath; // a partial type in several files keeps the last
            }
        }
        return fileByType;
    }
    static IEnumerable<MetadataReference> references() {
        var files = new List<string>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa) {
            files.AddRange(tpa.Split(Path.PathSeparator).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length > 0) files.Add(assembly.Location);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f));
    }
}
