import { API } from "./api";
import { UI } from "./ui";
import { Connection } from "../relatude.db/connection";
export class App {
    public api: API;
    public ui: UI;
    public connection: Connection
    constructor(baseUrl: string) {
        if (!baseUrl.endsWith("/")) baseUrl += "/";
        this.api = new API(baseUrl, async (errorMessage) => {
            return await this.ui.askRetryDialog(errorMessage);
        });
        this.ui = new UI(this.api);
        this.connection = new Connection(baseUrl + "data");
    }
}