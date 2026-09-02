using Relatude.DB.FileConversion.ImageEncoders;
using Relatude.DB.Native;
using Relatude.DB.Web;
namespace Relatude.DB.DataStores;

public class SettingsLocal {

    public string? DefaultCultureCode { get; set; } = null;  // culture code if culture ID is Guid.Empty or Null
    public SystemGroupType DefaultReadAccess { get; set; } = SystemGroupType.Everyone;
    public SystemGroupType DefaultWriteAccess { get; set; } = SystemGroupType.Everyone;
    /// <summary>The file store new uploads go to when the code names none. Null means the implicit
    /// store: a MultiFile store on the database's own IO provider, created on demand.</summary>
    public Guid? DefaultFileStore { get; set; }
    public bool ThrowOnBadLogFile { get; set; } = false;
    public bool ThrowOnBadStateFile { get; set; } = false;

    public bool WriteSystemLogConsole { get; set; } = true;

    /// <summary>
    /// Which activity logs record, and which of them aggregate statistics. The logger starts with
    /// every log off, so this is what carries a log turned on in the admin UI across a restart.
    /// Written by the Logs section ("Save and remember changes"); a log not listed stays off.
    /// </summary>
    public LogRecordingSettings[]? LogRecording { get; set; }
    /// <summary>Queries faster than this are left out of the query log. 0 records every query.</summary>
    public int MinQueryDurationMsBeforeLogging { get; set; } = 0;

    public bool DoNotCacheMapperFile { get; set; } = false;
    public double NodeCacheSizeGb { get; set; } = 1;
    public double SetCacheSizeGb { get; set; } = 1;

    public bool FlushDiskOnEveryTransactionByDefault { get; set; } = false;
    public int ForceDiskFlushAfterActionCountLimit { get; set; } = 10000; // to reduce memory usage, but avoid flushing too often (latency)
    public bool DeepFlushDisk { get; set; } = false;
    public bool AutoFlushDiskInBackground { get; set; } = true;
    public double AutoFlushDiskIntervalInSeconds { get; set; } = 1;
    public bool DelayAutoDiskFlushIfBusy { get; set; } = true;
    public double MaxDelayAutoDiskFlushIfBusyInSeconds { get; set; } = 15;

    public int BusyThresholdActivitiesLast10Sec { get; set; } = 100;
    public int BusyThresholdQueriesLast10Sec { get; set; } = 1000;

    public bool AutoSaveIndexStates { get; set; } = true;
    public double AutoSaveIndexStatesIntervalInMinutes { get; set; } = 120;
    public int AutoSaveIndexStatesActionCountLowerLimit { get; set; } = 50000;
    public int AutoSaveIndexStatesActionCountUpperLimit { get; set; } = 200000;

    public bool AutoBackUp { get; set; } = false;
    public int NoHourlyBackUps { get; set; } = 10;
    public int NoDailyBackUps { get; set; } = 10;
    public int NoWeeklyBackUps { get; set; } = 4;
    public int NoMontlyBackUps { get; set; } = 12;
    public int NoYearlyBackUps { get; set; } = 10;
    public bool TruncateBackups { get; set; } = false;

    public bool SecondaryBackupLog { get; set; } = false;

    public bool AutoTruncate { get; set; } = false; //true;
    public double AutoTruncateIntervalInMinutes { get; set; } = 240;
    public int AutoTruncateActionCountLowerLimit { get; set; } = 100000;
    public bool AutoTruncateDeleteOldFileOnSuccess { get; set; } = false; //true;

    public bool AutoPurgeCache { get; set; } = true;
    public double AutoPurgeCacheIntervalInMinutes { get; set; } = 5;
    public double AutoPurgeCacheLowerSizeLimitInMb { get; set; } = 1;

