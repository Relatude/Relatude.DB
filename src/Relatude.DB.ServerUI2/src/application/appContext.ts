import { useContext } from "react";
import { ReactAppContext } from "../main";
import { API } from "./api";
import { Connection } from "../relatude.db/connection";
export const useApp = () => useContext(ReactAppContext);
export class AppContext {
    public api: API;
    public db: Connection
    public baseUrl: string;
    constructor(baseUrl: string) {
        if (!baseUrl.endsWith("/")) baseUrl += "/";
        this.baseUrl = baseUrl;
        this.api = new API(baseUrl, async (errorMessage) => {
            console.error("API Error:", errorMessage);
            //return await this.ui.askRetryDialog(errorMessage);
            return false;
        });
        this.db = new Connection(baseUrl + "data");
    }
}
export default AppContext;