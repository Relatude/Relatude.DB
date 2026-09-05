using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Relatude.DB.Datamodels;


public enum DatamodelSourceType {
    /// <summary>
    /// Model classes compiled into the application: the assembly named by <see cref="DatamodelSource.Reference"/>
    /// (or the entry assembly when it is null) and every model type in <see cref="DatamodelSource.Namespace"/>.
    /// Called AssemblyNameReference before September 2026; the old name still reads from settings files.
    /// </summary>
    TypeReference = 0,
    // 1 was TypeNameReference (one type by its assembly qualified name), removed September 2026
    /// <summary>
    /// Model files on disk, read when the database opens: JSON model files or C# files compiled in memory,
    /// as <see cref="DatamodelSource.FileFormat"/> says. Before September 2026 these were the two kinds
    /// JsonFile (2) and CSharpCodeFile (3); both names still read, and set the file format.
    /// </summary>
    TextFiles = 2,
    // 3 was CSharpCodeFile, folded into TextFiles + FileFormat.CSharpCode
    /// <summary>
    /// Reserved for model types added directly from code at startup (for example in the OnDatamodelInit event).
    /// Cannot be used as a configured source in settings.
    /// </summary>
    Code = 4,
}

/// <summary>What the files of a <see cref="DatamodelSourceType.TextFiles"/> source hold.</summary>
public enum DatamodelSourceFileFormat {
    /// <summary>Serialized datamodel JSON (the <see cref="DatamodelJson"/> form), *.json files.</summary>
    Json = 0,
    /// <summary>C# model classes compiled while the database opens, *.cs files.</summary>
    CSharpCode = 1,
}

/// <remarks>
/// Serialized through <see cref="DatamodelSourceJsonConverter"/>, which keeps the kind names of earlier
/// versions readable; a new property has to be added there as well as here.
/// </remarks>
[JsonConverter(typeof(DatamodelSourceJsonConverter))]
public class DatamodelSource {
    public static readonly Guid CodeSourceId = new("00000000-0000-0000-0000-00000000c0de");
    public static DatamodelSource CreateCodeSource() => new() {
        Id = CodeSourceId,
        Name = "Code",
        Type = DatamodelSourceType.Code,
    };
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Namespace { get; set; }
    public DatamodelSourceType Type { get; set; }
    /// <summary>
    /// For a <see cref="DatamodelSourceType.TextFiles"/> source: whether the files hold datamodel JSON or C# model
    /// classes. Ignored by the other kinds. Defaults to JSON.
    /// </summary>
    public DatamodelSourceFileFormat FileFormat { get; set; } = DatamodelSourceFileFormat.Json;
    public string? Filepath { get; set; }
    public string? Reference { get; set; }
    public Guid? FileIO { get; set; }
    /// <summary>
    /// For a source read from a compiled assembly (<see cref="DatamodelSourceType.TypeReference"/>): the folder
    /// holding the C# files the assembly is built from, relative to the settings folder unless rooted. When
    /// set, the datamodel editor can write model changes back into those files (the application then has to
    /// be rebuilt and restarted for them to take effect). Without it the source is read only in the editor.
    /// </summary>
    public string? SourceCodePath { get; set; }
    /// <summary>
    /// For a <see cref="DatamodelSourceType.TypeReference"/> source with a <see cref="SourceCodePath"/>: when
    /// true, the folder is owned by the datamodel editor. Activating a model regenerates it from scratch - every
    /// file in it is deleted and one file per node type and relation is generated, each starting with a comment
    /// saying it is generated and will be overwritten. Files in the folder that do not carry that comment are
    /// reported before the activation, so nothing hand-written is deleted without asking. When false (the
    /// default), the editor rewrites the existing files in place and leaves files that hold no model types alone.
    /// </summary>
    public bool GenerateModelFile { get; set; } = false;
    /// <summary>
    /// When false the source is skipped entirely: nothing it defines reaches the datamodel, and it is
    /// not registered on it either. A source added from the admin UI starts turned off, so a settings
    /// file is never left holding a half configured source that would stop the database from opening.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// The colour the admin UI marks this source's types and relations with, as a CSS colour
    /// (<c>#2f7fd6</c>, or a name like <c>teal</c>). Empty leaves it to the UI, which picks one from a
    /// palette by the source's position in the list. Nothing but the UI reads it: the model does not
    /// change because a colour does.
    /// </summary>
    public string? Color { get; set; }
    /// <summary>
    /// Process wide: when true, plain node-typed properties (and collections of node types) without an
    /// explicit relation are turned into auto-created relations in every source read from compiled or
    /// compiled-on-open classes, matching the behavior of versions before 2026. When false (the default),
    /// such properties become Reference/References properties instead. Set it before the database opens.
    /// Until September 2026 this was a per-source setting; the old "AutoDeduceRelations" key in a
    /// settings file is ignored.
    /// </summary>
    public static bool AutoDeduceRelations { get; set; } = false;

    /// <summary>
    /// Whether a namespace matches a source's <see cref="Namespace"/>. The pattern is an exact namespace,
    /// or one with <c>*</c> wildcards, each standing for any run of characters (dots included):
    /// <c>MyApp.Models.*</c> takes every namespace under MyApp.Models - and MyApp.Models itself, since a
    /// trailing <c>.*</c> also matches the prefix alone; <c>MyApp.*.Models</c> takes MyApp.Web.Models and
    /// MyApp.Api.Models. A null or empty pattern matches nothing. Case sensitive, like C# namespaces.
    /// </summary>
    public static bool NamespaceMatches(string? pattern, string? ns) {
        if (string.IsNullOrEmpty(pattern)) return false;
        ns ??= "";
        if (!HasWildcard(pattern)) return string.Equals(pattern, ns, StringComparison.Ordinal);
        if (pattern.EndsWith(".*", StringComparison.Ordinal) && glob(pattern[..^2], ns)) return true;
        return glob(pattern, ns);
    }
    /// <summary>Whether a source namespace names several namespaces with <c>*</c> rather than one.</summary>
    public static bool HasWildcard(string? pattern) => pattern != null && pattern.Contains('*');
    /// <summary>
    /// The concrete namespace a wildcard pattern starts with - the part before the first <c>*</c>, without a
    /// trailing dot - which is where a type written into the source goes. An exact pattern is returned as is;
    /// a pattern starting with a wildcard gives null.
    /// </summary>
    public static string? NamespaceBase(string? pattern) {
        if (string.IsNullOrEmpty(pattern)) return null;
        var star = pattern.IndexOf('*');
        if (star < 0) return pattern;
        var head = pattern[..star].TrimEnd('.');
        return head.Length == 0 ? null : head;
    }
    // '*' matches any run of characters; iterative so a pattern with several stars stays linear
    static bool glob(string pattern, string text) {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length) {
            if (p < pattern.Length && pattern[p] == '*') { starP = p++; starT = t; }
            else if (p < pattern.Length && pattern[p] == text[t]) { p++; t++; }
            else if (starP >= 0) { p = starP + 1; t = ++starT; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Text files holding datamodel JSON.</summary>
    public bool IsJsonFiles => Type == DatamodelSourceType.TextFiles && FileFormat == DatamodelSourceFileFormat.Json;
    /// <summary>Text files holding C# model classes, compiled when the database opens.</summary>
    public bool IsCSharpFiles => Type == DatamodelSourceType.TextFiles && FileFormat == DatamodelSourceFileFormat.CSharpCode;
}
