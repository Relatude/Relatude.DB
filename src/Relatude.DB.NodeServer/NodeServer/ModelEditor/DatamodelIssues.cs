using Relatude.DB.Datamodels;

namespace Relatude.DB.NodeServer.ModelEditor;

public enum IssueSeverity { Error, Warning, Info }

/// <summary>
/// One finding about a draft model. Errors stop an activation; warnings need the person activating
/// to accept them; info is context. The ids say what the finding is about, so the editor can take
/// the reader there.
/// </summary>
public sealed class DatamodelIssue {
    public required IssueSeverity Severity { get; init; }
    /// <summary>A short stable code for the kind of finding, e.g. "duplicate-name", "read-only-source".</summary>
    public required string Code { get; init; }
    public required string Message { get; init; }
    public Guid? NodeTypeId { get; init; }
    public Guid? PropertyId { get; init; }
    public Guid? RelationId { get; init; }
    public Guid? SourceId { get; init; }
    public string? File { get; init; }
    public static DatamodelIssue Error(string code, string message, Guid? nodeType = null, Guid? property = null, Guid? relation = null, Guid? source = null, string? file = null)
        => new() { Severity = IssueSeverity.Error, Code = code, Message = message, NodeTypeId = nodeType, PropertyId = property, RelationId = relation, SourceId = source, File = file };
    public static DatamodelIssue Warning(string code, string message, Guid? nodeType = null, Guid? property = null, Guid? relation = null, Guid? source = null, string? file = null)
        => new() { Severity = IssueSeverity.Warning, Code = code, Message = message, NodeTypeId = nodeType, PropertyId = property, RelationId = relation, SourceId = source, File = file };
    public static DatamodelIssue Info(string code, string message, Guid? nodeType = null, Guid? property = null, Guid? relation = null, Guid? source = null, string? file = null)
        => new() { Severity = IssueSeverity.Info, Code = code, Message = message, NodeTypeId = nodeType, PropertyId = property, RelationId = relation, SourceId = source, File = file };
}

public enum PlannedFileAction { Write, Delete }

/// <summary>One file an activation writes or deletes in a datamodel source.</summary>
public sealed class PlannedFile {
    public required Guid SourceId { get; init; }
    /// <summary>The absolute path, or "provider:{ioId}/{key}" for a source read through a storage provider.</summary>
    public required string Path { get; init; }
    /// <summary>The path relative to the source's folder, which is also what the model stamps on its types.</summary>
    public required string RelativePath { get; init; }
    public required PlannedFileAction Action { get; init; }
    /// <summary>The new content of a written file; null for a delete.</summary>
    public string? Content { get; init; }
    public bool Exists { get; init; }
    /// <summary>False when the file already holds exactly this content, in which case it is left untouched.</summary>
    public bool Changed { get; init; }
    /// <summary>
    /// In a generated folder (<see cref="DatamodelSource.GenerateModelFile"/>): the file that is deleted
    /// or overwritten does not start with the generated-code marker, so somebody wrote it by hand. The
    /// plan reports these as a warning, which the activation needs accepted.
    /// </summary>
    public bool HandWritten { get; init; }
    public List<Guid> NodeTypeIds { get; init; } = [];
    public List<Guid> RelationIds { get; init; } = [];
    /// <summary>The IO provider and key of a provider-backed file, when the source reads through one.</summary>
    public Guid? IoId { get; init; }
    public string[]? IoKey { get; init; }
}

/// <summary>What an activation changes in one datamodel source.</summary>
public sealed class SourceChange {
    public required Guid SourceId { get; init; }
    public required string Name { get; init; }
    public required DatamodelSourceType Type { get; init; }
    public bool Writable { get; init; }
    public string? ReadOnlyReason { get; init; }
    /// <summary>The source is compiled into the application: written changes take effect after a rebuild and restart.</summary>
    public bool RequiresRebuild { get; init; }
    /// <summary>The source's code folder is generated as a whole at every activation (<see cref="DatamodelSource.GenerateModelFile"/>).</summary>
    public bool Generated { get; init; }
    /// <summary>The source is in the active model but not in the draft: it leaves the settings, its files stay.</summary>
    public bool Removed { get; init; }
    /// <summary>The source is in the draft but not yet in the settings.</summary>
    public bool Added { get; init; }
    public List<Guid> AddedTypes { get; } = [];
    public List<Guid> ChangedTypes { get; } = [];
    public List<Guid> RemovedTypes { get; } = [];
    public List<Guid> AddedRelations { get; } = [];
    public List<Guid> ChangedRelations { get; } = [];
    public List<Guid> RemovedRelations { get; } = [];
    public bool HasModelChanges => AddedTypes.Count + ChangedTypes.Count + RemovedTypes.Count + AddedRelations.Count + ChangedRelations.Count + RemovedRelations.Count > 0;
}

/// <summary>Everything an activation would do to the sources, before it does any of it.</summary>
public sealed class SourceWritePlan {
    public List<PlannedFile> Files { get; } = [];
    public List<SourceChange> Sources { get; } = [];
    public List<DatamodelIssue> Issues { get; } = [];
    /// <summary>
    /// A compiled source gets model changes, or (a generated folder) any file change at all - a stray
    /// file deleted from a generated folder may have declared a type the running application still has.
    /// </summary>
    public bool RequiresRebuild => Sources.Any(s => s.RequiresRebuild && (s.HasModelChanges || Files.Any(f => f.SourceId == s.SourceId && f.Changed)));
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
    /// <summary>Whether the sources listed in the settings change: a source added, removed or edited.</summary>
    public bool SettingsChange { get; set; }
}

/// <summary>The outcome of validating a draft, with the write plan when the draft is structurally sound.</summary>
public sealed class DatamodelValidation {
    public List<DatamodelIssue> Issues { get; } = [];
    public SourceWritePlan? Plan { get; set; }
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warning);
    public bool RequiresRebuild => Plan?.RequiresRebuild ?? false;
    /// <summary>Whether the dry run - writing to a scratch folder, loading and compiling the result - ran.</summary>
    public bool Compiled { get; set; }
    public Guid DraftChecksum { get; set; }
    public Guid ActiveChecksum { get; set; }
    /// <summary>The initialized draft, when it initialized; what the writer and the activator work on.</summary>
    internal Datamodel? Draft { get; set; }
    /// <summary>The model the draft is compared against: what the configured sources say right now.</summary>
    internal Datamodel? Active { get; set; }
}
