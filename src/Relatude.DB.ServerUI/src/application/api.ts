import { Datamodel } from "../relatude.db/datamodel";
import { DatamodelModel } from "../relatude.db/datamodelModels";
import { StoreStates, FileMeta, LogEntry, NodeStoreContainer, SimpleStoreContainer, StoreStatus, QueryLogEntry, TransactionLogEntry, ActionLogEntry, Transaction, ServerLogEntry, DataStoreStatus, SystemLogEntry, TaskLogEntry, MetricsLogEntry, TaskBatchLogEntry, LogInfo, PropertyHitEntry, SystemTraceEntry } from "./models";
import { EventSubscription } from "./serverEventHub";

type retryCallback = (errorMessage: any) => Promise<boolean>;
type QueryObject = any; // string[][] | Record<string, string> | string | URLSearchParams;
type responseType = "json" | "text" | "void";
type bodyType = "json" | "arraybuffer";
export class API {
    constructor(
        public baseUrl: string,
        public retry: retryCallback) {
    }
    query = async <T>(controller: string, action: string, noRetry: boolean, bodyType: bodyType, responseType: responseType, query?: QueryObject, body?: object): Promise<T> => {
        try {
            let url = this.baseUrl + (controller ? (controller + "/") : "") + action;
            if (query) url += "?" + new URLSearchParams(query);
            let init: RequestInit;
            if (bodyType === "json") {
                const haveBody = body !== undefined && body !== null;
                init = {
                    credentials: "include",
                    method: 'POST',
                    headers: haveBody ? { "Content-Type": "application/json" } : undefined,
                    body: haveBody ? JSON.stringify(body) : undefined
                };
            } else if (bodyType === "arraybuffer") {
                init = {
                    credentials: "include",         
                    method: 'POST',
                    body: body as ArrayBuffer
                };
            } else {
                throw new Error("Unknown body type.");
            }
            const response = await fetch(url, init);
            if (response.status !== 200) throw new Error(response.status + ": Failed to query \"" + url + "\". ");
            if (responseType === "void") return undefined as unknown as T;
            if (responseType === "text") return response.text() as Promise<T>;
            if (responseType === "json") return response.json() as Promise<T>;
            throw new Error("Unknown response type.");
        } catch (error: any) {
            if (!noRetry && await this.retry(error.message)) return this.query<T>(controller, action, noRetry, bodyType, responseType, query, body);
            throw new Error(error.message);
        }
    }
    queryJson = async <T>(controller: string, action: string, query?: QueryObject, body?: object, noRetry: boolean = false): Promise<T> => {
        return this.query<T>(controller, action, noRetry, "json", "json", query, body);
    }
    queryText = async (controller: string, action: string, query?: QueryObject, body?: object, noRetry: boolean = false): Promise<string> => {
        return this.query<string>(controller, action, noRetry, "json", "text", query, body);
    }
    execute = async (controller: string, action: string, query?: QueryObject, body?: object, noRetry: boolean = false): Promise<void> => {
        return this.query<void>(controller, action, noRetry, "json", "void", query, body);
    }
    upload = async (controller: string, action: string, query: QueryObject, data: ArrayBuffer): Promise<void> => {
        return this.query<void>(controller, action, false, "arraybuffer", "void", query, data);

    }
    userDownload = (controller: string, action: string, query?: QueryObject) => {
        let url = this.baseUrl + (controller ? (controller + "/") : "") + action;
        if (query) url += "?" + new URLSearchParams(query);
        window.open(url, '_blank');
    }
    version = async () => (await this.queryJson<{ version: string }>('', 'version')).version;
    status = new StatusAPI(this, "status");
    datamodel = new DatamodelAPI(this, "datamodel");
    data = new DataAPI(this, "data");
    endpoints = new EndpointsAPI(this, "endpoints");
    settings = new SettingsAPI(this, "settings");
    maintenance = new MaintenanceAPI(this, "maintenance");
    log = new LogAPI(this, "log");
    auth = new AuthAPI(this, "auth");
    server = new ServerAPI(this, "server");
    task = new TaskAPI(this, "task");
    demo = new DemoAPI(this, "demo");
}
class StatusAPI {
    constructor(private server: API, private controller: string) { }
    stateAll = () => this.server.queryJson<{ id: string, state: StoreStates }[]>(this.controller, 'state-all');
    statusAll = () => this.server.queryJson<{ id: string, status: DataStoreStatus }[]>(this.controller, 'status-all');
    connect = () => new EventSource(this.server.baseUrl + this.controller + "/connect", { withCredentials: true });
    setSubscriptions = (connectionId: string, subscriptions: EventSubscription[]) => this.server.execute(this.controller, 'set-subscriptions', { connectionId }, subscriptions);
    subscribe = (connectionId: string, name: string, filter?: string) => this.server.execute(this.controller, 'subscribe', { connectionId, name, filter });
    unsubscribe = (connectionId: string, name: string, filter?: string) => this.server.execute(this.controller, 'unsubscribe', { connectionId, name, filter });
}
class DatamodelAPI {
    constructor(private server: API, private controller: string) { }
    getCode = (storeId: string, addAttributes: boolean) => this.server.queryText(this.controller, 'get-code', { storeId, addAttributes: addAttributes ? "true" : "false" });
    getModel = async (storeId: string) => {
        const model = await this.server.queryJson<DatamodelModel>(this.controller, 'get-model', { storeId });
        return new Datamodel(model);
    }
}
class DataAPI {
    queueReIndexAll = (storeId: string) => this.server.queryJson<number>(this.controller, 'queue-re-index-all', { storeId });
    constructor(private server: API, private controller: string) { }
    shiftAllDates = (storeId: string, seconds: number) => this.server.queryJson<number>(this.controller, 'shift-all-dates', { storeId, seconds: seconds.toString() });
    query = (storeId: string, query: string) => this.server.queryJson<any>(this.controller, 'query', { storeId, query },);
    execute = (storeId: string, transactions: Transaction[]) => this.server.execute(this.controller, 'execute', { storeId }, transactions);
}
class EndpointsAPI {
    constructor(private server: API, private controller: string) { }
}
class AuthAPI {
    constructor(private server: API, private controller: string) { }
    haveUsers = async () => (await this.server.queryJson<boolean>(this.controller, 'have-users', undefined, undefined, true));
    isLoggedIn = () => this.server.queryJson<boolean>(this.controller, 'is-logged-in', undefined, undefined, true);
    login = async (userName: string, password: string, remember: boolean) => { return (await this.server.queryJson<{ success: boolean }>(this.controller, 'login', undefined, { userName, password, remember })).success };
    logout = () => this.server.execute(this.controller, 'logout');
    version = () => this.server.queryJson<{ version: string }>(this.controller, 'version');
}
class SettingsAPI {
    constructor(private server: API, private controller: string) { }
    getSettings = (storeId: string) => this.server.queryJson<NodeStoreContainer>(this.controller, 'get-settings', { storeId });
    setSettings = (storeId: string, settings: NodeStoreContainer) => this.server.execute(this.controller, 'set-settings', { storeId }, settings);
    reSaveSettings = (storeId: string) => this.server.execute(this.controller, 're-save-settings', { storeId });
}
class MaintenanceAPI {
    constructor(private server: API, private controller: string) { }
    deleteAllButDb = (storeId: string) => this.server.execute(this.controller, 'delete-all-but-db', { storeId });
    deleteAllFiles = (storeId: string, ioId: string) => this.server.execute(this.controller, 'delete-all-files', { storeId, ioId });
    downloadFullDb = (storeId: string) => this.server.userDownload(this.controller, 'download-full-db', { storeId });
    downloadTruncatedDb = (storeId: string) => this.server.userDownload(this.controller, 'download-truncated-db', { storeId });
    resetIoLocks(storeId: string, ioId: string) { return this.server.execute(this.controller, 'reset-io-locks', { storeId, ioId }); }
    isFileKeyLegal = async (fileKey: string | null) => !fileKey ? false : (await this.server.queryJson<{ isLegal: boolean }>(this.controller, 'is-file-key-legal', { fileKey: fileKey! })).isLegal;
    isFilePrefixLegal = async (filePrefix: string | null) => !filePrefix ? false : (await this.server.queryJson<{ isLegal: boolean }>(this.controller, 'is-file-prefix-legal', { filePrefix: filePrefix! })).isLegal;
    getSizeTempFiles = () => this.server.queryJson<{ totalSize: number }>(this.controller, 'get-size-temp-files').then(r => r.totalSize);
    cleanTempFiles = () => this.server.execute(this.controller, 'clean-temp-files');
    open = (storeId: string) => this.server.execute(this.controller, 'open', { storeId });
    close = (storeId: string) => this.server.execute(this.controller, 'close', { storeId });
    getAllFiles = (ioId: string) => fixFileListMetaDates(this.server.queryJson<FileMeta[]>(this.controller, 'get-all-files', { ioId }));
    getStoreFiles = (storeId: string, ioId: string) => fixFileListMetaDates(this.server.queryJson<FileMeta[]>(this.controller, 'get-store-files', { storeId, ioId }));
    canRenameFile = (storeId: string, ioId: string) => this.server.queryJson<{ canRename: boolean }>(this.controller, 'can-rename-file', { storeId, ioId }).then(r => r.canRename);
    renameFile = (storeId: string, ioId: string, fileName: string, newFileName: string) => this.server.execute(this.controller, 'rename-file', { storeId, ioId, fileName, newFileName });
    fileExist = (storeId: string, ioId: string, fileName: string) => this.server.queryJson<boolean>(this.controller, 'file-exist', { storeId, ioId, fileName });
    backUpNow = (storeId: string, ioId: string, truncate: boolean, keepForever: boolean) => this.server.execute(this.controller, 'backup-now', { storeId, ioId: ioId, truncate: truncate ? "true" : "false", keepForever: keepForever ? "true" : "false" });
    getFileKeyOfDb = async (storeId: string, ioId: string, filePrefix?: string) => this.server.queryText(this.controller, 'get-file-key-of-db', { storeId, ioId });
    getFileKeyOfNextDb = async (storeId: string, ioId: string, filePrefix?: string) => this.server.queryText(this.controller, 'get-file-key-of-db-next', { storeId, ioId });
    validateDownloadFileRead = async (storeId: string, ioId: string, fileName: string) => {
        const macthes = await this.getStoreFiles(storeId, ioId).then(files => files.filter(f => f.key === fileName));
        if (macthes.length === 0) throw new Error("File not found");
        if (macthes.length > 1) throw new Error("Multiple files found with the same name");
        if (macthes[0].writers > 0) throw new Error("File is locked");
    }
    downloadFile = async (storeId: string, ioId: string, fileName: string) => {
        try {
            await this.validateDownloadFileRead(storeId, ioId, fileName);
        } catch (error: any) {
            if (await this.server.retry(error.message)) {
                await this.downloadFile(storeId, ioId, fileName);
            } else {
                throw error;
            }
        }
        this.server.userDownload(this.controller, 'download-file', { storeId, ioId, fileName });
    }
    deleteFile = (storeId: string, ioId: string, fileName: string) => this.server.execute(this.controller, 'delete-file', { storeId, ioId, fileName });
    initiateUpload = async (storeId: string) => (await this.server.queryJson<{ value: string }>(this.controller, 'initiate-upload', { storeId })).value;
    uploadPart = (uploadId: string, data: ArrayBuffer) => this.server.upload(this.controller, 'upload-part', { uploadId }, data);
    cancelUpload = (uploadId: string) => this.server.execute(this.controller, 'cancel-upload', { uploadId });
    completeUpload = (storeId: string, ioId: string, uploadId: string, fileName: string, overwrite: boolean) => this.server.execute(this.controller, 'complete-upload', { storeId, ioId, uploadId, fileName, overwrite: overwrite ? "true" : "false" });
    copyFile = (storeId: string, fromIoId: string, fromFileName: string, toIoId: string, toIoFileName: string) => this.server.execute(this.controller, 'copy-file', { storeId, fromIoId, fromFileName, toIoId, toIoFileName });
    truncateLog = (storeId: string) => this.server.execute(this.controller, 'truncate-log', { storeId });
    saveIndexStates = (storeId: string) => this.server.execute(this.controller, 'save-index-states', { storeId });
    clearCache = (storeId: string) => this.server.execute(this.controller, 'clear-cache', { storeId });
    info = (storeId: string) => this.server.queryJson<StoreStatus>(this.controller, 'info', { storeId });
}
class ServerAPI {
    constructor(private server: API, private controller: string) { }
    getStoreContainers = () => this.server.queryJson<SimpleStoreContainer[]>(this.controller, 'get-store-containers');
    setMasterCredentials = (masterUserName: string, masterPassword: string) => this.server.execute(this.controller, 'set-master-credentials', { masterUserName, masterPassword });
    setNameAndDescription = (name: string, description: string) => this.server.execute(this.controller, 'set-name-and-description', { name, description });
    createStore = () => this.server.queryJson<NodeStoreContainer>(this.controller, 'create-store');
    removeStore = (storeId: string) => this.server.execute(this.controller, 'remove-store', { storeId });
    getDefaultStoreId = () => this.server.queryText(this.controller, 'get-default-store-id');
    setDefaultStoreId = (storeId: string) => this.server.execute(this.controller, 'set-default-store-id', { storeId });
    clearDefaultStoreId = () => this.setDefaultStoreId('00000000-0000-0000-0000-000000000000');
    getServerLog = async () => {
        const result = await this.server.queryJson<ServerLogEntry[]>(this.controller, 'get-server-log');
        result.forEach(e => e.timestamp = new Date(e.timestamp));
        return result;
    }
    clearServerLog = () => this.server.execute(this.controller, 'clear-server-log');
}
const fixFileListMetaDates = (file: Promise<FileMeta[]>) => file.then(files => files.map(fixFileMetaDates));
const fixFileMetaDates = (file: FileMeta) => {
    file.creationTimeUtc = new Date(file.creationTimeUtc);
    file.lastModifiedUtc = new Date(file.lastModifiedUtc);
    return file;
}
class LogAPI {
    constructor(private server: API, private controller: string) { }

