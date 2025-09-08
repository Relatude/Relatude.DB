import { Datamodel } from "../relatude.db/datamodel";
import { DatamodelModel } from "../relatude.db/datamodelModels";
import { StoreStates, FileMeta, LogEntry, NodeStoreContainer, SimpleStoreContainer, StoreStatus, ContainerLogEntry, QueryLogValues, TransactionLogValues, ActionLogValues, Transaction, ServerLogEntry, DataStoreStatus} from "./models";

type retryCallback = (errorMessage) => Promise<boolean>;
type QueryObject = any; // string[][] | Record<string, string> | string | URLSearchParams;
export class API {
    constructor(
        public baseUrl: string,
        retry: retryCallback) {
        this.retry = retry;
    }
    retry: retryCallback;
    queryJson = async <T>(controller: string, action: string, query?: QueryObject, body?: object, noRetry: boolean = false): Promise<T> => {
        let statusCode: number | undefined;
        let url = this.baseUrl + (controller ? (controller + "/") : "") + action;
        if (query) url += "?" + new URLSearchParams(query);
        try {
            let response: Response;
            if (body) {
                response = await fetch(url, { credentials: "include", method: 'POST', body: JSON.stringify(body), headers: { "Content-Type": "application/json" } },);
            } else {
                response = await fetch(url, { credentials: "include", method: 'POST' },);
            }
            statusCode = response.status;
            if (statusCode !== 200) throw new Error("Failed to query \"" + url + "\"");
            return await response.json() as T;
        } catch (error) {
            const errMsg = (statusCode ? statusCode + ": " : "") + error.message;
            if (!noRetry && await this.retry(errMsg)) return this.queryJson<T>(controller, action, query, body);
            throw new Error(errMsg);
        }
    }
    queryString = async (controller: string, action: string, query?: QueryObject, body?: object, noRetry: boolean = false): Promise<string> => {
        let statusCode: number | undefined;
        let url = this.baseUrl + (controller ? (controller + "/") : "") + action;
        if (query) url += "?" + new URLSearchParams(query);
        try {
            let response: Response;
            if (body) {
                response = await fetch(url, { credentials: "include", method: 'POST', body: JSON.stringify(body), headers: { "Content-Type": "application/json" } },);
            } else {
                response = await fetch(url, { credentials: "include", method: 'POST' },);
            }
            statusCode = response.status;
            if (statusCode !== 200) throw new Error("Failed to query \"" + url + "\"");
            return await response.text();
        } catch (error) {
            const errMsg = (statusCode ? statusCode + ": " : "") + error.message;
            if (!noRetry && await this.retry(errMsg)) return this.queryString(controller, action, query, body);
            throw new Error(errMsg);
        }
    }
    execute = async (controller: string, action: string, query?: QueryObject, body?: object): Promise<void> => {
        let statusCode: number | undefined;
        let url = this.baseUrl + (controller ? (controller + "/") : "") + action;
        if (query) url += "?" + new URLSearchParams(query);
        try {
            let response: Response;
            if (body) {
                response = await fetch(url, { credentials: "include", method: 'POST', body: JSON.stringify(body), headers: { "Content-Type": "application/json" } },);
            } else {
                response = await fetch(url, { credentials: "include", method: 'POST' },);
            }
            statusCode = response.status;
            if (statusCode !== 200) throw new Error("Failed to query \"" + url + "\"");
            return
        } catch (error) {
            const errMsg = (statusCode ? statusCode + ": " : "") + error.message;
            if (await this.retry(errMsg)) return this.execute(controller, action, query, body);
            throw new Error(errMsg);
        }
    }
    upload = async (controller: string, action: string, query: QueryObject, data: ArrayBuffer): Promise<void> => {
        let statusCode: number | undefined;
        let url = this.baseUrl + (controller ? (controller + "/") : "") + action;
        if (query) url += "?" + new URLSearchParams(query);
        try {
            let response: Response;
            response = await fetch(url, { credentials: "include", method: 'POST', body: data },);
            statusCode = response.status;
            if (statusCode !== 200) throw new Error("Failed to query \"" + url + "\"");
            return
        } catch (error) {
            const errMsg = (statusCode ? statusCode + ": " : "") + error.message;
            if (await this.retry(errMsg)) return this.upload(controller, action, query, data);
            throw new Error(errMsg);
        }
    }
    download = (controller: string, action: string, query?: QueryObject) => {
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
    createEventSource = () => new EventSource(this.server.baseUrl + this.controller + "/events", { withCredentials: true });
    changeSubscription = (subscriptionId: string, events: string[]) => this.server.execute(this.controller, 'change-subscription', { subscriptionId }, events);
}
class DatamodelAPI {
    constructor(private server: API, private controller: string) { }
    getCode = (storeId: string, addAttributes: boolean) => this.server.queryString(this.controller, 'get-code', { storeId, addAttributes: addAttributes ? "true" : "false" });
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
}
class MaintenanceAPI {
    constructor(private server: API, private controller: string) { }
    deleteAllButDb = (storeId: string) => this.server.execute(this.controller, 'delete-all-but-db', { storeId });
    deleteAllFiles = (storeId: string, ioId: string) => this.server.execute(this.controller, 'delete-all-files', { storeId, ioId });
    downloadFullDb = (storeId: string) => this.server.download(this.controller, 'download-full-db', { storeId });
    downloadTruncatedDb = (storeId: string) => this.server.download(this.controller, 'download-truncated-db', { storeId });
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
    getFileKeyOfDb = async (storeId: string, ioId: string, filePrefix?: string) => this.server.queryString(this.controller, 'get-file-key-of-db', { storeId, ioId });
    getFileKeyOfNextDb = async (storeId: string, ioId: string, filePrefix?: string) => this.server.queryString(this.controller, 'get-file-key-of-db-next', { storeId, ioId });
    validateDownloadFileRead = async (storeId: string, ioId: string, fileName: string) => {
        const macthes = await this.getStoreFiles(storeId, ioId).then(files => files.filter(f => f.key === fileName));
        if (macthes.length === 0) throw new Error("File not found");
        if (macthes.length > 1) throw new Error("Multiple files found with the same name");
        if (macthes[0].writers > 0) throw new Error("File is locked");
    }
    downloadFile = async (storeId: string, ioId: string, fileName: string) => {
        try {
            await this.validateDownloadFileRead(storeId, ioId, fileName);
        } catch (error) {
            if (await this.server.retry(error.message)) {
                return await this.downloadFile(storeId, ioId, fileName);
            } else {
                throw error;
            }
        }
        this.server.download(this.controller, 'download-file', { storeId, ioId, fileName });
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
    getDefaultStoreId = () => this.server.queryString(this.controller, 'get-default-store-id');
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
    getContainerLog = async (storeId: string, skip: number, take: number) => {
        const result = await this.server.queryJson<ContainerLogEntry[]>(this.controller, 'get-container-log', { storeId, skip, take });
        result.forEach(e => e.timestamp = new Date(e.timestamp));
        return result;
    }
    clearContainerLog = (storeId: string) => this.server.execute(this.controller, 'clear-container-log', { storeId });
    enable = (storeId: string, enable: boolean) => this.server.execute(this.controller, 'enable', { storeId, enable: enable ? "true" : "false" });
    enableDetails = (storeId: string, enable: boolean) => this.server.execute(this.controller, 'enable-details', { storeId, enable: enable ? "true" : "false" });
    isEnabled = (storeId: string) => this.server.queryJson<boolean>(this.controller, 'is-enabled', { storeId });
    isEnabledDetails = (storeId: string) => this.server.queryJson<boolean>(this.controller, 'is-enabled-details', { storeId });
    clear = (storeId: string,) => this.server.execute(this.controller, 'clear', { storeId });
    private fixTimeStamp = async <T>(entries: Promise<LogEntry<T>[]>) => {
        const result = await entries;
        result.forEach(e => e.timestamp = new Date(e.timestamp));
        return result;
    }
    extractQueryLog = async (storeId: string, from: Date, to: Date, skip: number, take: number) => this.fixTimeStamp(this.server.queryJson<LogEntry<QueryLogValues>[]>(this.controller, 'extract-query-log', { storeId, from: from.toISOString(), to: to.toISOString(), skip, take }));
    extractTransactionLog = (storeId: string, from: Date, to: Date, skip: number, take: number) => this.fixTimeStamp(this.server.queryJson<LogEntry<TransactionLogValues>[]>(this.controller, 'extract-transaction-log', { storeId, from: from.toISOString(), to: to.toISOString(), skip, take }));
    extractActionLog = (storeId: string, from: Date, to: Date, skip: number, take: number) => this.fixTimeStamp(this.server.queryJson<LogEntry<ActionLogValues>[]>(this.controller, 'extract-action-log', { storeId, from: from.toISOString(), to: to.toISOString(), skip, take }));
    setPropertyHitsRecordingStatus = (storeId: string, enabled: boolean) => this.server.execute(this.controller, 'set-property-hits-recording-status', { storeId, enabled: enabled ? "true" : "false" });
    isRecordingPropertyHits = (storeId: string) => this.server.queryJson<boolean>(this.controller, 'is-recording-property-hits', { storeId });
    analysePropertyHits = (storeId: string) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-property-hits', { storeId });
    analyseQueryCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-query-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyseQueryDuration = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-query-duration', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyseTransactionCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-transaction-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyseTransactionDuration = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-transaction-duration', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyseTransactionAction = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-transaction-action', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyseActionCount = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-action-count', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
    analyseActionOperations = (storeId: string, intervalType: string, from: Date, to: Date) => this.server.queryJson<{ key: string, count: number }[]>(this.controller, 'analyse-action-operations', { storeId, intervalType, from: from.toISOString(), to: to.toISOString() });
}
class DemoAPI {
    constructor(private server: API, private controller: string) { }
    populate = (storeId: string, count: number) => this.server.queryJson<{ countCreated: number, elapsedMs: number }>(this.controller, 'populate', { storeId, count });
}
class TaskAPI {
    constructor(private server: API, private controller: string) { }
}
