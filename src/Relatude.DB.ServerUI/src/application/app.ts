import { API } from "./api";
import { UI } from "./ui";
import { Connection } from "../relatude.db/connection";
import { sleep } from "./common";
import { menuData } from "../sections/main/mainMenu";
import { IconBrandStackoverflow, IconChartAreaLine, IconDatabase, IconDeviceFloppy, IconPlug, IconSchema, IconSearch, IconServerCog, IconSettings } from "@tabler/icons-react";
import { DataStoreStatus, StoreStates } from "./models";
import { EventData, ServerEventHub } from "./serverEventHub";
export class App {
    public api: API;
    public ui: UI;
    public connection: Connection
    public serverEvents: ServerEventHub;
    constructor(baseUrl: string) {
        if (!baseUrl.endsWith("/")) baseUrl += "/";
        this.api = new API(baseUrl, async (errorMessage) => {
            return await this.ui.askRetryDialog(errorMessage);
        });
        this.ui = new UI(this);
        this.connection = new Connection(baseUrl + "data");
        this.serverEvents = new ServerEventHub(this.api, this.onServerEvent, this.onServerEventError, this.onServerConnectionError);
    }
    onServerEvent = (eventData: EventData<unknown>) => {
        // console.log("Server event received:", eventData);
    }
    onServerEventError = (error: any) => {
        console.error("Server event error:", error);
    }
    onServerConnectionError = (error: any) => {
        console.error("Server connection error:", error);
    }
    inializeAndLoginIfAuthenticated = async () => {
        let isLoggedIn = false;
        let retryCount = 0;
        this.ui.appState = "splash";
        while (true) {
            try {
                isLoggedIn = await this.api.auth.isLoggedIn();
                break; // Exit loop on success
            } catch (e: Error | any) {
                console.log("Waiting for valid server response", e?.message);
            }
            await sleep(500 * (++retryCount));
            if (retryCount > 1) {
                console.log("Unable to connect to the server. Please check your connection and try again.");
                this.ui.appState = "disconnected"
                return; // Exit if unable to connect after retries
            }
        }
        if (isLoggedIn) {
            this.connectToSSEAndShowMainUI();
        } else {
            this.ui.appState = "login";
        }
    }
    connectToSSEAndShowMainUI = async () => {
        const ui = this.ui;
        await this.serverEvents.connect();
        console.log("Connected to SSE");
        this.serverEvents.addEventListener<any>("DataStoreStates", undefined, this.onDataStoreStates);
        ui.defaultStoreId = await this.api.server.getDefaultStoreId();
        if (ui.defaultStoreId && ui.containers.find(c => c.id === ui.defaultStoreId)) {
            ui.selectedStoreId = ui.defaultStoreId;
        } else if (ui.containers.length > 0) {
            ui.selectedStoreId = ui.containers[0].id;
        }
        ui.appState = "main";
    }
    updateMenu = () => {
        const ui = this.ui;
        const serverMenu = new menuData(IconServerCog, "Server", "server");
        const root = [serverMenu];
        for (let container of ui.containers) {
            const m = new menuData(IconDatabase, container.name, container.id);
            m.color = ui.getStoreStateColor(container.id);
            root.push(m);
            m.add(IconSchema, "Model", "datamodel");
            m.add(IconSearch, "Query", "data");
            m.add(IconChartAreaLine, "Logs", "logs");
            m.add(IconBrandStackoverflow, "Queue", "taskQueue");
            m.add(IconDeviceFloppy, "Files", "files");
            m.add(IconPlug, "API", "api");
            m.add(IconSettings, "Settings", "settings");
        }
        ui.menu.items = root;
    };
    private onDataStoreStates = async (statuses: { key: string, value: StoreStates }[]) => {
        const ui = this.ui;
        ui.containers = await this.api.server.getStoreContainers();
        ui.defaultStoreId = await this.api.server.getDefaultStoreId();        
        ui.storeStates = new Map<string, StoreStates>();
        //alert(JSON.stringify(statuses));
        statuses.forEach(s => ui.storeStates.set(s.key, s.value));
        this.updateMenu();
    }

}