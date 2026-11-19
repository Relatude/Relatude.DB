using Relatude.DB.Datamodels;

namespace Relatude.DB.DataStores;

/// <summary>
/// What a change did to one version of a node, as the history reader reads it out of the log: flags, as one save
/// can do several (a draft published and changed at once). A version is a revision, or the node itself when it
/// has none. Links made and taken away are <see cref="Linked"/> and <see cref="Unlinked"/>.
/// </summary>
[Flags]
public enum LogChangeKinds : ushort {
    None = 0,
    /// <summary>The version is new: the node was made, or a revision was added to it.</summary>
    Created = 1,
    /// <summary>Its values changed.</summary>
    Updated = 2,
    /// <summary>The version is gone: the node was deleted for good, or the revision was deleted.</summary>
    Removed = 4,
    /// <summary>It became what readers see.</summary>
    Published = 8,
    /// <summary>It stopped being what readers see, the version kept (archived, made a draft again).</summary>
    Unpublished = 16,
    /// <summary>It was moved to another language.</summary>
    LanguageMoved = 32,
    /// <summary>The node was marked deleted (put in a wastebin).</summary>
    MarkedDeleted = 64,
    /// <summary>The node's deleted mark was taken away (restored from a wastebin).</summary>
    Unmarked = 128,
    /// <summary>Its access, site, release, expiry or address changed.</summary>
    MetaChanged = 256,
    /// <summary>The node itself was made in the save (with <see cref="Created"/>).</summary>
    NodeCreated = 512,
    /// <summary>The node itself was deleted for good in the save (with <see cref="Removed"/>).</summary>
    NodeRemoved = 1024,
    /// <summary>A write the database made for itself (indexing, file analysis, measuring), not a person.</summary>
    System = 2048,
    /// <summary>A link was made.</summary>
    Linked = 4096,
    /// <summary>A link was taken away.</summary>
    Unlinked = 8192,
    /// <summary>Every kind a change or a link can be.</summary>
    All = 0x3FFF,
}

/// <summary>What the bars of a <see cref="LogActivity"/> are split by.</summary>
public enum LogActivityBy { None, Kind, Type, Writer, Culture, Collection }
/// <summary>How wide each bar of a <see cref="LogActivity"/> is. Weeks start on Monday; all in UTC.</summary>
public enum LogActivityInterval { Hour, Day, Week, Month }

/// <summary>Where the change history is read from, and how far it has got.</summary>
public sealed class LogHistoryStatus {
    /// <summary>Whether this store keeps a change history at all (a remote store does not).</summary>
    public bool Supported { get; init; }
    /// <summary>Whether recording is switched on (<c>LocalSettings.History.Enabled</c>).</summary>
    public bool Enabled { get; init; }
    /// <summary>"secondary" when the secondary log is read, else "files": the numbered log files kept.</summary>
    public string? Source { get; init; }
    /// <summary>The oldest change in history, which is as far back as the log files kept reach.</summary>
    public DateTime? ReachesBackToUtc { get; init; }
    /// <summary>The newest change read.</summary>
    public DateTime? LatestUtc { get; init; }
    /// <summary>Rows of changed versions in the index.</summary>
    public long Changes { get; init; }
    /// <summary>Rows of links made and taken away in the index.</summary>
    public long Links { get; init; }
    /// <summary>The bytes the index takes on the index storage.</summary>
    public long IndexBytes { get; init; }
    /// <summary>True while the reader is filling the index from log files it has not read before (switched on, rebuilt).</summary>
    public bool Building { get; init; }
    /// <summary>How much of the log the index has read, 0 to 100.</summary>
    public int Percent { get; init; }
    /// <summary>Bytes of the log not read yet.</summary>
    public long BytesBehind { get; init; }
    /// <summary>When the reader last finished a run.</summary>
    public DateTime? LastRunUtc { get; init; }
    /// <summary>What went wrong at the last run, if anything did.</summary>
    public string? Problem { get; init; }
    public LogHistorySourceStatus[] Sources { get; init; } = [];
    public static LogHistoryStatus NotSupported { get; } = new() { Supported = false };
}
/// <summary>One log file the history is read from.</summary>
public sealed class LogHistorySourceStatus {
    public required string Key { get; init; }
    public long Length { get; init; }
    public long ReadTo { get; init; }
    public long Changes { get; init; }
    public long Links { get; init; }
    public DateTime? FirstUtc { get; init; }
    public DateTime? LastUtc { get; init; }
}