    /// <summary>
    /// The persisted engines this database may run its value indexes on. An entry is configuration
    /// only until something points at it: today that is <see cref="DefaultValueIndex"/>, so the
    /// engines the defaults name are the ones actually created. See <see cref="IndexEngineSettings"/>.
    /// </summary>
    public IndexEngineSettings[]? ValueIndexes { get; set; }
    /// <summary>The persisted engines for the full-text word indexes, see <see cref="ValueIndexes"/>.</summary>
    public IndexEngineSettings[]? TextIndexes { get; set; }
    /// <summary>The persisted engines for the semantic (vector) indexes, see <see cref="ValueIndexes"/>.
    /// Semantic indexes only exist on a database with an AI provider configured.</summary>
    public IndexEngineSettings[]? VectorIndexes { get; set; }
    /// <summary>
    /// The engine, by <see cref="IndexEngineSettings.Id"/>, behind every value index that does not say
    /// otherwise. <see cref="Guid.Empty"/> means the memory index: everything resident, saved with the
    /// state snapshot and otherwise rebuilt from the log at every open.
    /// </summary>
    public Guid DefaultValueIndex { get; set; }
    /// <summary>The same for the word indexes, see <see cref="DefaultValueIndex"/>.</summary>
    public Guid DefaultTextIndex { get; set; }
    /// <summary>The same for the semantic indexes, see <see cref="DefaultValueIndex"/>.</summary>
    public Guid DefaultVectorIndex { get; set; }
    public string? PersistedValueIndexFolderPath { get; set; }

    public bool EnableTextIndexByDefault { get; set; } = false;
    public bool EnableSemanticIndexByDefault { get; set; } = false;
    public bool EnableInstantTextIndexingByDefault { get; set; } = false;

    public bool AutoDequeTasks { get; set; } = true;
    public PersistedQueueStoreEngine PersistedQueueStoreEngine { get; set; } = PersistedQueueStoreEngine.Native;
    public string? PersistedQueueStoreFolderPath { get; set; }
    public DefaultUrlManagerOptions? UrlOptions { get; set; } = new();

    public ImageDefaultFormat ImageDefaultFormat { get; set; } = ImageDefaultFormat.Jpeg;
    public int ImageDefaultQuality { get; set; } = 85;

    /// <summary>
    /// Settings with one Native value engine and one Native text engine, and the defaults pointing at
    /// them - what a database created by the server or the CLI starts with. A plain
    /// <c>new SettingsLocal()</c> has no engines and keeps every index in memory.
    /// </summary>
    public static SettingsLocal CreateWithNativeEngines() {
        var value = new IndexEngineSettings { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Native };
        var text = new IndexEngineSettings { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Native };
        return new SettingsLocal {
            ValueIndexes = [value],
            TextIndexes = [text],
            DefaultValueIndex = value.Id,
            DefaultTextIndex = text.Id,
        };
    }

    /// <summary>The value engine <see cref="DefaultValueIndex"/> names, or null when it is the memory index.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IndexEngineSettings? DefaultValueEngine => FindIndexEngine(ValueIndexes, DefaultValueIndex);
    /// <summary>The text engine <see cref="DefaultTextIndex"/> names, or null when it is the memory index.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IndexEngineSettings? DefaultTextEngine => FindIndexEngine(TextIndexes, DefaultTextIndex);
    /// <summary>The vector engine <see cref="DefaultVectorIndex"/> names, or null when it is the memory index.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IndexEngineSettings? DefaultVectorEngine => FindIndexEngine(VectorIndexes, DefaultVectorIndex);

    public static IndexEngineSettings? FindIndexEngine(IndexEngineSettings[]? engines, Guid id) {
        if (id == Guid.Empty || engines == null) return null;
        foreach (var e in engines) if (e.Id == id) return e;
        return null;
    }

    /// <summary>
    /// Throws when the engine configuration cannot be run: an engine without an id, an id used twice
    /// (across all three lists, since an id names a folder), a negative memory budget, or a default
    /// naming an engine its list does not have. Whether a type name resolves is checked where the
    /// engines are created. Called when a data store is constructed, so a bad file fails at open
    /// with the setting named rather than deep inside index creation.
    /// </summary>
    public void ValidateIndexEngines() {
        var seen = new HashSet<Guid>();
        check(ValueIndexes, nameof(ValueIndexes));
        check(TextIndexes, nameof(TextIndexes));
        check(VectorIndexes, nameof(VectorIndexes));
        requireDefault(DefaultValueIndex, ValueIndexes, nameof(DefaultValueIndex), nameof(ValueIndexes));
        requireDefault(DefaultTextIndex, TextIndexes, nameof(DefaultTextIndex), nameof(TextIndexes));
        requireDefault(DefaultVectorIndex, VectorIndexes, nameof(DefaultVectorIndex), nameof(VectorIndexes));
        void check(IndexEngineSettings[]? engines, string list) {
            if (engines == null) return;
            foreach (var e in engines) {
                if (e == null) throw new Exception(list + " holds an empty entry. ");
                if (e.Id == Guid.Empty) throw new Exception(list + " holds an engine without an Id (" + e + "). Every engine needs its own id. ");
                if (!seen.Add(e.Id)) throw new Exception("The index engine id " + e.Id + " is used more than once. An id names the engine's folder, so each engine needs its own. ");
                if (string.IsNullOrWhiteSpace(e.TypeName)) throw new Exception(list + " holds an engine without a TypeName (" + e.Id + "). ");
                if (e.MaxMemoryUsageInMb < 0) throw new Exception(list + ": " + e + " has a negative MaxMemoryUsageInMb. 0 is the smallest budget. ");
            }
        }
        static void requireDefault(Guid id, IndexEngineSettings[]? engines, string setting, string list) {
            if (id == Guid.Empty || FindIndexEngine(engines, id) != null) return;
            var known = engines == null || engines.Length == 0 ? "the list is empty"
                : "configured: " + string.Join(", ", engines.Select(e => e.Id + " (" + e + ")"));
            throw new Exception(setting + " is " + id + ", which is not in " + list + " - " + known + ". Use Guid.Empty for the memory index. ");
        }
    }

}

