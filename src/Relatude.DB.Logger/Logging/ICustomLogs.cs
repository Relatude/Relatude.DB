using Relatude.DB.IO;

namespace Relatude.DB.Logging;
/// <summary>
/// Logs defined while the application runs rather than in code: each one is a <see cref="LogSettings"/>
/// kept as a json file ({key}.json), so a log defined in the admin UI is there again after a restart, and
/// one written by hand or copied from another database is picked up when the database opens. On a server
/// the files are in two folders: SETTINGS (in relatude.settings, deployed with the application) and DATA
/// (this installation's, where the admin UI saves); see <see cref="LayeredDefinitionFolders"/>.
///
/// Recording is the only part an application needs:
/// <code>
///   store.CustomLogs.Record("orders", ("amount", 12.5), ("customer", "acme"));
///   store.CustomLogs.RecordObject("orders", new { amount = 12.5, customer = "acme" });
/// </code>
/// A value is converted to the type its column declares (see <see cref="LogValues"/>), and a log
/// that does not exist, or is turned off, costs a dictionary lookup.
/// </summary>
public interface ICustomLogs {
    /// <summary>The store the custom logs live in: every read, search and statistic goes through it.</summary>
    ILogStore LogStore { get; }
    /// <summary>The SETTINGS folder, for people: relatude.settings/[short name]/logs on a server; null when there is none.</summary>
    string? SettingsFolder { get; }
    /// <summary>The DATA folder, where every change made here is saved, for people.</summary>
    string DataFolder { get; }
    /// <summary>The file a log's definition in force is kept in, for people.</summary>
    string DefinitionFileOf(string logKey);
    /// <summary>Which folder the definition in force comes from; null for a log that is not there.</summary>
    DefinitionSource? SourceOf(string logKey);
    /// <summary>What DATA holds, definition by definition: logs added, changed or taken away here.</summary>
    IReadOnlyList<CustomLogDataEntry> DataEntries { get; }
    /// <summary>Copies of every definition, ordered by name. Changing one changes nothing: use <see cref="Update"/>.</summary>
    IReadOnlyList<LogSettings> GetDefinitions();
    /// <summary>A copy of one definition, or null when there is no log with the key.</summary>
    LogSettings? GetDefinition(string logKey);
    bool HasLog(string logKey);
    /// <summary>Whether the log records entries or statistics (false for a log that does not exist).</summary>
    bool IsEnabled(string logKey);
    /// <summary>Whether any custom log records anything, which is what decides whether they are flushed.</summary>
    bool AnyEnabled { get; }
    /// <summary>The keys of the system logs, which a custom log may not take.</summary>
    IReadOnlyCollection<string> ReservedKeys { get; }
    /// <summary>Why a key cannot be given to a new log, or null when it can.</summary>
    string? CheckNewKey(string logKey);
    /// <summary>The definition files that could not be read as a log, and why - listed under the name
    /// <see cref="ReadBrokenDefinition"/> and the others take.</summary>
    IReadOnlyList<CustomLogLoadError> LoadErrors { get; }

    /// <summary>Defines a new log and saves its settings file. Throws an ArgumentException for settings
    /// or a key that cannot be used.</summary>
    void Create(LogSettings settings);
    /// <summary>What <see cref="Update"/> would do to what the log has recorded, without doing it.</summary>
    CustomLogChanges PlanUpdate(LogSettings settings);
    /// <summary>
    /// Changes a log's definition (the key says which log, and cannot change). What it has recorded is
    /// kept: a new file interval moves the entries into files of the new size, and a column whose type
    /// changed is read as the new type. <paramref name="rebuildStatistics"/> aggregates the statistics
    /// again from the entries, which is how statistics added to a log cover what it recorded before.
    /// </summary>
    CustomLogChanges Update(LogSettings settings, bool rebuildStatistics = false);
    /// <summary>Turns recording, statistics, or both on or off, and saves it. Omitted switches are left alone.</summary>
    void SetEnabled(string logKey, bool? log, bool? statistics);
    /// <summary>Removes a log's definition - a log SETTINGS defines is taken away on this installation by a
    /// marker in DATA; <paramref name="deleteRecorded"/> deletes its entries and statistics too. Kept, they
    /// are picked up again by a log created later with the same key.</summary>
    void Delete(string logKey, bool deleteRecorded);
    /// <summary>Moves what DATA holds for these logs into SETTINGS: definitions written there, markers taking
    /// their SETTINGS file away. Nothing that runs changes. Returns how many were moved.</summary>
    int MoveToSettings(IEnumerable<string> logKeys);
    /// <summary>Drops what DATA holds for these logs: each goes back to its SETTINGS definition (entries
    /// moved as an update would), comes back when DATA took it away, or goes when only DATA had it - what
    /// it recorded is kept. Returns the keys discarded.</summary>
    IReadOnlyList<string> DiscardData(IEnumerable<string> logKeys);
    /// <summary>The files the log has in its folder of the log storage (log/{key}/): entries, text
    /// copies, statistics, and entries left behind in another file layout. Its definition is kept
    /// elsewhere (<see cref="DefinitionFileOf"/>).</summary>
    IReadOnlyList<CustomLogFile> GetFiles(string logKey);

