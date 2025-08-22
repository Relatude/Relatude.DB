import React, { useEffect, useState } from 'react';
import FileList from './fileList';
import { useApp } from '../../start/useApp';
import { observer } from 'mobx-react-lite';
import { Box, Group } from '@mantine/core';

export const component = (P: { storeId: string }) => {
    const app = useApp();
    const container = app.ui.containers.find(c => c.id === P.storeId);
    const [ioId, setIoId] = useState<string | undefined>();
    useEffect(() => {
        if (container) { // default to db IO for now....
            app.api.settings.getSettings(container.id).then(settings => setIoId(settings.ioSettings[0]?.id));
        }
    }, [app.ui.selectedStoreId]);
    return (
        <>
            <Box>
                {/* {app.ui.selectedStoreId ? <>
                    <Group>
                        <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.backUpNow(app.ui.selectedStoreId!, ioId!, true, true).then(updateFiles)}>Backup Now Truncated</Button>
                        <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.backUpNow(app.ui.selectedStoreId!, ioId!, false, true).then(updateFiles)}>Backup Now Copy</Button> 
                        <Button variant="light" onClick={() => app.api.maintenance.cleanTempFiles().then(updateFiles)}>Clean Temp Files</Button>
                        <Button variant="light" onClick={async () => alert(await app.api.maintenance.getSizeTempFiles())}>GetSizeTempFiles</Button>
                    </Group>
                </> : null */}                
                <Group>
                    <FileList storeId={app.ui.selectedStoreId!} />
                </Group>

            </Box>
        </>
    )
}

const observableComponent = observer(component);
export default observableComponent;
