export type AppStates = "splash" | "login" | "main" | "disconnected";
export type StoreStates = "Closed" | "Open" | "Opening" | "Closing" | "Error" | "Unknown";
export type StoreTypes = "SimpleStore" | "NodeStore";
export type DataStoreActivityCategory = "None" | "Opening" | "Closing" | "Querying" | "Executing" | "Flushing" | "Copying" | "Rewriting" | "Maintenance";
export type LogIntervalTypes = "Second" | "Minute" | "Hour" | "Day" | "Week" | "Month";
export interface AnalysisEntry { from: string, to: string, value: number, hasData: boolean }
export interface FileMeta {
    key: string
    size: number
    creationTimeUtc: Date
    lastModifiedUtc: Date
    readers: number
    writers: number
    description: string
}
export interface FolderMeta {
    subFolders: FolderMeta[]
    files: FileMeta[]
    name: string
    size: number
    creationTimeUtc: Date
    lastModifiedUtc: Date
    readers: number
    writers: number
    description: string
}
export interface SimpleStoreContainer {
    id: string
    name: string
    description: string
    status: DataStoreStatus
    ioDatabase: string
}
export interface NodeStoreContainer {
    id: string
    name: string
    description: string
    autoOpen: boolean
    localSettings: LocalSettings
    ioDatabase: string
    ioBackup: string
    ioFiles: string
    ioSettings: IoSetting[]
    datamodelSources: DatamodelSource[]
}

export interface LocalSettings {
    filePrefix: string
    throwOnBadLogFile: boolean
    throwOnBadStateFile: boolean
    enableSimpleSystemLog: boolean
    onlyLogErrorsToSimpleSystemLog: boolean
    writeSystemLogConsole: boolean
    doNotCacheMapperFile: boolean
    nodeCacheSizeGb: number
    setCacheSizeGb: number
    flushDiskOnEveryTransactionByDefault: boolean
    forceDiskFlushAfterActionCountLimit: number
    deepFlushDisk: boolean
    autoFlushDiskInBackground: boolean
    autoFlushDiskIntervalInSeconds: number
    delayAutoDiskFlushIfBusy: boolean
    maxDelayAutoDiskFlushIfBusyInSeconds: number
    busyThresholdActivitiesLast10Sec: number
    busyThresholdQueriesLast10Sec: number
    autoSaveIndexStates: boolean
    autoSaveIndexStatesIntervalInMinutes: number
    autoSaveIndexStatesActionCountLowerLimit: number
    autoSaveIndexStatesActionCountUpperLimit: number
    autoBackUp: boolean
    noHourlyBackUps: number
    noDailyBackUps: number
    noWeeklyBackUps: number
    noMontlyBackUps: number
    noYearlyBackUps: number
    truncateBackups: boolean
    secondaryBackupLog: boolean
    autoTruncate: boolean
    autoTruncateIntervalInMinutes: number
    autoTruncateActionCountLowerLimit: number
    autoTruncateDeleteOldFileOnSuccess: boolean
    autoPurgeCache: boolean
    autoPurgeCacheIntervalInMinutes: number
    autoPurgeCacheLowerSizeLimitInMb: number
    usePersistedValueIndexesByDefault: boolean
    persistedValueIndexEngine: number
    persistedValueIndexFolderPath: string | null
    enableTextIndexByDefault: boolean
    enableSemanticIndexByDefault: boolean
    enableInstantTextIndexingByDefault: boolean
    usePersistedTextIndexesByDefault: boolean
    persistedTextIndexEngine: number
    autoDequeTasks: boolean
    persistedQueueStoreEngine: number
    persistedQueueStoreFolderPath: string | null


}
export interface IoSetting {
    id: string
    name: any
    path: any
    blobConnectionString: any
    blobContainerName: any
    lockBlob: any
    ioType: number
}
export interface DatamodelSource {
    id: string
    name: string
    namespace: any
    type: number
    reference: any
    fileIO: any
}
export interface DataStoreStatus {
    state: StoreStates;
    activityTree: DataStoreActivityBranch[];
}
export interface DataStoreActivityBranch {
    activity: DataStoreActivity;
    children?: DataStoreActivityBranch[];
}
export interface DataStoreActivity {
    id: number;
    isRoot: boolean;
    parentId: number
    category: DataStoreActivityCategory;
    description?: string;
    percentageProgress?: number;
}

export interface TaskStatusCounts {
    Pending: number;
    Running: number;
    Completed: number;
    Failed: number;
    Cancelled: number;
    Waiting: number;
    AbortedOnStartup: number;
}

export interface DataStoreInfo {
    typeCounts: { [key: string]: number };


    created: Date;
    isFresh: boolean;
    ageMs: number;
    uptimeMs: number;
    startUpMs: number;
    initiatedUtc: Date;
    logFirstStateUtc: Date | null;
    logLastChange: Date | null;
    logTruncatableActions: number;
    logActionsNotItInStatefile: number;
    noIndexesOutOfSync: number;
    logTransactionsNotItInStatefile: number;
    logWritesQueuedTransactions: number;
    logWritesQueuedActions: number;
    logFileKey: string | null;
    logFileSize: number;
    logStateFileSize: number;

