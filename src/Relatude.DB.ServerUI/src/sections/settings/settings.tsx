import React, { ReactElement, useContext, useEffect, useState } from 'react';
import { useApp } from '../../start/useApp';
import { NodeStoreContainer } from '../../application/models';
//import { ServerSettings } from '../../application/api';

export const Settings = (P: { storeId: string }) => {
    const app = useApp();
    var [settings, setSettings] = useState<NodeStoreContainer>();
    var [settingsString, setSettingsString] = useState<string>();
    const updateSettings = async () => {
        var newSettings = await app.api.settings.getSettings(P.storeId, true);
        setSettings(newSettings);
        setSettingsString(JSON.stringify(newSettings, null, '\t'));
    }
    const saveSettings = async () => {
        if (!window.confirm("Are you sure you want to save the settings?")) return;
        try {
            if (!settingsString) throw new Error("Settings string is empty");
            var newSettings = JSON.parse(settingsString);
            if (settings) await app.api.settings.setSettings(P.storeId, newSettings);
            await updateSettings();
        } catch (err) {
            alert("Error parsing settings: " + (err as Error).message);
        }
    }
    const reSaveSettings = async () => {
        await app.api.settings.reSaveSettings(P.storeId);
        await updateSettings();
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
                }} />
                <div>
                    <button onClick={updateSettings}>Reload</button>
                    <button onClick={saveSettings}>Save</button>
                    <button onClick={reSaveSettings}>Re Save</button>
                </div>
            </div>
        </>
    )
}