/// <summary>
/// Which changes <see cref="IDataStore.ReadLogChanges"/> lists. Every filter is optional and they all apply
/// together. Changes come newest first unless <see cref="NewestFirst"/> is false, a page at a time: the next page
/// is asked for with the page's <see cref="LogChangePage.Next"/> as <see cref="Cursor"/>.
/// </summary>
public sealed class LogChangeQuery {
    /// <summary>From this moment (inclusive).</summary>
    public DateTime? FromUtc { get; set; }
    /// <summary>Up to this moment (exclusive).</summary>
    public DateTime? ToUtc { get; set; }
    /// <summary>One transaction alone, by its log timestamp: what "show all" of a collapsed save asks for.</summary>
    public long? Transaction { get; set; }
    public bool NewestFirst { get; set; } = true;
    /// <summary>Where the last page ended: its <see cref="LogChangePage.Next"/>.</summary>
    public string? Cursor { get; set; }
    /// <summary>How many entries (changes and links together) a page holds, 1 to 500.</summary>
    public int Take { get; set; } = 50;
    /// <summary>Changes to nodes of these types, and of the types inheriting from them.</summary>
    public Guid[]? NodeTypes { get; set; }
    /// <summary>Changes made by these people. Guid.Empty asks for those the log does not say who made.</summary>
    public Guid[]? Writers { get; set; }
    public Guid[]? Nodes { get; set; }
    public Guid[]? Cultures { get; set; }
    public Guid[]? Collections { get; set; }
    /// <summary>Links of these relations. Without it every relation's links are listed.</summary>
    public Guid[]? Relations { get; set; }
    /// <summary>The kinds asked for: a change with any of them, a link of <see cref="LogChangeKinds.Linked"/> or <see cref="LogChangeKinds.Unlinked"/>.</summary>
    public LogChangeKinds Kinds { get; set; } = LogChangeKinds.All;
    /// <summary>Whether links are listed besides the changes.</summary>
    public bool WithLinks { get; set; } = true;
    /// <summary>Whether writes the database makes for itself (indexing, analysis, measuring) are listed.</summary>
    public bool IncludeSystemWrites { get; set; }
    /// <summary>
    /// One entry per node and save: the versions a save changed of one node are one entry, its kinds together. A save
    /// that changed more than <see cref="CollapseAbove"/> nodes is one entry, with <see cref="LoggedChange.Others"/>
    /// and <see cref="LoggedChange.TypeCounts"/> saying what else it changed.
    /// </summary>
    public bool Collapse { get; set; }
    public int CollapseAbove { get; set; } = 12;
    /// <summary>Names and changed properties, read from the log for the page's entries.</summary>
    public bool WithDetails { get; set; }
}
/// <summary>A page of history: changes and links newest first (oldest first when asked), merged by their timestamps,
/// a save's changes before its links.</summary>
public sealed class LogChangePage {
    public LoggedChange[] Changes { get; init; } = [];
    public LoggedRelationChange[] Links { get; init; } = [];
    /// <summary>What to ask for the next page with; null when there is none.</summary>
    public string? Next { get; init; }
}
/// <summary>One version of one node changed by one save, as the log says.</summary>
public sealed record LoggedChange(
    long Timestamp,
    DateTime Utc,
    Guid NodeId,
    Guid NodeType,
    Guid RevisionId,
    LogChangeKinds Kind,
    Guid? Writer,
    Guid? Culture,
    Guid? Collection,
    RevisionType? TypeAfter,
    RevisionType? TypeBefore,
    int InTransaction) {
    /// <summary>The node's name in the version (read from the log with <see cref="LogChangeQuery.WithDetails"/>).</summary>
    public string? Name { get; init; }
    /// <summary>The properties whose values the save changed (with <see cref="LogChangeQuery.WithDetails"/>).</summary>
    public Guid[]? ChangedProperties { get; init; }
    /// <summary>Whether the version can still be read from the log (its file is there).</summary>
    public bool VersionKept { get; init; } = true;
    /// <summary>With <see cref="LogChangeQuery.Collapse"/>: how many versions of the node the save changed.</summary>
    public int Versions { get; init; } = 1;
    /// <summary>With <see cref="LogChangeQuery.Collapse"/>: how many other nodes the save changed, for an entry that stands for them all.</summary>
    public int Others { get; init; }
    /// <summary>With <see cref="LogChangeQuery.Collapse"/>: the nodes the save changed, by type, for an entry that stands for them all.</summary>
    public LogTypeCount[]? TypeCounts { get; init; }
    /// <summary>Where the version is in the log, for <see cref="IDataStore.FindVersionAt"/>.</summary>
    public string? Source { get; init; }
}
public sealed record LogTypeCount(Guid NodeType, int Count);
/// <summary>One link made or taken away by one save. Relations name nodes by ids the history reader turns into the nodes' own.</summary>
public sealed record LoggedRelationChange(
    long Timestamp,
    DateTime Utc,
    Guid RelationId,
    Guid Source,
    Guid Target,
    bool Added,
    Guid? Writer,
    int InTransaction) {
    public Guid SourceType { get; init; }
    public Guid TargetType { get; init; }
}

