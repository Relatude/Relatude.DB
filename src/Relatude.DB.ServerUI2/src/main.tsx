import './index.css'
import "@mantine/core/styles.css";
import { createContext, StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MantineProvider } from '@mantine/core'
import Application from './application/application';
import { App } from './application/app';
export const ReactAppContext = createContext<App>(null as any);
const createApp = () => {
  const baseUrl = window.location.href.indexOf("localhost:5173") > -1 ? 'https://localhost:7054/relatude.db' : window.location.pathname;
  const app = new App(baseUrl);
  return app;
};
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider>
      <ReactAppContext.Provider value={createApp()}>
        <Application />
      </ReactAppContext.Provider>
    </MantineProvider>
  </StrictMode>,
)
