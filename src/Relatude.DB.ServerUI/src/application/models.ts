export type AppStates = "splash" | "login" | "main" ;
export type StoreStates = "Closed" | "Open" | "Opening" | "Closing" | "Error" | "Unknown";
export type StoreTypes = "SimpleStore" | "NodeStore";
export type DataStoreActivityCategory = "None" | "Opening" | "Closing" | "Querying" | "Executing" | "Flushing" | "Copying" | "Rewriting" | "Maintenance";



export interface FileMeta {
    key: string
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
    doNotAcceptBadLogFile: boolean
    doNotAcceptBadStateFile: boolean
    enableSystemLog: boolean
    writeSystemLogConsole: boolean
    daysToKeepSystemLog: number
    nodeCacheSizeGb: number
    setCacheSizeGb: number
    forceDiskFlushOnEveryTransaction: boolean
    autoFlushDiskInBackground: boolean
    autoFlushDiskIntervalInSeconds: number
    autoSaveIndexStates: boolean
    autoSaveIndexStatesIntervalInMinutes: number
    autoBackUp: boolean
    noHourlyBackUps: number
    noDailyBackUps: number
    noWeeklyBackUps: number
    truncateBackups: boolean
    autoTruncate: boolean
    autoTruncateIntervalInMinutes: number
    autoIndexes: boolean
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
    activity: DataStoreActivity;
}
export interface DataStoreActivity {
    category: DataStoreActivityCategory;
    description?: string;
    progress?: number;
}
export interface StoreStatus {

    typeCounts: Record<string, number>;
    queuedTaskStateCounts: Record<string, number>;

    initiatedUtc?: Date;
    uptimeMs: number;
    logFirstStateUtc?: Date;
    logLastChange?: Date;
    logTruncatableActions: number;
    logActionsNotItInStatefile: number;
    logTransactionsNotItInStatefile: number;
    logWritesQueued: number;
    logFileKey?: string;
    logFileSize: number;
    logStateFileSize: number;
    countActionsSinceClearCache: number;
    countTransactionsSinceClearCache: number;
    countQueriesSinceClearCache: number;
    countNodeGetsSinceClearCache: number;
    datamodelPropertyCount: number;
    datamodelNodeTypeCount: number;
    datamodelRelationCount: number;
    datamodelIndexCount: number;
    nodeCount: number;
    nodeCacheCount: number;
    nodeCacheCountOfUnsaved: number;
    nodeCacheSize: number;
    nodeCacheSizePercentage: number;
    nodeCacheHits: number;
    nodeCacheMisses: number;
    nodeCacheReduces: number;
    setCacheCounters: number;
    setCacheCount: number;
    setCacheSize: number;
    setCacheSizePercentage: number;
    setCacheHits: number;
    setCacheMisses: number;
    setCacheReduces: number;
}
export interface LogEntry<T> {
    timestamp: Date;
    values: T;
}
export interface QueryLogValues {
    query: string;
    duration: number;
    resultCount: number;
    nodeCount: number;
    uniqueNodeCount: number;
    diskReads: number;
    nodesReadFromDisk: number;
}
export interface TransactionLogValues {
    transactionId: string;
    duration: number;
    actionCount: number;
    primitiveActionCount: number;
    diskFlush: string;
}
export interface ActionLogValues {
    transactionId: string;
    operation: string;
    details: string;
}
export interface ContainerLogEntry {
    timestamp: Date;
    description: string;
}
export interface ServerLogEntry {
    timestamp: Date;
    description: string;
}
export interface Transaction {

}
export interface EventData<T> {
    id: string;
    timestamp: Date;
    maxAge: number;
    name: string;
    data: T;
}