import { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Button, Group, Space, Stack, Switch, Table, Tabs, Title } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { Poller } from '../../application/poller';
import { ActionLogValues, ContainerLogEntry, LogEntry, QueryLogValues, TransactionLogValues } from '../../application/models';
export const component = (P: { storeId: string }) => {
    const app = useApp();
    // let status = app.ui.getStoreStatus(app.ui.selectedStoreId);
    const [containerLog, setContainerLog] = useState<ContainerLogEntry[]>();
    const [queryLog, setQueryLog] = useState<LogEntry<QueryLogValues>[]>();
    const [enabled, setEnabled] = useState<boolean | undefined>();
    const [enabledDetails, setEnabledDetails] = useState<boolean | undefined>();
    const [transactionLog, setTransactionLog] = useState<LogEntry<TransactionLogValues>[]>();
    const [actionLog, setActionLog] = useState<LogEntry<ActionLogValues>[]>();
    const [active, setActive] = useState<string>("system");
    const [propertyHits, setPropertyHits] = useState<string>();
    const [isRecordingPropertyHits, setIsRecordingPropertyHits] = useState<boolean>();
    let currentContainer = app.ui.containers.find(c => c.id === P.storeId);
    useEffect(() => {
        const poller = new Poller(async () => {
            if (!P.storeId) return;
            setContainerLog(await app.api.log.getContainerLog(P.storeId, 0, 50));
            const from = new Date()
            from.setSeconds(from.getSeconds() - 100);
            const to = new Date();
            to.setSeconds(to.getSeconds() + 1);
            setQueryLog(await app.api.log.extractQueryLog(P.storeId, from, to, 0, 50));
            setTransactionLog(await app.api.log.extractTransactionLog(P.storeId, from, to, 0, 50));
            setActionLog(await app.api.log.extractActionLog(P.storeId, from, to, 0, 50));
            setIsRecordingPropertyHits(await app.api.log.isRecordingPropertyHits(P.storeId));
            if (isRecordingPropertyHits) setPropertyHits(JSON.stringify(await app.api.log.analysePropertyHits(P.storeId), null, 2));
            setEnabled(await app.api.log.isEnabled(P.storeId));
            setEnabledDetails(await app.api.log.isEnabledDetails(P.storeId));
        });
        return () => { poller.dispose(); }
    }, []);
    if (!P.storeId) return;
    if (!currentContainer) return;
    const setEnabledStatus = async (enable: boolean) => {
        setEnabled(undefined);
        await app.api.log.enable(app.ui.selectedStoreId!, enable);
        setEnabled(enable);
    }
    const setEnabledDetailsStatus = async (enable: boolean) => {
        setEnabledDetails(undefined);
        await app.api.log.enableDetails(app.ui.selectedStoreId!, enable);
        setEnabledDetails(enable);
        if (enable) await setEnabledStatus(true);
    }
    const queryLogToolbar = <>
        <Group>
            <Switch checked={enabled === true} onChange={(e) => setEnabledStatus(e.currentTarget.checked)} label="Statistics" />
            <Switch checked={enabledDetails === true} onChange={(e) => setEnabledDetailsStatus(e.currentTarget.checked)} label="Log" />
            <Button variant="light" onClick={() => app.api.log.clear(app.ui.selectedStoreId!)} >Clear</Button>
        </Group>
        {/* <Button disabled={enabled === undefined || enabled === true} variant="light" onClick={() => setEnabledStatus(true)} >Enable</Button>
        <Button disabled={enabled === undefined || enabled === false} variant="light" onClick={() => setEnabledStatus(false)} >Disable</Button> */}
        {/* <Button disabled={enabledDetails === undefined || enabledDetails === true} variant="light" onClick={() => setEnabledDetailsStatus(true)} >Enable details</Button>
        <Button disabled={enabledDetails === undefined || enabledDetails === false} variant="light" onClick={() => setEnabledDetailsStatus(false)} >Disable details</Button> */}
    </>
    return (<>
        <Tabs defaultValue="system">
            <Tabs.List>
                <Tabs.Tab value="system" onClick={() => setActive("system")}>System</Tabs.Tab>
                <Tabs.Tab value="queries" onClick={() => setActive("queries")}>Queries</Tabs.Tab>
                <Tabs.Tab value="transactions" onClick={() => setActive("transactions")}>Transactions</Tabs.Tab>
                <Tabs.Tab value="actions" onClick={() => setActive("actions")}>Actions</Tabs.Tab>
                <Tabs.Tab value="propertyHits" onClick={() => setActive("propertyHits")}>Table scans</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="system">
                <Table>
                    <Table.Thead>
                        <Table.Tr>
                            <Table.Th>System log</Table.Th>
                            <Table.Th><Button variant="light" onClick={() => app.api.log.clearContainerLog(app.ui.selectedStoreId!)} >Clear</Button></Table.Th>
                        </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                        {containerLog?.map((entry, index) => (
                            <Table.Tr key={index}>
                                <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                <Table.Td>{entry.description}</Table.Td>
                            </Table.Tr>
                        ))}
                    </Table.Tbody>
                </Table>
            </Tabs.Panel>
            <Tabs.Panel value="queries">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Queries</Table.Th>
                                <Table.Th>Query</Table.Th>
                                <Table.Th>Duration</Table.Th>
                                <Table.Th>Rows</Table.Th>
                                <Table.Th>Unique nodes</Table.Th>
                                <Table.Th>Nodes</Table.Th>
                                <Table.Th>Disk reads</Table.Th>
                                <Table.Th>Nodes reads</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {queryLog?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                    <Table.Td>{entry.values.query}</Table.Td>
                                    <Table.Td>{entry.values.duration}</Table.Td>
                                    <Table.Td>{entry.values.resultCount}</Table.Td>
                                    <Table.Td>{entry.values.uniqueNodeCount}</Table.Td>
                                    <Table.Td>{entry.values.nodeCount}</Table.Td>
                                    <Table.Td>{entry.values.diskReads}</Table.Td>
                                    <Table.Td>{entry.values.nodesReadFromDisk}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="transactions">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Transactions</Table.Th>
                                <Table.Th>ID</Table.Th>
                                <Table.Th>Duration</Table.Th>
                                <Table.Th>Actions</Table.Th>
                                <Table.Th>Primitive actions</Table.Th>
                                <Table.Th>Disk flush</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {transactionLog?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                    <Table.Td>{entry.values.transactionId}</Table.Td>
                                    <Table.Td>{entry.values.duration}</Table.Td>
                                    <Table.Td>{entry.values.actionCount}</Table.Td>
                                    <Table.Td>{entry.values.primitiveActionCount}</Table.Td>
                                    <Table.Td>{entry.values.diskFlush}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="actions">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Actions</Table.Th>
                                <Table.Th>Transaction</Table.Th>
                                <Table.Th>Operation</Table.Th>
                                <Table.Th>Details</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {actionLog?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                    <Table.Td>{entry.values.transactionId}</Table.Td>
                                    <Table.Td>{entry.values.operation}</Table.Td>
                                    <Table.Td>{entry.values.details}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="propertyHits">
                <Title>Non indexed property hits:</Title>
                <Button variant="light" color={isRecordingPropertyHits ? "red" : "green"}
                    onClick={() => app.api.log.setPropertyHitsRecordingStatus(app.ui.selectedStoreId!, !isRecordingPropertyHits)}
                >{isRecordingPropertyHits ? "stop" : "start"}</Button>
                <pre>{propertyHits}</pre>
            </Tabs.Panel>
        </Tabs>
    </>);
}
const observerComponent = observer(component);
export default observerComponent;


