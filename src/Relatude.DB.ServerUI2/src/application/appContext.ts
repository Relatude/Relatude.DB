import { useContext } from "react";
import { ReactAppContext } from "../main";
import { API } from "./api";
import { Connection } from "../relatude.db/connection";
import type { UIContext } from "./uiContext";

export const useApp = () => useContext(ReactAppContext);
export class AppContext {
    private _ui: UIContext = null as any;
    public api: API;
    public db: Connection
    public baseUrl: string;
    // public async EnsureInit(ui: UIContext) {
    //     if (this._ui) return;
    //     this._ui = ui;
    //     this._ui.setScreen("loading");
    //     const tryToConnect = window.setInterval(async () => {
    //         try { // keep trying until the server is available ( show splash screen while failing or waiting )
    //             const isLoggedIn = await this.api.auth.isLoggedIn();
    //             if (isLoggedIn) ui.setScreen("online");
    //             else ui.setScreen("login");
    //             clearInterval(tryToConnect);
    //         } catch {
    //             this._ui.setScreen("disconnected");
    //         }
    //     }, 500);
    // }
    constructor(baseUrl: string) {
        if (!baseUrl.endsWith("/")) baseUrl += "/";
        this.baseUrl = baseUrl;
        this.api = new API(baseUrl, async (errorMessage) => {
            console.error("API Error:", errorMessage);
            //return await this.ui.askRetryDialog(errorMessage);
            return false;
        });
        this.db = new Connection(baseUrl + "data");
        console.log("AppContext initiated");
    }
}
export default AppContext;