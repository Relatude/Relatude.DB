import "@mantine/core/styles.css";
import { createContext, StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MantineProvider } from '@mantine/core'
import { AppContext } from './application/appContext';
import { useUI } from './application/uiContext';
import { Splash } from './sections/splash/splash';
import { Login } from "./sections/login/login";
export const ReactAppContext = createContext<AppContext>(null as any);

const createAppContext = () => {
  const baseUrl = window.location.href.indexOf("localhost:5173") > -1 ? 'https://localhost:7054/relatude.db' : window.location.pathname;
  const app = new AppContext(baseUrl);
  return app;
}

const Application = () => {
  const ui = useUI();
  const screen = ui.screen;
  return (
    <StrictMode>
      <ReactAppContext.Provider value={createAppContext()}>
        <MantineProvider forceColorScheme={ui.darkTheme ? "dark" : "light"}>
          {/* {screen === 'splash' && <Splash />} */}
          {screen === 'splash' && <Login />}
           {/* {screen === 'main' && <Main />}  */}
        </MantineProvider>
      </ReactAppContext.Provider>
    </StrictMode>
  );
}

const rootElement = document.getElementById('root');
if(!rootElement) throw new Error("Failed to find the root element");
const root = createRoot(rootElement);
root.render(<Application />);
