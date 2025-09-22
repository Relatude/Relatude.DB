import { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Button, Group, Space, Stack, Switch, Table, Tabs, Title } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { Poller } from '../../application/poller';
import { SystemLogEntry, ActionLogEntry, LogEntry, QueryLogEntry, TransactionLogEntry, TaskBatchLogEntry, MetricsLogEntry } from '../../application/models';
import { set } from 'mobx';
export const component = (P: { storeId: string }) => {
    const app = useApp();

    const [enableLog, setEnabledLog] = useState<boolean | undefined>();
    const [enableStatistics, setEnabledStatistics] = useState<boolean | undefined>();

    const [systemLog, setSystemLog] = useState<LogEntry<SystemLogEntry>[]>();
    const [queryLog, setQueryLog] = useState<LogEntry<QueryLogEntry>[]>();
    const [transactionLog, setTransactionLog] = useState<LogEntry<TransactionLogEntry>[]>();
    const [actionLog, setActionLog] = useState<LogEntry<ActionLogEntry>[]>();
    const [taskLog, setTaskLog] = useState<LogEntry<any>[]>();
    const [taskBatchLog, setTaskBatchLog] = useState<LogEntry<TaskBatchLogEntry>[]>();
    const [metricsLog, setMetricsLog] = useState<LogEntry<MetricsLogEntry>[]>();

    const [activeLogKey, setActiveLogKey] = useState<string>("system");
    const [propertyHits, setPropertyHits] = useState<string>();
    const [isRecordingPropertyHits, setIsRecordingPropertyHits] = useState<boolean>();
    let currentContainer = app.ui.containers.find(c => c.id === P.storeId);
    useEffect(() => {
        const poller = new Poller(async () => {
            if (!P.storeId) return;
            app.api.log.isLogEnabled(P.storeId, activeLogKey).then(setEnabledLog);
            app.api.log.isStatisticsEnabled(P.storeId, activeLogKey).then(setEnabledStatistics);
            const to = new Date(); // now
            const from = new Date(to.getTime() - 60 * 60 * 1000); // 1 hour ago
            const skip = 0;
            const take = 100;
            switch (activeLogKey) {
                case "system": setSystemLog(await app.api.log.extractSystemLog(P.storeId, from, to, skip, take)); break;
                case "query": setQueryLog(await app.api.log.extractQueryLog(P.storeId, from, to, skip, take)); break;
                case "transaction": setTransactionLog(await app.api.log.extractTransactionLog(P.storeId, from, to, skip, take)); break;
                case "action": setActionLog(await app.api.log.extractActionLog(P.storeId, from, to, skip, take)); break;
                case "task": setTaskLog(await app.api.log.extractTaskLog(P.storeId, from, to, skip, take)); break;
                case "taskbatch": setTaskBatchLog(await app.api.log.extractTaskBatchLog(P.storeId, from, to, skip, take)); break;
                case "metrics": setMetricsLog(await app.api.log.extractMetricsLog(P.storeId, from, to, skip, take)); break;
                default:
                    break;
            }
        });
        return () => { poller.dispose(); }
    }, [activeLogKey, P.storeId]);
    if (!P.storeId) return;
    if (!currentContainer) return;
    const setCurrentEnabledLog = async (enable: boolean) => {
        setEnabledLog(enable);
        await app.api.log.enableLog(P.storeId, activeLogKey, enable);
    }
    const setCurrentEnabledStatistics = async (enable: boolean) => {
        setEnabledStatistics(enable);
        await app.api.log.enableStatistics(P.storeId, activeLogKey, enable);
    }
    const clearLog = async () => {
        await app.api.log.clearLog(P.storeId, activeLogKey);
    }
    const clearStatistics = async () => {
        await app.api.log.clearLog(P.storeId, activeLogKey);
    }
    const queryLogToolbar = <>
        <Group>
            <Switch checked={enableStatistics === true} onChange={(e) => setCurrentEnabledStatistics(e.currentTarget.checked)} label="Statistics" />
            <Switch checked={enableLog === true} onChange={(e) => setCurrentEnabledLog(e.currentTarget.checked)} label="Log" />
            <Button variant="light" onClick={() => clearLog()} >Clear Log</Button>
            <Button variant="light" onClick={() => clearStatistics()} >Clear Statistics</Button>
        </Group>
        {/* <Button disabled={enabled === undefined || enabled === true} variant="light" onClick={() => setEnabledStatus(true)} >Enable</Button>
        <Button disabled={enabled === undefined || enabled === false} variant="light" onClick={() => setEnabledStatus(false)} >Disable</Button> */}
        {/* <Button disabled={enabledDetails === undefined || enabledDetails === true} variant="light" onClick={() => setEnabledDetailsStatus(true)} >Enable details</Button>
        <Button disabled={enabledDetails === undefined || enabledDetails === false} variant="light" onClick={() => setEnabledDetailsStatus(false)} >Disable details</Button> */}
    </>
    return (<>
        <Tabs defaultValue="system">
            <Tabs.List>
                <Tabs.Tab value="system" onClick={() => setActiveLogKey("system")}>System</Tabs.Tab>
                <Tabs.Tab value="query" onClick={() => setActiveLogKey("query")}>Queries</Tabs.Tab>
                <Tabs.Tab value="transaction" onClick={() => setActiveLogKey("transaction")}>Transactions</Tabs.Tab>
                <Tabs.Tab value="action" onClick={() => setActiveLogKey("action")}>Actions</Tabs.Tab>
                <Tabs.Tab value="taskbatch" onClick={() => setActiveLogKey("taskbatch")}>Task batches</Tabs.Tab>
                <Tabs.Tab value="task" onClick={() => setActiveLogKey("task")}>Tasks</Tabs.Tab>
                <Tabs.Tab value="metrics" onClick={() => setActiveLogKey("metrics")}>Metrics</Tabs.Tab>
                <Tabs.Tab value="propertyHits" onClick={() => setActiveLogKey("propertyHits")}>Property Hits</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="system">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>System</Table.Th>
                                <Table.Th>Type</Table.Th>
                                <Table.Th>Text</Table.Th>
                                <Table.Th>Details</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {systemLog?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                    <Table.Td>{entry.values.type}</Table.Td>
                                    <Table.Td>{entry.values.text}</Table.Td>
                                    <Table.Td>{entry.values.details ? <Button variant="light" onClick={() => alert(entry.values.details)} >Details</Button> : <></>}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="query">
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
            <Tabs.Panel value="transaction">
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
            <Tabs.Panel value="action">
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
            <Tabs.Panel value="task">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Task</Table.Th>
                                <Table.Th>ID</Table.Th>
                                <Table.Th>Started</Table.Th>
                                <Table.Th>Duration</Table.Th>
                                <Table.Th>Success</Table.Th>
                                <Table.Th>Error</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {taskLog?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                    <Table.Td>{entry.values.taskId}</Table.Td>
                                    <Table.Td>{entry.values.started}</Table.Td>
                                    <Table.Td>{entry.values.duration}</Table.Td>
                                    <Table.Td>{entry.values.success}</Table.Td>
                                    <Table.Td>{entry.values.error}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="taskbatch">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Task Batch</Table.Th>
                                <Table.Th>ID</Table.Th>
                                <Table.Th>Started</Table.Th>
                                <Table.Th>Duration</Table.Th>
                                <Table.Th>Task count</Table.Th>
                                <Table.Th>Success</Table.Th>
                                <Table.Th>Error</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {taskBatchLog?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                    <Table.Td>{entry.values.batchId}</Table.Td>
                                    <Table.Td>{entry.values.started.toString()}</Table.Td>
                                    <Table.Td>{entry.values.duration}</Table.Td>
                                    <Table.Td>{entry.values.taskCount}</Table.Td>
                                    <Table.Td>{entry.values.success}</Table.Td>
                                    <Table.Td>{entry.values.error}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="metrics">
                <Stack>
                    <Space />
                    {queryLogToolbar}
                </Stack>
                <Table>
                    <Table.Thead>
                        <Table.Tr>
                            <Table.Th>Metric</Table.Th>
                            <Table.Th>Query Count</Table.Th>
                            <Table.Th>Transaction Count</Table.Th>
                            <Table.Th>Node Count</Table.Th>
                            <Table.Th>Relation Count</Table.Th>
                            <Table.Th>Node Cache Count</Table.Th>
                            <Table.Th>Node Cache Size</Table.Th>
                            <Table.Th>Set Cache Count</Table.Th>
                            <Table.Th>Set Cache Size</Table.Th>
                        </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                        {metricsLog?.map((entry, index) => (
                            <Table.Tr key={index}>
                                <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                                <Table.Td>{entry.values.queryCount}</Table.Td>
                                <Table.Td>{entry.values.transactionCount}</Table.Td>
                                <Table.Td>{entry.values.nodeCount}</Table.Td>
                                <Table.Td>{entry.values.relationCount}</Table.Td>
                                <Table.Td>{entry.values.nodeCacheCount}</Table.Td>
                                <Table.Td>{entry.values.nodeCacheSize}</Table.Td>
                                <Table.Td>{entry.values.setCacheCount}</Table.Td>
                                <Table.Td>{entry.values.setCacheSize}</Table.Td>
                            </Table.Tr>
                        ))}
                    </Table.Tbody>
                </Table>
            </Tabs.Panel>
            <Tabs.Panel value="taskqueue">
                <Title>Task Queue:</Title>
                <pre>Not implemented yet</pre>
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