/// <summary>
/// One persisted index engine a database may run indexes on, listed in <see cref="SettingsLocal.ValueIndexes"/>,
/// <see cref="SettingsLocal.TextIndexes"/> or <see cref="SettingsLocal.VectorIndexes"/> and referred to by
/// <see cref="Id"/>. The memory index is not an engine and has no entry: it is what a default of
/// <see cref="Guid.Empty"/> means.
/// </summary>
public class IndexEngineSettings {
    /// <summary>What the defaults (and later, individual properties) refer to. Also names the engine's
    /// folder below the index folder, so it must stay the same for as long as the engine's files should.</summary>
    public Guid Id { get; set; }
    /// <summary>
    /// Which engine. The built-in ones are known by short name - see <see cref="IndexEngineTypes"/>:
    /// value engines <c>Native</c> and <c>Sqlite</c>, text engines <c>Native</c>, <c>Sqlite</c> and
    /// <c>Lucene</c>, vector engines <c>IVS</c> and <c>HNSW</c>. Anything else is taken as the full type
    /// name of a custom engine, constructed with the engine's folder path as its only argument.
    /// </summary>
    public string? TypeName { get; set; }
    /// <summary>
    /// How much memory the engine may spend on caches and buffers, in megabytes. It is a budget the
    /// engine works within, not an allocation: 0 makes it use as little as it can, and an engine with a
    /// hard floor (the HNSW graph is always resident) exceeds a budget below that floor with a warning.
    /// Changing it never invalidates the engine's files.
    /// </summary>
    public int MaxMemoryUsageInMb { get; set; } = 256;
    [System.Text.Json.Serialization.JsonIgnore]
    public long MaxMemoryUsageInBytes => Math.Max(0, MaxMemoryUsageInMb) * 1024L * 1024L;
    public override string ToString() => (TypeName ?? "?") + ", " + MaxMemoryUsageInMb + " MB";
}

/// <summary>The short <see cref="IndexEngineSettings.TypeName"/>s of the built-in engines.</summary>
public static class IndexEngineTypes {
    /// <summary>The built-in disk engine: the KV store for value indexes, the LSM text index for word indexes.</summary>
    public const string Native = "Native";
    /// <summary>SQLite, value indexes as tables and word indexes as FTS5 tables. Needs the Relatude.DB.Plugins.Sqlite package.</summary>
    public const string Sqlite = "Sqlite";
    /// <summary>Lucene word indexes. Needs the Relatude.DB.Plugins.Lucene package.</summary>
    public const string Lucene = "Lucene";
    /// <summary>The disk-based IVF vector index.</summary>
    public const string IVS = "IVS";
    /// <summary>The disk-based HNSW graph vector index.</summary>
    public const string HNSW = "HNSW";
    public static readonly string[] ValueEngines = [Native, Sqlite];
    public static readonly string[] TextEngines = [Native, Sqlite, Lucene];
    public static readonly string[] VectorEngines = [IVS, HNSW];
    public static bool Is(string? typeName, string known) => string.Equals(typeName, known, StringComparison.OrdinalIgnoreCase);
}
public enum PersistedQueueStoreEngine {
    Memory = 0,
    Native = 1,
    Sqlite = 2,
}
public enum FileStoreEngine {
    SingleFile = 0,
    MultiFile = 1,
}


