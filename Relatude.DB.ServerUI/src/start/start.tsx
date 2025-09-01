import "@mantine/core/styles.css";
import { createRoot } from 'react-dom/client';
import { useApp, AppContext, createApp } from './useApp';
import { MantineProvider } from '@mantine/core';
import { observer } from 'mobx-react-lite';
import { Splash } from '../sections/splash/splash';
import { Login } from '../sections/login/login';
import { Main } from './main';

const component = () => {
    const app = useApp();
    const screen = app.ui.appState;
    return (
        <MantineProvider forceColorScheme={app.ui.darkTheme ? "dark" : "light"}>
            {screen === 'splash' && <Splash />}
            {screen === 'login' && <Login />}
            {screen === 'main' && <Main />}
        </MantineProvider>
    );
}
const Application = observer(component);

const root = createRoot(document.getElementById('app')!);
root.render(
    <AppContext.Provider value={createApp()}>
        <Application />
    </AppContext.Provider>
);