    /// <summary>The text of a settings file that could not be read, so it can be looked at and fixed.</summary>
    string ReadBrokenDefinition(string fileKey);
    /// <summary>Replaces a settings file that could not be read with settings that can, and starts the log.</summary>
    void RepairBrokenDefinition(string fileKey, string json);
    /// <summary>Deletes a settings file that could not be read.</summary>
    void DeleteBrokenDefinition(string fileKey);
    /// <summary>Reads every settings file again, for settings changed on disk while the database runs.</summary>
    void Reload();

    /// <summary>Records one entry. False when there is no log with the key.</summary>
    bool Record(string logKey, LogEntry entry, bool flushToDisk = false);
    /// <summary>Records one entry, timestamped now, from property-value pairs. Null values are left out.</summary>
    bool Record(string logKey, params (string Property, object? Value)[] values);
    /// <summary>Records one entry from a dictionary of values, timestamped now unless a time is given.</summary>
    bool Record(string logKey, IEnumerable<KeyValuePair<string, object?>> values, DateTime? timestampUtc = null);
    /// <summary>Records one entry from the public properties of an object: an anonymous object is the
    /// usual one. Property names are matched to the columns without regard to case.</summary>
    bool RecordObject(string logKey, object values, DateTime? timestampUtc = null);

    void FlushToDiskNow();
    /// <summary>Saves the statistics and enforces every log's age and size limits.</summary>
    void SaveStatsAndDeleteExpiredData();
    long GetTotalFileSize();
}

/// <summary>A definition file that could not be read as a log.</summary>
public sealed record CustomLogLoadError(string FileKey, string Message);

/// <summary>What DATA holds for one log, beside where SETTINGS has it.</summary>
public sealed record CustomLogDataEntry(string Key, string? Name, DataChange Change, string DataFile, string? SettingsFile);

/// <summary>One file a custom log has in its folder of the log storage.</summary>
/// <param name="Kind">"entries", "text", "statistics", "statistics-backup", or
/// "left-over": entries written in a file layout the log no longer uses, which it cannot read.</param>
public sealed record CustomLogFile(string FileKey, string Kind, long Size);

/// <summary>
/// What changing a log's definition does to what it has recorded, said in sentences, so the change
/// can be confirmed knowing what it costs. <see cref="ICustomLogs.PlanUpdate"/> fills it in without
/// doing anything; <see cref="ICustomLogs.Update"/> returns the same, with what it did.
/// </summary>
public sealed class CustomLogChanges {
    /// <summary>False when the definition is the one the log already has.</summary>
    public bool Changed { get; internal set; }
    /// <summary>The file interval changed and there are entries, so every one of them is read and written again.</summary>
    public bool MovesEntries { get; internal set; }
    /// <summary>A column with statistics changed type: its statistics no longer read, so they are
    /// rebuilt from the entries (or deleted, while statistics are off).</summary>
    public bool DiscardsStatistics { get; internal set; }
    /// <summary>Statistics were added or restart, and there are entries they could be rebuilt from.</summary>
    public bool CanRebuildStatistics { get; internal set; }
    /// <summary>Set by <see cref="ICustomLogs.Update"/>: how many entries were moved.</summary>
    public int EntriesMoved { get; internal set; }
    /// <summary>Set by <see cref="ICustomLogs.Update"/>: whether the statistics were rebuilt from the entries.</summary>
    public bool StatisticsRebuilt { get; internal set; }
    /// <summary>What the change does, one sentence per consequence, in the order worth reading them.</summary>
    public List<string> Notes { get; } = [];
}
