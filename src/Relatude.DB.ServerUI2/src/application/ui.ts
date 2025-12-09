import { create } from 'zustand'
import type { AppStates } from './models';
type UI = {
    darkTheme: boolean;
    toggleTheme: () => void;
    screen: AppStates;
    setScreen: (screen: AppStates) => void;
}
export const useUI = create<UI>((set) => ({
    darkTheme: window.matchMedia('(prefers-color-scheme: dark)').matches,
    toggleTheme: () => { set((state) => ({ darkTheme: !state.darkTheme })) },
    screen: "splash",
    setScreen: (screen: AppStates) => { set(() => ({ screen })) },
}));

