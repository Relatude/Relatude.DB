import { create } from 'zustand'
import type { AppStates } from './models';
export type UIContext = {
    darkTheme: boolean;
    counter: number;
    toggleTheme: () => void;
    screen: AppStates;
    setScreen: (screen: AppStates) => void;
    addCounter: () => void;
}
export const useUI = create<UIContext>((set) => {
    console.log("UIContext initialized");
    return ({
        darkTheme: window.matchMedia('(prefers-color-scheme: dark)').matches,
        toggleTheme: () => { set((state) => ({ darkTheme: !state.darkTheme })) },
        screen: "login",
        setScreen: (screen: AppStates) => { set(() => ({ screen })) },
        counter: 0,
        addCounter: () => { set((state) => ({ counter: state.counter + 1 })) },
    })
});


