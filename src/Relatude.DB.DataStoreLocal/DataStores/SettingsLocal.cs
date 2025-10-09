public class SettingsLocal {

    public string? FilePrefix { get; set; }

    public bool ThrowOnBadLogFile { get; set; } = false;
    public bool ThrowOnBadStateFile { get; set; } = false;

    public bool EnableSystemLog { get; set; } = false;
    public bool OnlyLogErrorsToSystemLog { get; set; } = true;
    //public bool EnableQueryLog { get; set; } = false;
    //public bool EnableQueryLogDetails { get; set; } = false;
    public bool WriteSystemLogConsole { get; set; } = true;
    public bool DoNotCacheMapperFile { get; set; } = false;
    public int DaysToKeepSystemLog { get; set; } = 10;
    public double NodeCacheSizeGb { get; set; } = 1;
    public double SetCacheSizeGb { get; set; } = 1;
    public bool ForceDiskFlushOnEveryTransaction { get; set; } = false;

    public bool AutoFlushDiskInBackground { get; set; } = true;
    public double AutoFlushDiskIntervalInSeconds { get; set; } = 1;
    public bool DelayAutoDiskFlushIfBusy { get; set; } = true;
    public double MaxDelayAutoDiskFlushIfBusyInSeconds { get; set; } = 15;

    public bool AutoSaveIndexStates { get; set; } = true;
    public double AutoSaveIndexStatesIntervalInMinutes { get; set; } = 30;
    public int AutoSaveIndexStatesActionCountUpperLimit { get; set; } = 50000;
    public int AutoSaveIndexStatesActionCountLowerLimit { get; set; } = 10000;

    public bool AutoBackUp { get; set; } = false;
    public int NoHourlyBackUps { get; set; } = 10;
    public int NoDailyBackUps { get; set; } = 10;
    public int NoWeeklyBackUps { get; set; } = 4;
    public int NoMontlyBackUps { get; set; } = 12;
    public int NoYearlyBackUps { get; set; } = 10;
    public bool TruncateBackups { get; set; } = false;

    public bool AutoTruncate { get; set; } = false; //true;
    public double AutoTruncateIntervalInMinutes { get; set; } = 60;
    public int AutoTruncateActionCountUpperLimit { get; set; } = 50000;
    public int AutoTruncateActionCountLowerLimit { get; set; } = 10000;
    public bool AutoTruncateDeleteOldFileOnSuccess { get; set; } = false; //true;

    public bool AutoPurgeCache { get; set; } = true;
    public double AutoPurgeCacheIntervalInMinutes { get; set; } = 5;
    public double AutoPurgeCacheLowerSizeLimitInMb { get; set; } = 1;

    public bool UsePersistedValueIndexesByDefault { get; set; } = false;
    public PersistedValueIndexEngine PersistedValueIndexEngine { get; set; } = PersistedValueIndexEngine.Memory;
    public string? PersistedValueIndexFolderPath { get; set; }

    public bool EnableTextIndexByDefault { get; set; } = true;
    public bool EnableSemanticIndexByDefault { get; set; } = false;
    public bool EnableInstantTextIndexingByDefault { get; set; } = false;
    public bool UsePersistedTextIndexesByDefault { get; set; } = false;
    public PersistedTextIndexEngine PersistedTextIndexEngine { get; set; } = PersistedTextIndexEngine.Memory;

    public bool AutoDequeTasks { get; set; } = true;
    public PersistedQueueStoreEngine PersistedQueueStoreEngine { get; set; } = PersistedQueueStoreEngine.BuiltIn;
    public string? PersistedQueueStoreFolderPath { get; set; }

}

public enum PersistedTextIndexEngine {
    Memory = 0,
    Sqlite = 1,
    Lucene = 2,
}
public enum PersistedValueIndexEngine {
    Memory = 0,
    Sqlite = 1,
}
public enum PersistedQueueStoreEngine {
    Memory = 0,
    BuiltIn = 1,
    Sqlite = 2,
}