    totalFileSize: number;
    fileStoreSize: number;
    loggingFileSize: number;
    backupFileSize: number;
    secondaryLogFileSize: number;
    indexFileSize: number;
    runningRewriteFile: string | null;

    countActionsSinceClearCache: number;
    countTransactionsSinceClearCache: number;
    countQueriesSinceClearCache: number;
    countNodeGetsSinceClearCache: number;
    datamodelPropertyCount: number;
    datamodelNodeTypeCount: number;
    datamodelRelationCount: number;
    datamodelIndexCount: number;
    relationCount: number;
    nodeCount: number;
    nodeCacheCount: number;
    nodeCacheSize: number;
    nodeCacheCountOfUnsaved: number;
    nodeCacheSizePercentage: number;
    nodeCacheHits: number;
    nodeCacheMisses: number;
    nodeCacheOverflows: number;
    aggregateCacheCount: number;
    aggregateCacheHits: number;
    aggregateCacheMisses: number;
    aggregateCacheOverflows: number;
    setCacheCount: number;
    setCacheSize: number;
    setCacheHits: number;
    setCacheMisses: number;
    setCacheSizePercentage: number;
    setCacheOverflows: number;
    queuedTasksPending: number;
    queuedTasksPendingPersisted: number;
    queuedBatchesPending: number;
    queuedTaskEstimatedMsUntilEmpty: number;
    queuedTaskEstimatedMsUntilEmptyPersisted: number;
    queuedBatchesPendingPersisted: number;
    queuedTaskStateCounts: TaskStatusCounts;
    queuedTaskStateCountsPersisted: TaskStatusCounts;
    queuedBatchesStateCounts: TaskStatusCounts;
    queuedBatchesStateCountsPersisted: TaskStatusCounts;
    processWorkingMemory: number;
    cpuUsagePercentage: number;
    cpuUsagePercentageLastMinute: number;
}
export interface ServerLogEntry {
    timestamp: Date;
    description: string;
}
export interface LogEntry<T> {
    timestamp: Date;
    values: T;
}
export interface SystemTraceEntry {
    timestamp: Date;
    type: "Info" | "Warning" | "Error";
    text: string;
    details?: string;
}
export interface SystemLogEntry {
    type: "Info" | "Warning" | "Error";
    text: string;
    details?: string;
}
export interface QueryLogEntry {
    query: string;
    duration: number;
    resultCount: number;
    nodeCount: number;
    uniqueNodeCount: number;
    diskReads: number;
    nodesReadFromDisk: number;
}
export interface TransactionLogEntry {
    transactionId: string;
    duration: number;
    actionCount: number;
    primitiveActionCount: number;
    diskFlush: string;
}
export interface ActionLogEntry {
    transactionId: string;
    operation: string;
    details: string;
}
export interface TaskLogEntry {
    taskId: string;
    batchId: string;
    taskTypeName: string;
    success: string;
    details: string;
}
export interface TaskBatchLogEntry {
    batchId: string;
    taskTypeName: string;
    started: string;
    duration: number;
    taskCount: string;
    success: string;
    error: string;
}


// entry.Values.Add("memUsageMb", (int)(metrics.MemUsage / 1024 * 1024));
// entry.Values.Add("cpuUsagePercentage", (int)(metrics.CpuUsage * 100));
// entry.Values.Add("queryCount", unchecked((int)metrics.QueryCount));
// entry.Values.Add("actionCount", unchecked((int)metrics.ActionCount));
// entry.Values.Add("transactionCount", unchecked((int)metrics.TransactionCount));
// entry.Values.Add("nodeCount", metrics.NodeCount);
// entry.Values.Add("relationCount", metrics.RelationCount);
// entry.Values.Add("nodeCacheCount", metrics.NodeCacheCount);
// entry.Values.Add("nodeCacheSizeMb", (int)(metrics.NodeCacheSize / 1024 / 1024));
// entry.Values.Add("setCacheCount", metrics.SetCacheCount);
// entry.Values.Add("setCacheSizeMb", (int)(metrics.SetCacheSize / 1024 * 1024));
// entry.Values.Add("taskQueueCount", metrics.TasksQueued);
// entry.Values.Add("taskPersistedQueueCount", metrics.TasksPersistedQueued);
// entry.Values.Add("taskExecutedCount", metrics.TasksExecuted);
// entry.Values.Add("taskPersistedExecutedCount", metrics.TasksPersistedExecuted);

export interface MetricsLogEntry {
    memUsageMb: number;
    cpuUsagePercentage: number;
    queryCount: number;
    actionCount: number;
    transactionCount: number;
    nodeCount: number;
    relationCount: number;
    nodeCacheCount: number;
    nodeCacheSizeMb: number;
    setCacheCount: number;
    setCacheSizeMb: number;
    taskQueueCount: number;
    taskPersistedQueueCount: number;
}
export interface LogInfo {
    key: string;
    name: string;
    enabledLog: boolean;
    enabledStatistics: boolean;
    firstRecord: string;
    lastRecord: string;
    totalFileSize: number;
    logFileSize: number;
    statisticsFileSize: number;
}
export interface PropertyHitEntry {
    propertyName: string;
    hitCount: number;
}
export interface Transaction {

}