/// <summary>What <see cref="IDataStore.AnalyseLogChanges"/> counts: the filters of <see cref="LogChangeQuery"/>, and bars.</summary>
public sealed class LogActivityQuery {
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public LogActivityInterval Interval { get; set; } = LogActivityInterval.Day;
    public LogActivityBy By { get; set; } = LogActivityBy.None;
    public Guid[]? NodeTypes { get; set; }
    public Guid[]? Writers { get; set; }
    public Guid[]? Nodes { get; set; }
    public Guid[]? Cultures { get; set; }
    public Guid[]? Collections { get; set; }
    public Guid[]? Relations { get; set; }
    public LogChangeKinds Kinds { get; set; } = LogChangeKinds.All;
    public bool WithLinks { get; set; } = true;
    public bool IncludeSystemWrites { get; set; }
    /// <summary>How many groups of <see cref="By"/> are kept, the biggest; the rest are counted as "other".</summary>
    public int TopGroups { get; set; } = 8;
}
/// <summary>How much changed when, in bars.</summary>
public sealed class LogActivity {
    public LogActivityInterval Interval { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public LogActivityBy By { get; init; }
    public LogActivityBucket[] Buckets { get; init; } = [];
    public LogActivityCounts Totals { get; init; } = new();
    /// <summary>The groups of <see cref="By"/> over the whole period, biggest first.</summary>
    public LogActivityGroup[] Groups { get; init; } = [];
}
public sealed class LogActivityCounts {
    /// <summary>Changed versions.</summary>
    public int Changes { get; set; }
    /// <summary>Saves (transactions) with a change or a link.</summary>
    public int Saves { get; set; }
    /// <summary>People who made them, those the log does not name counted as none.</summary>
    public int People { get; set; }
    /// <summary>Nodes changed.</summary>
    public int Nodes { get; set; }
    /// <summary>Links made or taken away.</summary>
    public int Links { get; set; }
}
public sealed class LogActivityBucket {
    public DateTime StartUtc { get; init; }
    public LogActivityCounts Counts { get; init; } = new();
    /// <summary>The changes of the bar by <see cref="LogActivityQuery.By"/>: the keys of <see cref="LogActivity.Groups"/>, "other" for the rest.</summary>
    public Dictionary<string, int>? Groups { get; init; }
}
/// <summary>A group: a kind's name, or a type's, person's, language's or site's id.</summary>
public sealed record LogActivityGroup(string Key, int Changes);
