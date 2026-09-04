using Relatude.DB.CodeGeneration;
using Relatude.DB.IO;
using System.Reflection;

namespace Relatude.DB.Datamodels;

/// <summary>
/// Loads a configured datamodel source into a datamodel: registers the source metadata on the
/// model, tags everything the source adds with its id, and (for file-based sources) with the file
/// each type came from. Shared by the server and the CLI.
///
/// A source that turns out to hold no model types - an empty namespace, a folder without files, a
/// folder that is not there yet - is loaded as an empty source rather than treated as an error: it
/// is registered on the model and can be written to, which is how the data model editor adds the
/// first type to a source that has just been configured. It leaves a line in
/// <see cref="Datamodel.SourceNotices"/> so the same situation, when it is a typo instead, is still
/// reported. What cannot be recovered from - an assembly that will not load, a type name that does
/// not resolve, JSON that is invalid, C# that does not compile - still throws.
/// </summary>
public static class DatamodelSourceLoader {
    public const string DefaultJsonFolder = "Models/Json";
    public const string DefaultCSharpFolder = "Models/CSharp";
    /// <param name="dm">The datamodel the source is combined into.</param>
    /// <param name="source">The source to load.</param>
    /// <param name="rootFolder">The folder relative file paths resolve against — the folder holding the settings file.</param>
    /// <param name="resolveIO">Resolves an IO provider by id, only needed for legacy JsonFile sources using FileIO.</param>
    public static void Load(Datamodel dm, DatamodelSource source, string rootFolder, Func<Guid, IIOProvider?>? resolveIO = null) {
        // a turned off source is not a source at all: it is not registered either, so nothing on the
        // model claims to come from it
        if (!source.Enabled) return;
        if (source.Id == Guid.Empty) throw new Exception("The datamodel source has no Id. Every datamodel source must have a unique id. ");
        if (source.Type == DatamodelSourceType.Code) throw new Exception(
            "The datamodel source type Code is reserved for model types added directly from code at startup and cannot be configured as a source. ");
        if (dm.Sources.Any(s => s.Id == source.Id)) throw new Exception(
            "Two datamodel sources have the same id " + source.Id + ". Every datamodel source must have a unique id. ");
        dm.Sources.Add(source);
        dm.CurrentSourceId = source.Id;
        try {
            switch (source.Type) {
                case DatamodelSourceType.AssemblyNameReference:
                    loadAssemblySource(dm, source);
                    stampSourceCodeFiles(dm, source, rootFolder);
                    break;
                case DatamodelSourceType.TypeNameReference:
                    loadTypeNameSource(dm, source);
                    stampSourceCodeFiles(dm, source, rootFolder);
                    break;
                case DatamodelSourceType.JsonFile:
                    loadJsonSource(dm, source, rootFolder, resolveIO);
                    break;
                case DatamodelSourceType.CSharpCodeFile:
                    loadCSharpSource(dm, source, rootFolder);
                    break;
                default:
                    throw new NotSupportedException("Unknown datamodel source type: " + source.Type);
            }
        } finally {
            // anything added outside a configured source (e.g. the OnDatamodelInit event) is tagged as code:
            dm.CurrentSourceId = DatamodelSource.CodeSourceId;
        }
    }
    /// <summary>Records why a source came out empty. Not an error - see the class summary.</summary>
    static void note(Datamodel dm, DatamodelSource source, string message) {
        var name = string.IsNullOrEmpty(source.Name) ? source.Id.ToString() : source.Name;
        dm.SourceNotices.Add("The datamodel source \"" + name + "\" adds no types: " + message);
    }
    static void loadAssemblySource(Datamodel dm, DatamodelSource source) {
        Assembly? assembly;
        if (source.Reference == null) {
            assembly = Assembly.GetEntryAssembly();
            if (assembly == null) throw new Exception("No assembly reference is set and there is no entry assembly. Set Reference to the assembly name containing the model types. ");
        } else {
            try {
                assembly = Assembly.Load(source.Reference);
            } catch (Exception ex) {
                throw new Exception("The assembly \"" + source.Reference + "\" could not be loaded: " + ex.Message
                    + " Check that the name is spelled correctly (without the .dll extension) and that the assembly is referenced by, or copied to, the application. ", ex);
            }
        }
        if (string.IsNullOrEmpty(source.Namespace)) throw new Exception(
            "The datamodel source has no Namespace. Set Namespace to the namespace containing the model types in " + assembly.GetName().Name + ". ");
        var typesBefore = dm.NodeTypes.Count + dm.Relations.Count;
        dm.AddAssembly(assembly, source.Namespace, source.AutoDeduceRelations);
        if (dm.NodeTypes.Count + dm.Relations.Count == typesBefore) {
            string[] available;
            try {
                available = assembly.GetTypes().Select(t => t.Namespace).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n).ToArray()!;
            } catch { available = []; }
            note(dm, source, "no model types were found in the namespace \"" + source.Namespace + "\" of the assembly " + assembly.GetName().Name
                + " (or all of them were already added by an earlier datamodel source), so it is loaded as an empty source. "
                + "Check the namespace for typos if it was meant to hold types. "
                + (available.Length == 0 ? "" : "Namespaces in the assembly: " + string.Join(", ", available.Take(20)) + (available.Length > 20 ? ", ..." : "") + ". "));
        }
    }
    static void loadTypeNameSource(Datamodel dm, DatamodelSource source) {
        if (string.IsNullOrEmpty(source.Reference)) throw new Exception(
            "The datamodel source has no Reference. Set Reference to the type name of a model type, assembly qualified if it is not in the entry assembly (e.g. \"MyApp.Models.Person, MyApp\"). ");
        var type = Type.GetType(source.Reference);
        if (type == null) throw new Exception("The type \"" + source.Reference + "\" could not be found. "
            + "Use the full type name, assembly qualified if the type is not in the entry assembly or mscorlib (e.g. \"MyApp.Models.Person, MyApp\"). ");
        dm.Add(type, true, source.AutoDeduceRelations);
    }
    // A compiled source whose C# folder is known gets the declaring file stamped on its types, the
    // way file based sources do, so the datamodel editor knows where to write a changed type. Syntax
    // only, and never a reason for the open to fail: a folder that is missing or unreadable simply
    // leaves the file names empty, and the editor reports that when it is asked to write.
    static void stampSourceCodeFiles(Datamodel dm, DatamodelSource source, string rootFolder) {
        var folder = ResolveSourceCodeFolder(source, rootFolder);
        if (folder == null || !Directory.Exists(folder)) return;
        Dictionary<string, string> fileByType;
        try { fileByType = ModelSourceFiles.MapTypesToFiles(folder); } catch { return; }
        foreach (var nt in dm.NodeTypes.Values) {
            if (nt.DatamodelSourceId == source.Id && fileByType.TryGetValue(nt.FullName, out var file))
                nt.DatamodelSourceFilename = Path.GetRelativePath(folder, file);
        }
        foreach (var r in dm.Relations.Values) {
            if (r.DatamodelSourceId == source.Id && fileByType.TryGetValue(r.FullName(), out var file))
                r.DatamodelSourceFilename = Path.GetRelativePath(folder, file);
        }
    }
    /// <summary>
    /// The folder holding the C# files a compiled source is built from (<see cref="DatamodelSource.SourceCodePath"/>
    /// resolved against the settings folder), or null when the source does not name one.
    /// </summary>
    public static string? ResolveSourceCodeFolder(DatamodelSource source, string rootFolder) {
        if (string.IsNullOrEmpty(source.SourceCodePath)) return null;
        var path = source.SourceCodePath;
        if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(rootFolder, path));
        return path;
    }
    static void loadJsonSource(Datamodel dm, DatamodelSource source, string rootFolder, Func<Guid, IIOProvider?>? resolveIO) {
        if (source.FileIO != null) { // legacy variant reading through an IO provider
            var io = resolveIO?.Invoke(source.FileIO.Value);
            if (io == null) throw new Exception("No IO provider with id " + source.FileIO.Value + " is configured. ");
            if (string.IsNullOrEmpty(source.Reference)) throw new Exception("The datamodel source has no Reference. Set Reference to the file name of the JSON datamodel. ");
            var key = source.Reference.SplitKey();
            if (!io.ExistsAndIsNotEmpty(key)) { // not written yet: an empty source, the same as a folder without files
                note(dm, source, "the provider holds no file \"" + source.Reference + "\" yet, so it is loaded as an empty source. ");
                return;
            }
            dm.AddDatamodel(deserialize(io.ReadAllTextUTF8(key), source.Reference), source.Id, source.Reference);
            return;
        }
        var (files, baseFolder, emptyReason) = ResolveFiles(source, rootFolder, DefaultJsonFolder, "*.json");
        if (emptyReason != null) {
            note(dm, source, emptyReason);
            return;
        }
        var typesBefore = dm.NodeTypes.Count + dm.Relations.Count;
        foreach (var file in files) {
            var imported = deserialize(File.ReadAllText(file), file);
            registerAssembliesOfBackingClrTypes(dm, imported);
            dm.AddDatamodel(imported, source.Id, Path.GetRelativePath(baseFolder, file));
        }
        if (dm.NodeTypes.Count + dm.Relations.Count == typesBefore) note(dm, source,
            "the file" + (files.Count == 1 ? "" : "s") + " " + string.Join(", ", files.Select(Path.GetFileName))
            + " define no types, so it is loaded as an empty source. ");
    }
    // A JSON file only defines the model; at runtime each node type still needs a backing CLR type
    // (a plain class, no attributes needed) for the mapper to compile against. Reflection sources
    // record their assemblies as they add types - for JSON types the assemblies are found here.
    static void registerAssembliesOfBackingClrTypes(Datamodel dm, Datamodel imported) {
        foreach (var nt in imported.NodeTypes.Values) {
            if (nt.Id == NodeConstants.BaseNodeTypeId) continue;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                if (assembly.IsDynamic || assembly.Location.Length == 0) continue;
                if (assembly.GetType(nt.FullName, throwOnError: false) != null) {
                    dm.Assemblies.Add(assembly);
                    break;
                }
            }
        }
    }
    static Datamodel deserialize(string json, string file) {
        try {
            return DatamodelJson.Deserialize(json);
        } catch (System.Text.Json.JsonException ex) {
            throw new Exception("The datamodel file \"" + file + "\" contains invalid JSON or an invalid datamodel: " + ex.Message, ex);
        }
    }
    static void loadCSharpSource(Datamodel dm, DatamodelSource source, string rootFolder) {
        var (files, baseFolder, emptyReason) = ResolveFiles(source, rootFolder, DefaultCSharpFolder, "*.cs");
        if (emptyReason != null) {
            note(dm, source, emptyReason);
            return; // nothing to compile: an empty source, ready for the editor to write the first type into
        }
        var compiled = ModelCodeCompiler.CompileAndLoad(files, "RelatudeModel");
        dm.AssemblyImages[compiled.Assembly.GetName().Name!] = compiled.Image;
        var typesBefore = dm.NodeTypes.Count + dm.Relations.Count;
        if (!string.IsNullOrEmpty(source.Namespace)) {
            dm.AddAssembly(compiled.Assembly, source.Namespace, source.AutoDeduceRelations);
        } else {
            foreach (var type in compiled.Assembly.GetTypes()) {
                if (type.Name.StartsWith('<')) continue; // compiler generated
                if (type.IsAbstract && type.IsSealed) continue; // static classes
                if (type.IsEnum || type.IsNested) continue;
                dm.Add(type, true, source.AutoDeduceRelations);
            }
        }
        if (dm.NodeTypes.Count + dm.Relations.Count == typesBefore) note(dm, source,
            "no model types were found in the compiled source file" + (files.Count == 1 ? "" : "s") + " "
            + string.Join(", ", files.Select(Path.GetFileName))
            + (string.IsNullOrEmpty(source.Namespace) ? "" : " in the namespace \"" + source.Namespace + "\"")
            + ", so it is loaded as an empty source. ");
        // several files compile as one assembly, so the file each type came from is stamped afterwards:
        foreach (var nt in dm.NodeTypes.Values) {
            if (nt.DatamodelSourceId == source.Id && compiled.FileByTypeFullName.TryGetValue(nt.FullName, out var file))
                nt.DatamodelSourceFilename = Path.GetRelativePath(baseFolder, file);
        }
        foreach (var r in dm.Relations.Values) {
            if (r.DatamodelSourceId == source.Id && compiled.FileByTypeFullName.TryGetValue(r.FullName(), out var file))
                r.DatamodelSourceFilename = Path.GetRelativePath(baseFolder, file);
        }
    }
    /// <summary>
    /// Filepath may name a file or a folder (all matching files, recursively). When empty, the
    /// default folder is used, combined with Reference when set. Relative paths resolve against
    /// the folder holding the settings file. Also returns the folder filenames are stored relative
    /// to, and - when there is nothing to read - why, since that is a note rather than an error:
    /// the folder of a source that has just been added does not exist until its first type is
    /// written into it.
    /// </summary>
    public static (List<string> files, string baseFolder, string? emptyReason) ResolveFiles(DatamodelSource source, string rootFolder, string defaultFolder, string pattern) {
        var path = ResolveFilePath(source, rootFolder, defaultFolder);
        if (File.Exists(path)) return ([path], Path.GetDirectoryName(path)!, null);
        if (Directory.Exists(path)) {
            var files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            return (files, path, files.Count == 0 ? "the folder \"" + path + "\" holds no " + pattern + " files, so it is loaded as an empty source. " : null);
        }
        // the path is not there: a folder unless it names a file
        var baseFolder = Path.HasExtension(path) ? Path.GetDirectoryName(path)! : path;
        return ([], baseFolder, "the path \"" + path + "\" does not exist, so it is loaded as an empty source. "
            + "Set Filepath to a " + pattern + " file or a folder holding such files if that is not intended "
            + "(relative paths resolve against the settings folder; when Filepath is empty, \"" + defaultFolder + "\" is used). ");
    }
    /// <summary>
    /// The absolute file or folder path a file based source reads from, whether or not it exists yet:
    /// Filepath, else the default folder combined with Reference, else the default folder, resolved
    /// against the settings folder. The default folder for the source's type is
    /// <see cref="DefaultJsonFolder"/> or <see cref="DefaultCSharpFolder"/>.
    /// </summary>
    public static string ResolveFilePath(DatamodelSource source, string rootFolder, string defaultFolder) {
        var path = !string.IsNullOrEmpty(source.Filepath) ? source.Filepath
            : !string.IsNullOrEmpty(source.Reference) ? Path.Combine(defaultFolder, source.Reference)
            : defaultFolder;
        if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(rootFolder, path));
        return path;
    }
}
