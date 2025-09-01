import { createContext, useContext, useState } from "react";
import { App } from "../application/app";
export const AppContext = createContext<App>(null as any);
export const useApp = () => useContext(AppContext);
export const createApp = () => {
    const baseUrl = window.location.href.indexOf("localhost:1234") > -1 ? 'https://localhost:7054/relatude.db' : window.location.pathname;
    //const baseUrl = window.location.href.indexOf("localhost:1234") > -1 ? 'https://localhost:7053/wafj314gh132' : window.location.pathname;
    const app = new App(baseUrl);
    return app;
};

