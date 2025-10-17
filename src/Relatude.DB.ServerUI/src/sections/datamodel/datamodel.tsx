import React, { useContext, useEffect, useState } from 'react';
import { useApp } from '../../start/useApp';
import { Button } from '@mantine/core';
import { ServerEventHub, EventData } from '../../application/serverEventHub';
import { DataStoreStatus } from '../../application/models';

let hubTest: ServerEventHub;
export const Datamodel = (P: { storeId: string }) => {
    const app = useApp();
    //const [settings, setSettings] = useState<ServerSettings>();
    const [selectedModelId, setSelectedModelId] = useState<string>();
    const [code, setCode] = useState<string>();
    const [model, setModel] = useState<string>();




    const selectModel = async (id: string) => {
        // setSelectedModelId(id);
        // const code = await ctx.api.datamodel.getCode(true);
        // setCode(code);
        // const model = await ctx.api.datamodel.getModel();
        // setModel(JSON.stringify(model, null, 2));
    }
    const updateSettings = async () => {
        setCode(await app.api.datamodel.getCode(P.storeId, true));
    }
    useEffect(() => {
        updateSettings();
    }, []);
    const reIndexAll = async () => {
        const result = await app.api.data.queueReIndexAll(P.storeId);
        alert(`Reindexed ${result} items`);
    }
    const populateDemo = async () => {
        const countStr = prompt("How many items to create?", "1000");
        if (!countStr || isNaN(Number(countStr))) {
            alert("Invalid count");
            return;
        }
        const count = Number(countStr);
        const wikipediaData = confirm("Use Wikipedia data?");
        const result = await app.api.demo.populate(P.storeId, count, wikipediaData);
        alert(`Created ${result.countCreated} items in ${result.elapsedMs} ms, ${Math.round(result.countCreated / (result.elapsedMs / 1000))} items/sec`);
    }
    const connectEvents = async () => {
        hubTest = new ServerEventHub(app.api, (event: EventData<unknown>) => {
            //console.log("Event", event);
            //alert("Event: " + event.name + " " + JSON.stringify(event.data));
        }, (error) => {
            console.log("Event error", error);
        }, (error) => {
            console.log("Connection error", error);
        });
        await hubTest.connect();
        hubTest.addEventListener<string>("test", undefined, (data, filter) => {
            console.log("Test event", data);
        });
    }
    const subscribeEvents = async () => {
        if (!hubTest) {
            alert("Not connected");
            return;
        }
        await hubTest.addEventListener<string>("ServerStatus", undefined, (data, filter) => {
            console.log("ServerStatus event", data);
        });
        await hubTest.addEventListener<DataStoreStatus>("DataStoreStatus", app.ui.selectedStoreId, (data, filter) => {
            // console.log("DataStoreStatus event", data, filter);
            setCode(JSON.stringify(data, null, 2));
        });

    }
    return (
        <>
            <div>
                <h1>Datamodel</h1>
                <Button onClick={populateDemo}>Add demo content</Button>
                <Button onClick={reIndexAll}>Re index content</Button>
                <Button onClick={connectEvents}>Connect</Button>
                <Button onClick={subscribeEvents}>Subscribe</Button>
                {/* {settings && settings.datamodelSources && settings.datamodelSources.map((source, index) => (
                    <div style={{ fontWeight: source.id == selectedModelId ? "bold" : "" }} onClick={(e) => selectModel(source.id)} key={source.id}>{source.name}</div>
                ))} */}
            </div>
            <div>
                <h1>C# Code</h1>
                <textarea cols={80} rows={30} value={code} onChange={(e) => setCode(e.target.value)} />
                {/* <h1>JSON</h1>
                <textarea cols={80} rows={30} value={model} onChange={(e) => setCode(e.target.value)} /> */}
            </div>
        </>
    )
}

