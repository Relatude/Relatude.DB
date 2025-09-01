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
    if (!storeId) return;
    if (!currentContainer) return;
    const state = status.state;
    return (
        <>
            <Title>{currentContainer.name}</Title>
            <Group>
                <Button variant="light" disabled={state != "Closed"} onClick={() => app.api.maintenance.open(app.ui.selectedStoreId!)}>Start</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.close(app.ui.selectedStoreId!)}>Stop</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.saveIndexStates(app.ui.selectedStoreId!)}>Save Index</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.clearCache(app.ui.selectedStoreId!)}>Clear Cache</Button>
                <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.truncateLog(app.ui.selectedStoreId!)}>Compact</Button>
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


