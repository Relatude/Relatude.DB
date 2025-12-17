import { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Button, Card, Group, Text, Grid } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { DataStoreStatus, DataStoreInfo, AnalysisEntry } from '../../application/models';
import { formatBytes, formatNumber, formatTimeSpan } from '../../application/common';
import { LineChart } from '@mantine/charts';
import { SimplePlot } from './simplePlot';

const Panel = (P: { title?: string, span?: number, children?: React.ReactNode }) => {
    return (
        <Grid.Col span={P.span} >
            <Card withBorder>
                <Group>
                    <Text fw={500}>{P.title}</Text>
                </Group>
                {P.children}
            </Card>
        </Grid.Col>
    )
}
export const component = () => {
    const app = useApp();
    const storeId = app.ui.selectedStoreId;
    const [currentStatus, setCurrentStatus] = useState<DataStoreStatus>();
    const [currentInfo, setCurrentInfo] = useState<DataStoreInfo>();
    let currentContainer = app.ui.containers.find(c => c.id === app.ui.selectedStoreId);
    useEffect(() => {
        if (!app.ui.selectedStoreId) return;
        let subIdStatus: string;
        app.serverEvents.addEventListener<DataStoreStatus>("DataStoreStatus", app.ui.selectedStoreId, (status) => {
            setCurrentStatus(status);
        }).then(subId => { subIdStatus = subId; });
        let subIdInfo: string;
        app.serverEvents.addEventListener<DataStoreInfo>("DataStoreInfo", app.ui.selectedStoreId, (info) => {
            setCurrentInfo(info);
        }).then(subId => { subIdInfo = subId; });
        return () => {
            app.serverEvents.removeEventListener<DataStoreStatus>(subIdStatus);
            app.serverEvents.removeEventListener<DataStoreInfo>(subIdInfo);
        }
    }, [app.ui.selectedStoreId]);
    const truncateLog = () => {
        if (!storeId) return;
        const deleteOld = window.confirm("Do you want to delete old log entries? (OK = Yes, Cancel = No)");
        app.api.maintenance.truncateLog(storeId, deleteOld);
    }
    if (!storeId) return;
    if (!currentContainer) return;
    if (!currentInfo) return;
    const state = app.ui.getStoreState(app.ui.selectedStoreId);
    return (
        <>
            <Grid mt="md">
                <Panel>
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
                </Panel>
                <Panel title="Info" span={3}>
                    <div>State: {state}</div>
                    <div>Uptime: {formatTimeSpan(currentInfo.uptimeMs)}</div>
                    <div>Memory: {formatBytes(currentInfo.processWorkingMemory)}</div>
                    <div>CPU: {currentInfo.cpuUsagePercentage.toFixed(1)}%</div>
                    <div>CPU (1 min avg): {currentInfo.cpuUsagePercentageLastMinute.toFixed(1)}%</div>
                    <div>Startup: {formatTimeSpan(currentInfo.startUpMs)}</div>
                </Panel>
                <Panel title="Activity" span={3}>
                    {currentStatus?.activityTree && (
                        <pre style={{ maxHeight: '400px', overflow: 'auto' }}>
                            {currentStatus.activityTree.map(line => line.activity.description + "\n")}

                        </pre>
                    )}
                </Panel>
                <Panel title="Trace" span={3}>
                    <pre style={{ maxHeight: '400px', overflow: 'auto' }}>
                        {currentInfo.latestTraces?.map(trace => `${trace.timestamp.toLocaleString()} [${trace.type}] ${trace.text}\n`)}
                    </pre>
                </Panel>
                <Panel title="Cache" span={3}>
                    {formatBytes(currentInfo.setCacheSize)} set cache size<br />
                    {currentInfo.setCacheSizePercentage.toFixed(1)}% set cache used<br />
                    {formatNumber(currentInfo.setCacheCount)} sets cached<br />
                    {formatNumber(currentInfo.setCacheHits)} set cache hits<br />
                    {formatNumber(currentInfo.setCacheMisses)} set cache misses<br />
                    {formatNumber(currentInfo.setCacheOverflows)} set cache overflows<br />
                    <br />
                    {formatBytes(currentInfo.nodeCacheSize)} node cache size<br />
                    {currentInfo.nodeCacheSizePercentage.toFixed(1)}% node cache used<br />
                    {formatNumber(currentInfo.nodeCacheCount)} nodes cached<br />
                    {formatNumber(currentInfo.nodeCacheHits)} node cache hits<br />
                    {formatNumber(currentInfo.nodeCacheMisses)} node cache misses<br />
                    {formatNumber(currentInfo.nodeCacheOverflows)} node cache overflows<br />
                    <Button mt="sm" variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.clearCache(app.ui.selectedStoreId!)}>Clear Cache</Button>
                </Panel>
                <Panel title="Content" span={3}>
                    {Object.entries(currentInfo.typeCounts).map(([type, count]) => (
                        <div key={type}>{type}: {count}</div>
                    ))}

                </Panel>
                <Panel title="Storage" span={3}>
                    {formatBytes(currentInfo.totalFileSize)} total file size<br />
                    {formatBytes(currentInfo.logFileSize)} bytes in main log file<br />
                    {formatBytes(currentInfo.logStateFileSize)} bytes in state log file<br />
                    {formatBytes(currentInfo.indexFileSize)} bytes in indexes<br />
                    {formatBytes(currentInfo.fileStoreSize)} bytes in filestore<br />
                    {formatBytes(currentInfo.backupFileSize)} bytes in backups<br />
                    {formatBytes(currentInfo.loggingFileSize)} bytes in logging<br />
                    {formatBytes(currentInfo.secondaryLogFileSize)} bytes in secondary log<br />
                    {formatNumber(currentInfo.logActionsNotItInStatefile)} actions not in state file<br />
                    {formatNumber(currentInfo.logTransactionsNotItInStatefile)} transactions not in state file<br />
                    {formatNumber(currentInfo.logWritesQueuedActions)} queued action writes<br />
                    {formatNumber(currentInfo.logWritesQueuedTransactions)} queued transaction writes<br />
                </Panel>
                <Panel title="Qued tasks" span={3}>
                    {Object.entries(currentInfo.queuedTaskStateCounts).map(([state, count]) => (
                        <div key={state}>{state}: {count}</div>
                    ))}
                    {Object.entries(currentInfo.queuedTaskStateCountsPersisted).map(([state, count]) => (
                        <div key={state}>{state}: {count}</div>
                    ))}
                </Panel>
                <Panel title="Queries per second" span={3}>
                    <SimplePlot logKey="query" />
                </Panel>
                <Panel title="Transactions per second" span={3}>
                    <SimplePlot logKey="transaction" />
                </Panel>
                <Panel title="Actions per second" span={3}>
                    <SimplePlot logKey="action" />
                </Panel>
            </Grid>
        </>
    )
}
const Status = observer(component);
export default Status;

