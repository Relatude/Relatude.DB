import { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Button, Group, Space, Stack, Switch, Table, Tabs, Title } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { Poller } from '../../application/poller';
import { SystemLogEntry, ActionLogEntry, LogEntry, QueryLogEntry, TransactionLogEntry, TaskBatchLogEntry, MetricsLogEntry, LogInfo, PropertyHitEntry } from '../../application/models';
import { set } from 'mobx';
import { formatBytesString } from '../../utils/formatting';
import { useStore } from '../../application/test';
export const component = (P: { storeId: string }) => {
    const app = useApp();

    const [enableLog, setEnabledLog] = useState<boolean | undefined>();
    const [enableStatistics, setEnabledStatistics] = useState<boolean | undefined>();
    const [logInfos, setLogInfos] = useState<LogInfo[]>([]);

    const [systemLog, setSystemLog] = useState<LogEntry<SystemLogEntry>[]>();
    const [queryLog, setQueryLog] = useState<LogEntry<QueryLogEntry>[]>();
    const [transactionLog, setTransactionLog] = useState<LogEntry<TransactionLogEntry>[]>();
    const [actionLog, setActionLog] = useState<LogEntry<ActionLogEntry>[]>();
    const [taskLog, setTaskLog] = useState<LogEntry<any>[]>();
    const [taskBatchLog, setTaskBatchLog] = useState<LogEntry<TaskBatchLogEntry>[]>();
    const [metricsLog, setMetricsLog] = useState<LogEntry<MetricsLogEntry>[]>();

    const [propertyHits, setPropertyHits] = useState<PropertyHitEntry[]>([]);
    const [isRecordingPropertyHits, setIsRecordingPropertyHits] = useState<boolean>();

    const [isNotOpen, setIsNotOpen] = useState(false);

    const sss = useStore();

    let currentContainer = app.ui.containers.find(c => c.id === P.storeId);
    const allLogKeys = ["system", "query", "transaction", "action", "task", "taskbatch", "metrics"];
    useEffect(() => {
        const poller = new Poller(async () => {
            if (!P.storeId) return;
            if (app.ui.activeLogKey == "settings") {
                app.api.log.getLogInfos(P.storeId).then(setLogInfos);
            } else if (app.ui.activeLogKey == "propertyHits") {

                app.api.log.analyzePropertyHits(P.storeId).then(setPropertyHits);

            } else {
                app.api.log.isLogEnabled(P.storeId, app.ui.activeLogKey).then(setEnabledLog);
                app.api.log.isStatisticsEnabled(P.storeId, app.ui.activeLogKey).then(setEnabledStatistics);
                const to = new Date(); // now
                const from = new Date(to.getTime() - 60 * 60 * 1000); // 1 hour ago
                const skip = 0;
                const take = 100;
                switch (app.ui.activeLogKey) {
                    case "system": setSystemLog(await app.api.log.extractSystemLog(P.storeId, from, to, skip, take, true)); break;
                    case "query": setQueryLog(await app.api.log.extractQueryLog(P.storeId, from, to, skip, take, true)); break;
                    case "transaction": setTransactionLog(await app.api.log.extractTransactionLog(P.storeId, from, to, skip, take, true)); break;
                    case "action": setActionLog(await app.api.log.extractActionLog(P.storeId, from, to, skip, take, true)); break;
                    case "task": setTaskLog(await app.api.log.extractTaskLog(P.storeId, from, to, skip, take, true)); break;
                    case "taskbatch": setTaskBatchLog(await app.api.log.extractTaskBatchLog(P.storeId, from, to, skip, take, true)); break;
                    case "metrics": setMetricsLog(await app.api.log.extractMetricsLog(P.storeId, from, to, skip, take, true)); break;
                    default:
                        break;
                }
            }
        });
        return () => { poller.dispose(); }
    }, [app.ui.activeLogKey, P.storeId]);
    useEffect(() => {
        setIsNotOpen(app.ui.storeStates.get(P.storeId)?.state != "Open")
    }, [app.ui.storeStates.get(P.storeId)?.state])
    if (!P.storeId) return;
    if (!currentContainer) return;
    const setCurrentEnabledLog = async (enable: boolean) => {
        setEnabledLog(enable);
        await app.api.log.enableLog(P.storeId, app.ui.activeLogKey, enable);
    }
    const setCurrentEnabledStatistics = async (enable: boolean) => {
        setEnabledStatistics(enable);
        await app.api.log.enableStatistics(P.storeId, app.ui.activeLogKey, enable);
    }
    const setInfoEnabledLog = async (enable: boolean, logKey: string) => {
        logInfos.find(l => l.key == logKey)!.enabledLog = enable;
        setLogInfos(logInfos);
        await app.api.log.enableLog(P.storeId, logKey, enable);
    }
    const setInfoEnabledStatistics = async (enable: boolean, logKey: string) => {
        logInfos.find(l => l.key == logKey)!.enabledStatistics = enable;
        setLogInfos(logInfos);
        await app.api.log.enableStatistics(P.storeId, logKey, enable);
    }
    const clearAllLogs = async (logKey?: string) => {
        await Promise.all(allLogKeys.map(key => app.api.log.clearLog(P.storeId, key)));
    }
    const clearAllStatistics = async (logKey?: string) => {
        await Promise.all(allLogKeys.map(key => app.api.log.clearStatistics(P.storeId, key)));
    }
    const clearLog = async (logKey?: string) => {
        await app.api.log.clearLog(P.storeId, logKey || app.ui.activeLogKey);
    }
    const clearStatistics = async (logKey?: string) => {
        await app.api.log.clearLog(P.storeId, logKey || app.ui.activeLogKey);
    }
    const allLogsEnabled = () => {
        return logInfos.filter(l => allLogKeys.includes(l.key)).every(l => l.enabledLog);
    }
    const setAllLogsEnabled = async (enable: boolean) => {
        await Promise.all(logInfos.filter(l => allLogKeys.includes(l.key)).map(l => app.api.log.enableLog(P.storeId, l.key, enable)));
        logInfos.filter(l => allLogKeys.includes(l.key)).forEach(l => l.enabledLog = enable);
        setLogInfos(logInfos);
    }
    const allStatisticsEnabled = () => {
        return logInfos.filter(l => allLogKeys.includes(l.key)).every(l => l.enabledStatistics);
    }
    const setAllStatisticsEnabled = async (enable: boolean) => {
        await Promise.all(logInfos.filter(l => allLogKeys.includes(l.key)).map(l => app.api.log.enableStatistics(P.storeId, l.key, enable)));
        logInfos.filter(l => allLogKeys.includes(l.key)).forEach(l => l.enabledStatistics = enable);
        setLogInfos(logInfos);
    }
    const logSettingsRow = (logKey: string, logName: string, enableLog: boolean, enableStatistics: boolean) => <>
        {(app.ui.activeLogKey != "settings" && app.ui.activeLogKey != "propertyHits") ?
            <Group>
                <Switch disabled={isNotOpen} checked={enableStatistics === true} onChange={(e) => setCurrentEnabledStatistics(e.currentTarget.checked)} label="Statistics" />
                <Switch disabled={isNotOpen} checked={enableLog === true} onChange={(e) => setCurrentEnabledLog(e.currentTarget.checked)} label="Log" />
                <Button variant="light" onClick={() => clearLog()} >Clear Log</Button>
                <Button variant="light" onClick={() => clearStatistics()} >Clear Statistics</Button>
            </Group>
            : <></>}
    </>
    const logToolbar = <>
        {(app.ui.activeLogKey != "settings" && app.ui.activeLogKey != "propertyHits") ?
            <Group>
                <Switch disabled={isNotOpen} checked={enableStatistics === true} onChange={(e) => setCurrentEnabledStatistics(e.currentTarget.checked)} label="Statistics" />
                <Switch disabled={isNotOpen} checked={enableLog === true} onChange={(e) => setCurrentEnabledLog(e.currentTarget.checked)} label="Log" />
                <Button variant="light" onClick={() => clearLog()} >Clear Log</Button>
                <Button variant="light" onClick={() => clearStatistics()} >Clear Statistics</Button>
            </Group>
            : <></>}
    </>


    return (<>
        <Tabs defaultValue={app.ui.activeLogKey}>
            <Tabs.List>
                <Tabs.Tab value="system" onClick={() => app.ui.activeLogKey = "system"}>System</Tabs.Tab>
                <Tabs.Tab value="metrics" onClick={() => app.ui.activeLogKey = "metrics"}>Metrics</Tabs.Tab>
                <Tabs.Tab value="query" onClick={() => app.ui.activeLogKey = "query"}>Queries</Tabs.Tab>
                <Tabs.Tab value="transaction" onClick={() => app.ui.activeLogKey = "transaction"}>Transactions</Tabs.Tab>
                <Tabs.Tab value="action" onClick={() => app.ui.activeLogKey = "action"}>Actions</Tabs.Tab>
                <Tabs.Tab value="taskbatch" onClick={() => app.ui.activeLogKey = "taskbatch"}>Task batches</Tabs.Tab>
                <Tabs.Tab value="task" onClick={() => app.ui.activeLogKey = "task"}>Tasks</Tabs.Tab>
                <Tabs.Tab value="propertyHits" onClick={() => app.ui.activeLogKey = "propertyHits"}>Scans</Tabs.Tab>
                <Tabs.Tab value="settings" onClick={() => app.ui.activeLogKey = "settings"}>Settings</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="system">
                <Stack>
                    <Space />
                    {logToolbar}
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
                    {logToolbar}
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
                    {logToolbar}
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
                    {logToolbar}
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
                    {logToolbar}
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
                                    <Table.Td>{new Date(entry.values.started).toLocaleString()}</Table.Td>
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
                    {logToolbar}
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
                    {logToolbar}
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
            <Tabs.Panel value="propertyHits">
                <Stack>
                    <Space />
                    <Group>
                        <Switch disabled={isNotOpen} checked={isRecordingPropertyHits}
                            onChange={() => app.api.log.setPropertyHitsRecordingStatus(app.ui.selectedStoreId!, !isRecordingPropertyHits)}
                            label="Record property scans" />
                    </Group>
                    <Table>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Property</Table.Th>
                                <Table.Th>Scans</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {propertyHits?.map((entry, index) => (
                                <Table.Tr key={index}>
                                    <Table.Td>{entry.propertyName}</Table.Td>
                                    <Table.Td>{entry.hitCount}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                </Stack>

            </Tabs.Panel>
            <Tabs.Panel value="settings">
                <Stack>
                    <Table >
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Log</Table.Th>
                                <Table.Th>Statistics</Table.Th>
                                <Table.Th>Logging</Table.Th>
                                <Table.Th>First</Table.Th>
                                <Table.Th>Last</Table.Th>
                                <Table.Th>Size</Table.Th>
                                <Table.Th>Statistics</Table.Th>
                                <Table.Th>Logs</Table.Th>

                            </Table.Tr>
                        </Table.Thead>
                        {logInfos.map(info => (
                            <Table.Tr>
                                <Table.Td>{info.name}</Table.Td>
                                <Table.Td>
                                    <Switch disabled={isNotOpen} checked={info.enabledStatistics} onChange={(e) => setInfoEnabledStatistics(e.currentTarget.checked, info.key)} />
                                </Table.Td>
                                <Table.Td>
                                    <Switch disabled={isNotOpen} checked={info.enabledLog} onChange={(e) => setInfoEnabledLog(e.currentTarget.checked, info.key)} />
                                </Table.Td>
                                <Table.Td
                                    title={info.logFileSize > 1000 ? new Date(info.firstRecord).toTimeString() : "-"}
                                >{info.logFileSize > 1000 ? new Date(info.firstRecord).toDateString() : "-"}</Table.Td>
                                <Table.Td
                                    title={info.logFileSize > 1000 ? new Date(info.lastRecord).toTimeString() : "-"}
                                >{info.logFileSize > 1000 ? new Date(info.lastRecord).toDateString() : "-"}</Table.Td>
                                <Table.Td>{info.totalFileSize > 1000 ? formatBytesString(info.totalFileSize) : "-"}</Table.Td>
                                <Table.Td>
                                    <Button variant="light" onClick={() => clearLog(info.key)} >Clear</Button>
                                </Table.Td>
                                <Table.Td>
                                    <Button variant="light" onClick={() => clearStatistics(info.key)} >Clear</Button>
                                </Table.Td>
                            </Table.Tr>
                        ))}
                        <Table.Tr>
                        </Table.Tr>
                        <Table.Tr>
                            <Table.Td>All</Table.Td>
                            <Table.Td>
                                <Switch disabled={isNotOpen} checked={allStatisticsEnabled()} onChange={(e) => setAllStatisticsEnabled(e.currentTarget.checked)} />
                            </Table.Td>
                            <Table.Td>
                                <Switch disabled={isNotOpen} checked={allLogsEnabled()} onChange={(e) => setAllLogsEnabled(e.currentTarget.checked)} />
                            </Table.Td>
                            <Table.Td></Table.Td>
                            <Table.Td></Table.Td>
                            <Table.Td>{logInfos.reduce((a, b) => a + b.totalFileSize, 0) > 1000 ? formatBytesString(logInfos.reduce((a, b) => a + b.totalFileSize, 0)) : "-"}</Table.Td>
                            <Table.Td>
                                <Button variant="light" onClick={() => clearAllLogs()} >Clear</Button>
                            </Table.Td>
                            <Table.Td>
                                <Button variant="light" onClick={() => clearAllStatistics()} >Clear</Button>
                            </Table.Td>

                        </Table.Tr>
                    </Table>
                </Stack>
            </Tabs.Panel>
        </Tabs>
    </>);
}
const observerComponent = observer(component);
export default observerComponent;


