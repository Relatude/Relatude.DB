import { ActionIcon, Button, CloseButton, Tabs } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { observer } from 'mobx-react-lite';
import { QueryBuilder, QueryBuilderStore } from './queryBuilder';
import { IconCross, IconPlus, IconSearch, IconX } from '@tabler/icons-react';
import { useState } from 'react';

const component = (P: { storeId: string }) => {
    const app = useApp();
    const [showCloseButton, setShowCloseButton] = useState("");
    const newQuery = async () => {
        if (!app.ui.isCurrentStoreOpen()) return;
        const datamodel = await app.api.datamodel.getModel(P.storeId);
        const qb = new QueryBuilderStore(datamodel, app.connection, P.storeId);
        app.ui.addQueryBuilder(P.storeId, qb);
        app.ui.setSelectedQueryBuilderId(P.storeId, qb.id);
    }
    const builders = app.ui.getQueryBuilders(P.storeId);
    const selectedBuilder = app.ui.getSelectedQueryBuilderId(P.storeId);
    return (
        <>
            <Tabs value={selectedBuilder} onChange={(v) => app.ui.setSelectedQueryBuilderId(P.storeId, v)}>
                <Tabs.List>
                    {builders.map((q) => (
                        <Tabs.Tab key={q.id} value={q.id}
                            onMouseOver={() => setShowCloseButton(q.id)}
                            onMouseLeave={() => setShowCloseButton("")}
                            leftSection={<IconSearch size={12} />}
                            rightSection={showCloseButton == q.id ?
                                <ActionIcon variant="transparent" onClick={() => app.ui.removeQueryBuilder(P.storeId, q)} >
                                    <IconX style={{ width: '70%', height: '70%' }} stroke={1.5} />
                                </ActionIcon> :
                                <ActionIcon hidden variant="transparent" >
                                </ActionIcon>
                            }
                        >
                            {q.name}
                        </Tabs.Tab>
                    ))}
                    <Tabs.Tab value="newQuery" onClick={newQuery} leftSection={<ActionIcon variant="transparent" >
                        <IconPlus style={{ width: '70%', height: '70%' }} stroke={1.5} /></ActionIcon>}>
                    </Tabs.Tab>
                </Tabs.List>

                {builders.map((q) => (
                    <Tabs.Panel key={q.id} value={q.id} pt="xs">
                        <QueryBuilder store={q} storeId={P.storeId} />
                    </Tabs.Panel>
                ))}
            </Tabs>
        </>
    )
}

const Data = observer(component);
export default Data;