    hasStartupException = (storeId: string) => this.server.queryJson<boolean>(this.controller, 'has-startup-exception', { storeId });
    getStartupException = (storeId: string) => this.server.queryJson<{ when: string, message: string, stackTrace: string } | null>(this.controller, 'get-startup-exception', { storeId });

    getLogInfos = (storeId: string) => this.server.queryJson<LogInfo[]>(this.controller, 'get-log-infos', { storeId });
    enableLog = (storeId: string, logKey: string, enable: boolean) => this.server.execute(this.controller, 'enable-log', { storeId, logKey, enable: enable ? "true" : "false" });
    isLogEnabled = (storeId: string, logKey: string) => this.server.queryJson<boolean>(this.controller, 'is-log-enabled', { storeId, logKey })
    enableStatistics = (storeId: string, logKey: string, enable: boolean) => this.server.execute(this.controller, 'enable-statistics', { storeId, logKey, enable: enable ? "true" : "false" });
    isStatisticsEnabled = (storeId: string, logKey: string) => this.server.queryJson<boolean>(this.controller, 'is-statistics-enabled', { storeId, logKey })
    clearLog = (storeId: string, logKey: string) => this.server.execute(this.controller, 'clear-log', { storeId, logKey });
    clearStatistics = (storeId: string, logKey: string) => this.server.execute(this.controller, 'clear-statistics', { storeId, logKey });
    extractLog = <T>(storeId: string, logKey: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.fixTimeStamp(this.server.queryJson<LogEntry<T>[]>(this.controller, 'extract-log', { storeId, logKey, from: from.toISOString(), to: to.toISOString(), skip, take, orderByDescendingDates }));

    getSystemTrace = async (storeId: string, skip: number, take: number) => {
        var entires = await this.server.queryJson<SystemTraceEntry[]>(this.controller, 'get-system-trace', { storeId, skip, take });
        entires.forEach(e => e.timestamp = new Date(e.timestamp));
        return entires;
    }
    extractSystemLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<SystemLogEntry>(storeId, "system", from, to, skip, take, orderByDescendingDates);
    extractQueryLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<QueryLogEntry>(storeId, "query", from, to, skip, take, orderByDescendingDates);
    extractTransactionLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<TransactionLogEntry>(storeId, "transaction", from, to, skip, take, orderByDescendingDates);
    extractActionLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<ActionLogEntry>(storeId, "action", from, to, skip, take, orderByDescendingDates);
    extractTaskLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<TaskLogEntry>(storeId, "task", from, to, skip, take, orderByDescendingDates);
    extractTaskBatchLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<TaskBatchLogEntry>(storeId, "taskbatch", from, to, skip, take, orderByDescendingDates);
    extractMetricsLog = (storeId: string, from: Date, to: Date, skip: number, take: number, orderByDescendingDates: boolean) => this.extractLog<MetricsLogEntry>(storeId, "metrics", from, to, skip, take, orderByDescendingDates);

    setPropertyHitsRecordingStatus = (storeId: string, enabled: boolean) => this.server.execute(this.controller, 'set-property-hits-recording-status', { storeId, enabled: enabled ? "true" : "false" });
    isRecordingPropertyHits = (storeId: string) => this.server.queryJson<boolean>(this.controller, 'is-recording-property-hits', { storeId });

    analyzePropertyHits = (storeId: string) => this.server.queryJson<PropertyHitEntry[]>(this.controller, 'analyze-property-hits', { storeId });
    analyzeSystemLogCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-system-log-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeSystemLogCountByType = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-system-log-count-by-type', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeQueryCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-query-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeQueryDuration = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-query-duration', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeTransactionCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-transaction-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeTransactionDuration = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-transaction-duration', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeTransactionAction = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-transaction-action', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeActionCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-action-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyzeActionOperations = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyze-action-operations', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    private fixTimeStamp = async <T>(entries: Promise<LogEntry<T>[]>) => {
        const result = await entries;
        result.forEach(e => {
            e.timestamp = new Date(e.timestamp);
            // for (const key in e.values) {
            //     if (isValidDateString(e.values[key] as string)) e.values[key] = new Date(e.values[key] as string) as any;
            // }
        });
        return result;
    }

}
class DemoAPI {
    constructor(private server: API, private controller: string) { }
    populate = (storeId: string, count: number, wikipediaData:boolean) => this.server.queryJson<{ countCreated: number, elapsedMs: number }>(this.controller, 'populate', { storeId, count, wikipediaData});
}
class TaskAPI {
    constructor(private server: API, private controller: string) { }
}

