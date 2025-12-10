import "@mantine/core/styles.css";
import { createContext, StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MantineProvider } from '@mantine/core'
import { AppContext, useApp } from './application/appContext';
import { useUI } from './application/uiContext';
import { Splash, Disconnected } from './application/splash';
import { Login } from "./application/login";
import { App } from "./application/app";

export const ReactAppContext = createContext<AppContext>(null as any);

const createAppContext = () => {
  const baseUrl = window.location.href.indexOf("localhost:5173") > -1 ? 'https://localhost:7054/relatude.db' : window.location.pathname;
  const app = new AppContext(baseUrl);
  return app;
}
const ApplicationWrapper = () => {
  return (
    <StrictMode>
      <ReactAppContext.Provider value={createAppContext()}>
        <MantineProvider forceColorScheme={"dark"}>
          <Login />
        </MantineProvider>
        {/* <Application /> */}
      </ReactAppContext.Provider>
    </StrictMode>
  );
}

const Application = () => {
  const ui = useUI();
  const app = useApp();
  //app.EnsureInit(ui);
  const screen = ui.screen;
  return (
    <MantineProvider forceColorScheme={ui.darkTheme ? "dark" : "light"}>
      {screen === 'loading' && <Splash />}
      {screen === 'login' && <Login />}
      {screen === 'online' && <App />}
      {screen === 'disconnected' && <Disconnected />}
    </MantineProvider>
  );
}

const rootElement = document.getElementById('root');
if (!rootElement) throw new Error("Failed to find the root element");
const root = createRoot(rootElement);

root.render(
  <ApplicationWrapper />
);
