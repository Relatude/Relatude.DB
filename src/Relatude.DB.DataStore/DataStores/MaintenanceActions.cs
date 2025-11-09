namespace Relatude.DB.DataStores;
[Flags]
public enum MaintenanceAction {
    FlushDisk = 1, // write all data to disk
    ClearCache = 2, // clear all cache
    CompressMemory = 4, // simplifies memory structures where possible
    TruncateLog = 8, // rewrites log to only contain current state
    TruncateIndexes = 16, // rewrites log to only contain current state
    DeleteOldLogs = 32, // deletes old logs
    SaveIndexStates = 64, // saves state of all indexes to disk for faster opening
    ClearAiCache = 128, // saves state of all caches to disk for faster opening ( AI service caches )
    PurgeCache = 256, // purges cache
    GarbageCollect = 512, // runs garbage collection
    ResetSecondaryLogFile = 1024, // resets secondary log file
}
