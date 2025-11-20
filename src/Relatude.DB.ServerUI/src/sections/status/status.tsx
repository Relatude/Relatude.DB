import React, { useContext, useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Box, Button, Group, Text, Title } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { Poller } from '../../application/poller';


export const component = () => {
    const app = useApp();
    const storeId = app.ui.selectedStoreId;
    const status = app.ui.getStoreStatus(app.ui.selectedStoreId);
    const [currentInfo, setInfo] = useState<string>();
    let currentContainer = app.ui.containers.find(c => c.id === app.ui.selectedStoreId);
    useEffect(() => {
        const poller = new Poller(async () => {
            if (app.ui.selectedStoreId) setInfo(JSON.stringify(await app.api.maintenance.info(app.ui.selectedStoreId), null, 2));
        });
        return () => { poller.dispose(); }
    }, []);
    const truncateLog = () => {
        if (!storeId) return;
        const deleteOld = window.confirm("Do you want to delete old log entries? (OK = Yes, Cancel = No)");
        app.api.maintenance.truncateLog(storeId, deleteOld);
    }
    if (!storeId) return;
    if (!currentContainer) return;
    const state = status.state;
    return (
        <>
            <Title>{currentContainer.name}</Title>
            <Group>
                <Button variant="light" disabled={state != "Disposed"} onClick={() => app.api.maintenance.initialize(app.ui.selectedStoreId!)}>Initialize</Button>
                <Button variant="light" disabled={state != "Closed"} onClick={() => app.api.maintenance.open(app.ui.selectedStoreId!)}>Open</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.close(app.ui.selectedStoreId!)}>Close</Button>
                <Button variant="light" disabled={state != "Closed" && state != "Error"} onClick={() => app.api.maintenance.initialize(app.ui.selectedStoreId!)}>Dispose</Button>

                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.saveIndexStates(app.ui.selectedStoreId!, true, false)}>Save Index</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.clearCache(app.ui.selectedStoreId!)}>Clear Cache</Button>
                <Button variant="light" disabled={state != "Open"} onClick={truncateLog}>Compact</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.resetSecondaryLogFile(app.ui.selectedStoreId!)}>Reset second log</Button>
                <Button variant="light" disabled={state != "Closed" && state != "Error"} onClick={() => app.api.maintenance.resetStateAndIndexes(app.ui.selectedStoreId!)}>Reset all states</Button>
            </Group>
            <pre>
                {JSON.stringify(status, null, 2)}
                {currentInfo}
            </pre>
        </>
    )
}
const Status = observer(component);
export default Status;


