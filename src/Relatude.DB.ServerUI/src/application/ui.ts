import { makeAutoObservable } from "mobx";
import { API } from "./api";
import { AppStates, StoreStates, SimpleStoreContainer, DataStoreStatus } from "./models";
import { menuStore } from "../sections/main/mainMenu";
import { QueryBuilderStore } from "../sections/data/queryBuilder";
import { App } from "./app";

// Global UI state management for the application
export class UI {
    private app: App;
    constructor(app: App) {
        makeAutoObservable(this);
        this.app = app;
        this._darkTheme = window.matchMedia('(prefers-color-scheme: dark)').matches;
    }
    private _containers: SimpleStoreContainer[] = []; get containers() { return this._containers; } set containers(value: SimpleStoreContainer[]) { this._containers = value; }
    private _state: StoreStates | null = null; get state() { return this._state; } set state(value: StoreStates | null) { this._state = value; }
    private _darkTheme: boolean = false; get darkTheme() { return this._darkTheme; } set darkTheme(value: boolean) { this._darkTheme = value; }
    private _appState: AppStates = "splash"; get appState() { return this._appState; } set appState(value: AppStates) { this._appState = value; }
    private _defaultStoreId: string | null = null; get defaultStoreId() { return this._defaultStoreId; } set defaultStoreId(value: string | null) { this._defaultStoreId = value; }
    private _storeStates: Map<string, StoreStates> = new Map<string, StoreStates>(); set storeStates(value: Map<string, StoreStates>) { this._storeStates = value; } get storeStates() { return this._storeStates; }
    get selectedStoreId() { return this.containers.find(c => this.menu.path.includes(c.id))?.id; }
    set selectedStoreId(value: string | undefined) {
        if (!value) {
            this.menu.clearPath();
        } else {
            this.menu.setSelected(value, 0);
        }
    }
    private _menu: menuStore = new menuStore([]); get menu() { return this._menu; } set menu(value: menuStore) { this._menu = value; }
    private _showRetryDialog: boolean = false; get showRetryDialog() { return this._showRetryDialog; } set showRetryDialog(value: boolean) { this._showRetryDialog = value; }
    private _retryDialogMessage: string = ""; get retryDialogMessage() { return this._retryDialogMessage; } set retryDialogMessage(value: string) { this._retryDialogMessage = value; }
    private _queryBuilders: Map<string, QueryBuilderStore[]> = new Map();
    private _selectedQueryBuilders: Map<string, string> = new Map();
    private _activeLogKey: string = "trace"; get activeLogKey() { return this._activeLogKey; } set activeLogKey(value: string) { this._activeLogKey = value; }
    getSelectedQueryBuilderId(storeId: string) {
        return this._selectedQueryBuilders.get(storeId);
    }
    setSelectedQueryBuilderId(storeId: string, id: string | null) {
        if (!id) this._selectedQueryBuilders.delete(storeId);
        else this._selectedQueryBuilders.set(storeId, id);
    }
    getQueryBuilders = (storeId: string) => {
        return this._queryBuilders.get(storeId) || [];
    }
    addQueryBuilder = (storeId: string, qb: QueryBuilderStore) => {
        if (!this._queryBuilders.has(storeId)) this._queryBuilders.set(storeId, []);
        this._queryBuilders.get(storeId)!.push(qb);
        this.app.updateMenu();
    }
    removeQueryBuilder = (storeId: string, qb: QueryBuilderStore) => {
        const builders = this._queryBuilders.get(storeId)!;
        builders.splice(builders.indexOf(qb), 1);
    }
    askRetryDialog(errorMessage: any): boolean | PromiseLike<boolean> {
        this.showRetryDialog = true;
        this.retryDialogMessage = errorMessage;
        return new Promise((resolve, reject) => {

        });
    }
    getCurrentStore = () => this.containers.find(c => c.id === this.selectedStoreId);
    isIoUsedForCurrentDatabase = (ioId: string) => this.getCurrentStore()?.ioDatabase === ioId;
    isCurrentStoreOpen = () => this.getStoreState(this.selectedStoreId) === "Open";
    isStoreOpen = (storeId?: string) => storeId ? false : this.getStoreState(storeId) === "Open";
    getStoreState = (storeId: string | undefined): StoreStates => storeId ? this._storeStates.get(storeId)! : "Unknown";
    getStoreStateCurrentStore = () => this.getStoreState(this.selectedStoreId);
    getStoreStateColor = (storeId: string) => {
        const state = this.getStoreState(storeId);
        let color: string;
        if (this.darkTheme) {
            switch (state) {
                case "Closed": color = "gray"; break;
                case "Open": color = "lightgreen"; break;
                case "Opening": color = "lightyellow"; break;
                case "Closing": color = "blue"; break;
                case "Error": color = "red"; break;
                default: color = "black"; break;
            }
        } else {
            switch (state) {
                case "Closed": color = "lightgray"; break;
                case "Open": color = "green"; break;
                case "Opening": color = "yellow"; break;
                case "Closing": color = "blue"; break;
                case "Error": color = "red"; break;
                default: color = "white"; break;
            }
        }
        return color;
    }
}


