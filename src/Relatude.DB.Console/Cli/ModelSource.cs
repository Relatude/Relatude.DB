using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.Cli;

/// <summary>
/// Builds a <see cref="Datamodel"/> from the model code of an application, without needing the
/// application itself to run. The model can come from a compiled assembly (--assembly, or the newest
/// build output of --project) or straight from source files (--source), which are compiled in memory so
/// the model can be inspected before the project builds.
/// </summary>
public static class ModelSource {
    public const string NativeNamespace = "Relatude.DB.Native.Models";
    public static readonly string[] Options = ["namespace", "model-type", "source", "no-native", "auto-deduce-relations"];

    /// <summary>True when the command line names model code explicitly, rather than relying on the settings file.</summary>
    public static bool IsExplicit(CommandArgs args, Target target)
        => args.Has("namespace") || args.Has("model-type") || args.Has("source") || target.AssemblyFiles.Length > 0;

    /// <summary>
    /// The datamodel of the application, built from the model code alone. The native model is included
    /// (as the server includes it) so ids, inheritance and marker properties are the same as they are at
    /// runtime; use <see cref="IsNative"/> to leave it out of the output.
    /// </summary>
    public static Datamodel Build(CommandArgs args, Target target) {
        var dm = new Datamodel();
        if (!args.Flag("no-native")) dm.AddAssembly(typeof(NodeStore).Assembly, NativeNamespace);
        AddTo(dm, args, target);
        if (dm.NodeTypes.Count <= 1 && dm.Relations.Count == 0) {
            throw new CliException("No model types found. Point the tool at your model code with --assembly, "
                + "--project or --source, and select what to include with --namespace or --model-type."
                + Environment.NewLine + target.Describe());
        }
        return dm;
    }

    /// <summary>Adds everything the command line asks for to an existing datamodel.</summary>
    public static void AddTo(Datamodel dm, CommandArgs args, Target target) {
        var namespaces = args.GetAll("namespace");
        var typeNames = args.GetAll("model-type");
        var sources = args.GetAll("source");
        var assemblies = new List<Assembly>(target.LoadExplicitAssemblies());
        if (sources.Length > 0) assemblies.Add(compileSources(sources, target));
        if (assemblies.Count == 0 && (namespaces.Length > 0 || typeNames.Length > 0)) {
            var fromProject = target.ProbeFolders.Length > 0 ? assemblyFromProjectOutput(target) : null;
            if (fromProject != null) assemblies.Add(fromProject);
        }
        if (assemblies.Count == 0) return;

        var added = 0;
        if (namespaces.Length == 0 && typeNames.Length == 0) {
            foreach (var assembly in assemblies) added += addDetectedTypes(dm, assembly, args);
            if (added == 0) {
                throw new CliException("No model types were detected in "
                    + string.Join(", ", assemblies.Select(a => a.GetName().Name))
                    + ". Name them with --namespace or --model-type.");
            }
            Con.Info($"Detected {added} model type(s) in {string.Join(", ", assemblies.Select(a => a.GetName().Name))}."
                + " Use --namespace or --model-type to select them explicitly.");
            return;
        }
        foreach (var ns in namespaces) {
            var before = dm.NodeTypes.Count + dm.Relations.Count;
            foreach (var assembly in assemblies) dm.AddAssembly(assembly, ns, args.Flag("auto-deduce-relations"));
            var count = dm.NodeTypes.Count + dm.Relations.Count - before;
            if (count == 0) throw new CliException("No types found in namespace \"" + ns + "\".");
            added += count;
        }
        foreach (var typeName in typeNames) {
            var type = assemblies.Select(a => a.GetType(typeName, false, true)).FirstOrDefault(t => t != null)
                ?? throw new CliException("Type not found: " + typeName);
            dm.Add(type, true, args.Flag("auto-deduce-relations"));
        }
    }

