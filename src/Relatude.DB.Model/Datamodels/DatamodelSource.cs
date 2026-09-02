namespace Relatude.DB.Datamodels;

public enum DatamodelSourceType {
    AssemblyNameReference = 0,
    TypeNameReference = 1,
    JsonFile = 2,
    CSharpCodeFile = 3,
    /// <summary>
    /// Reserved for model types added directly from code at startup (for example in the OnDatamodelInit event).
    /// Cannot be used as a configured source in settings.
    /// </summary>
    Code = 4,
}
public class DatamodelSource {
    /// <summary>
    /// The source id of all model types added directly from code (outside any configured datamodel source).
    /// </summary>
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
    public string? Filepath { get; set; }
    public string? Reference { get; set; }
    public Guid? FileIO { get; set; }
    /// <summary>
    /// When false the source is skipped entirely: nothing it defines reaches the datamodel, and it is
    /// not registered on it either. A source added from the admin UI starts turned off, so a settings
    /// file is never left holding a half configured source that would stop the database from opening.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// When true, plain node-typed properties (and collections of node types) without an
    /// explicit relation are turned into auto-created relations, matching the old behavior.
    /// When false (default), such properties become Reference/References properties instead.
    /// </summary>
    public bool AutoDeduceRelations { get; set; } = false;
}