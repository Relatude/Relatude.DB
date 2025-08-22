import React, { ReactElement, useContext, useEffect, useState } from 'react';
import { useApp } from '../../start/useApp';
//import { ServerSettings } from '../../application/api';

export const Settings = (P: { storeId: string }) => {
    const app = useApp();
    //var [settings, setSettings] = useState<ServerSettings>();
    var [settingsString, setSettingsString] = useState<string>();
    var [settingsStringState, setSettingsStringState] = useState<string>();
    const updateSettings = async () => {
        // var newSettings = await ctx.api.settings.getSettings();
        // setSettings(newSettings);
        // setSettingsString(JSON.stringify(newSettings, null, '\t'));
    }
    const saveSettings = async () => {
        // if (settings) await ctx.api.settings.setSettings(settings);
        // await updateSettings();
    }
    useEffect(() => {
        updateSettings();
    }, []);
    return (
        <>
            <div>
                <h1>Settings</h1>
                <textarea cols={80} rows={30} value={settingsString} onChange={(e) => {
                    setSettingsString(e.target.value);
                    try {
                        //setSettings(JSON.parse(e.target.value));
                        setSettingsStringState("");
                    } catch (error) {
                        setSettingsStringState(error.message);
                    }
                }} />
                <div>
                    <button onClick={updateSettings}>Reload</button>
                    <button onClick={saveSettings}>Save</button>
                </div>
                <div style={{ color: "red" }} >{settingsStringState}</div>
            </div>
        </>
    )
}