    static Assembly? assemblyFromProjectOutput(Target target) {
        var name = target.ProjectFile == null ? null : Path.GetFileNameWithoutExtension(target.ProjectFile);
        target.RegisterAssemblyProbing();
        foreach (var folder in target.ProbeFolders) {
            var candidates = name == null ? Directory.GetFiles(folder, "*.dll")
                : [Path.Combine(folder, name + ".dll")];
            foreach (var file in candidates) {
                if (!File.Exists(file)) continue;
                if (Path.GetFileName(file).StartsWith("Relatude.DB.", StringComparison.OrdinalIgnoreCase)) continue;
                try {
                    return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
                } catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Every type that carries a Relatude attribute, is a relation, or holds a member of a Relatude model
    /// type. Used when no --namespace or --model-type is given; referenced types come along through
    /// <see cref="DatamodelExtensions.Add"/>.
    /// </summary>
    static int addDetectedTypes(Datamodel dm, Assembly assembly, CommandArgs args) {
        var added = 0;
        foreach (var type in loadableTypes(assembly)) {
            if (!looksLikeModelType(type)) continue;
            var before = dm.NodeTypes.Count + dm.Relations.Count;
            try {
                dm.Add(type, true, args.Flag("auto-deduce-relations"));
            } catch (Exception err) {
                throw new CliException("Unable to add the detected model type " + type.FullName + ": " + err.Message, err);
            }
            if (dm.NodeTypes.Count + dm.Relations.Count > before) added++;
        }
        return added;
    }
    static IEnumerable<Type> loadableTypes(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException err) { // a missing dependency should not hide the types that did load
            var missing = err.LoaderExceptions.Select(e => e?.Message).Where(m => m != null).Distinct().Take(3);
            Con.Warn("Some types in " + assembly.GetName().Name + " could not be loaded: " + string.Join(" ", missing));
            return err.Types.Where(t => t != null)!;
        }
    }
    static bool looksLikeModelType(Type type) {
        if (type.IsEnum || type.IsGenericTypeDefinition || type.IsArray || type.IsPointer) return false;
        if (type.IsAbstract && type.IsSealed) return false; // static class
        if (!type.IsPublic && !type.IsNestedPublic) return false;
        if (type.Namespace != null && type.Namespace.StartsWith("Relatude.DB.", StringComparison.Ordinal)) return false;
        if (typeof(IRelation).IsAssignableFrom(type)) return true;
        if (isRelatudeAttributed(type.GetCustomAttributes())) return true;
        foreach (var member in type.GetMembers()) {
            if (member is not FieldInfo && member is not PropertyInfo) continue;
            if (isRelatudeAttributed(member.GetCustomAttributes())) return true;
            var memberType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
            if (isModelValueType(memberType)) return true;
        }
        return false;
    }
    static bool isRelatudeAttributed(IEnumerable<Attribute> attributes)
        => attributes.Any(a => a.GetType().Namespace?.StartsWith("Relatude.DB", StringComparison.Ordinal) == true);
    static bool isModelValueType(Type type) {
        if (type == typeof(FileValue) || type == typeof(NodeMeta) || type == typeof(GeoCoordinate)) return true;
        if (type.IsGenericType) {
            var open = type.GetGenericTypeDefinition();
            if (open.Namespace?.StartsWith("Relatude.DB", StringComparison.Ordinal) == true) return true;
        }
        return typeof(IRelationProperty).IsAssignableFrom(type);
    }

    /// <summary>
    /// What &lt;ImplicitUsings&gt; adds to every file of a project. Model files are written against them,
    /// so the in memory compilation has to provide them too.
    /// </summary>
    const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>
    /// Compiles model source files in memory. Only the model needs to compile, so a folder holding just
    /// the model interfaces works even when the rest of the application does not build.
    /// </summary>
    static Assembly compileSources(string[] sources, Target target) {
        var files = new List<string>();
        foreach (var source in sources) {
            var full = Path.GetFullPath(source, Directory.GetCurrentDirectory());
            if (File.Exists(full)) files.Add(full);
            else if (Directory.Exists(full)) files.AddRange(Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
            else throw new UsageException("--source not found: " + full);
        }
        if (files.Count == 0) throw new UsageException("No .cs files found in " + string.Join(", ", sources));
        Con.Info($"Compiling {files.Count} source file(s) in memory.");
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, f))
            .Prepend(CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, "ImplicitUsings.g.cs"));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable);
        var compilation = CSharpCompilation.Create("RelatudeModelSource", trees, references(target), options);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20)
                .Select(d => "  " + d.Location.GetLineSpan().Path + "(" + (d.Location.GetLineSpan().StartLinePosition.Line + 1) + "): " + d.GetMessage());
            throw new CliException("The model source files did not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        stream.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(stream);
    }
    /// <summary>
    /// Compiles generated model code and returns the errors, empty when it compiles. Used as a last check
    /// that a datamodel can be expressed as code again.
    /// </summary>
    public static string[] CompileErrors(string code, Target target) {
        var tree = CSharpSyntaxTree.ParseText(code,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest), "Model.g.cs");
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release);
        var compilation = CSharpCompilation.Create("RelatudeModelCheck", [tree], references(target), options);
        return [.. compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => "line " + (d.Location.GetLineSpan().StartLinePosition.Line + 1) + ": " + d.GetMessage())];
    }

    static IEnumerable<MetadataReference> references(Target target) {
        var files = new List<string>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa) {
            files.AddRange(tpa.Split(Path.PathSeparator).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length > 0) files.Add(assembly.Location);
        }
        files.AddRange(target.AssemblyFiles);
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f));
    }

    /// <summary>
    /// The engine's own model (users, groups, collections, cultures) and the synthetic base type. Part of
    /// every database, but rarely what the user asked about.
    /// </summary>
    public static bool IsNative(NodeTypeModel nodeType)
        => nodeType.Id == NodeConstants.BaseNodeTypeId || isNativeNamespace(nodeType.Namespace);
    public static bool IsNative(RelationModel relation) => isNativeNamespace(relation.Namespace);
    static bool isNativeNamespace(string? ns)
        => ns != null && (ns == NativeNamespace || ns.StartsWith(NativeNamespace + ".", StringComparison.Ordinal));
}
