import { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Button, Card, Group, Text, Grid } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { DataStoreStatus, DataStoreInfo, DataStoreActivityBranch, SystemTraceEntry } from '../../application/models';
import { formatBytes, formatNumber, formatTimeOfDay, formatTimeSpan } from '../../application/common';
import { SimplePlot } from './simplePlot';
import { Activity } from './activity';

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
    const [info, setCurrentInfo] = useState<DataStoreInfo>();
    const [currentTrace, setCurrentTrace] = useState<SystemTraceEntry[]>([]);
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
        let subIdTrace: string;
        app.serverEvents.addEventListener<SystemTraceEntry[]>("DataStoreTrace", app.ui.selectedStoreId, (trace) => {
            for (let entry of trace) {
                entry.timestamp = new Date(entry.timestamp);
            }
            setCurrentTrace(trace);
        }).then(subId => { subIdTrace = subId; });
        return () => {
            app.serverEvents.removeEventListener<DataStoreStatus>(subIdStatus);
            app.serverEvents.removeEventListener<DataStoreInfo>(subIdInfo);
            app.serverEvents.removeEventListener<SystemTraceEntry[]>(subIdTrace);
        }
    }, [app.ui.selectedStoreId]);
    const truncateLog = () => {
        if (!storeId) return;
        const deleteOld = window.confirm("Do you want to delete old log entries? (OK = Yes, Cancel = No)");
        app.api.maintenance.truncateLog(storeId, deleteOld);
    }
    if (!storeId) return;
    if (!currentContainer) return;
    if (!info) return;
    const state = app.ui.getStoreState(app.ui.selectedStoreId);
    const showActivity = (activity: DataStoreActivityBranch, level: number = 0) => {
        return <>
            {activity.activity.description} <br />
            {activity.children && activity.children.map(child =>
                <span style={{ marginLeft: level * 20 }}>{showActivity(child, level + 1)}</span>
            )}
        </>;
    }
    const taskCount = info.queuedTasksPending;
    const taskCountPersisted = info.queuedTasksPendingPersisted;
    return (
        <>
            <Grid mt="md">
                <Panel>
                    <Group>
                        <Button variant="light" disabled={state != "Closed" } onClick={() => app.api.maintenance.open(app.ui.selectedStoreId!)}>Open</Button>
                        <Button variant="light" disabled={state != "Opening"} onClick={() => app.api.maintenance.cancelOpening(app.ui.selectedStoreId!)}>Cancel</Button>
                        <Button variant="light" disabled={state != "Open" && state != "Error"} onClick={() => app.api.maintenance.close(app.ui.selectedStoreId!)}>Close</Button>

                        <Button variant="light" disabled={state != "Open" || (info.logActionsNotItInStatefile == 0)} onClick={() => app.api.maintenance.saveIndexStates(app.ui.selectedStoreId!, true, false)}>Save state</Button>
                        <Button variant="light" disabled={state != "Open" || ((info.setCacheCount + info.nodeCacheCount) == 0)} onClick={() => app.api.maintenance.clearCache(app.ui.selectedStoreId!)}>Clear Cache</Button>
                        <Button variant="light" disabled={state != "Open" || (info.logTruncatableActions == 0)} onClick={truncateLog}>Truncate</Button>
                        <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.resetSecondaryLogFile(app.ui.selectedStoreId!)}>Reset second log</Button>
                        <Button variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.resetStateAndIndexes(app.ui.selectedStoreId!)}>Reset all states</Button>
                        <Button variant="light" disabled={state != "Closed"} onClick={() => app.api.maintenance.deleteStateAndIndexes(app.ui.selectedStoreId!)}>Delete all states</Button>
                    </Group>
                </Panel>
                <Panel title="Info" span={3}>
                    <div style={{ height: '250px', overflowY: 'auto' }}>
                        <div>State: {state}</div>
                        <div>Uptime: {formatTimeSpan(info.uptimeMs)}</div>
                        <div>Memory: {formatBytes(info.processWorkingMemory)}</div>
                        <div style={{ color: taskCount > 0 ? 'orange' : '' }}>Transient tasks: {formatNumber(taskCount)}</div>
                        <div style={{ color: taskCountPersisted > 0 ? 'orange' : '' }}>Persisted tasks: {formatNumber(taskCountPersisted)}</div>
                        <div>CPU: {info.cpuUsagePercentage.toFixed(1)}%</div>
                        <div>CPU (1 min avg): {info.cpuUsagePercentageLastMinute.toFixed(1)}%</div>
                        <div>Startup: {formatTimeSpan(info.startUpMs)}</div>
                    </div>
                </Panel>
                <Panel title="Activity" span={9}>
                    <div style={{ height: '250px', overflowY: 'auto' }}>
                        <Activity level={0} activities={currentStatus?.activityTree} />
                    </div>
                </Panel>
                <Panel title="Storage" span={3}>
                    <div style={{ height: '278px', overflowY: 'auto' }}>
                        {formatBytes(info.totalFileSize)} total file size<br />
                        {formatBytes(info.logFileSize)} bytes in main log file<br />
                        {formatBytes(info.secondaryLogFileSize)} bytes in secondary log<br />
                        {formatBytes(info.logStateFileSize)} bytes in state log file<br />
                        {formatBytes(info.indexFileSize)} bytes in indexes<br />
                        {formatBytes(info.fileStoreSize)} bytes in filestore<br />
                        {formatBytes(info.backupFileSize)} bytes in backups<br />
                        {formatBytes(info.loggingFileSize)} bytes in logging<br />
                        <span style={{ color: info.logActionsNotItInStatefile > 0 ? 'orange' : 'inherit' }}>
                            {formatNumber(info.logActionsNotItInStatefile)} actions not in state file<br />
                            {/* {formatNumber(currentInfo.logTransactionsNotItInStatefile)} transactions not in state file<br /> */}
                        </span>
                        <span style={{ color: info.logTruncatableActions > 0 ? 'orange' : 'inherit' }}>
                            {formatNumber(info.logTruncatableActions)} truncatable actions<br />
                        </span>
                        <span style={{ color: info.logWritesQueuedActions > 0 ? 'orange' : 'inherit' }}>
                            {formatNumber(info.logWritesQueuedActions)} unflushed actions<br />
                            {/* {formatNumber(currentInfo.logWritesQueuedTransactions)} queued transaction writes<br /> */}
                        </span>
                    </div>
                </Panel>
                <Panel title="Trace" span={6}>
                    <pre style={{ maxHeight: '250px', overflow: 'auto', backgroundColor: '#222', padding: '10px', fontSize: '14px' }}>
                        {currentTrace?.map(trace =>
                            <>
                                {formatTimeOfDay(trace.timestamp)}&nbsp;
                                <span key={trace.timestamp.toISOString()} style={(() => {
                                    let color = "lightgray";
                                    if (trace.type === "Error") color = 'red';
                                    else if (trace.type === "Warning") color = 'orange';
                                    else if (trace.type === "Info") color = 'lightblue';
                                    return { color };
                                })()}>[{trace.type}]
                                </span>&nbsp;{`${trace.text}\n`}
                            </>
                        )}
                    </pre>
                </Panel>
                <Panel title="Cache" span={3}>
                    {formatBytes(info.setCacheSize)} set cache size<br />
                    {info.setCacheSizePercentage.toFixed(1)}% set cache used<br />
                    {formatNumber(info.setCacheCount)} sets cached<br />
                    {formatNumber(info.setCacheHits)} set cache hits<br />
                    {formatNumber(info.setCacheMisses)} set cache misses<br />
                    {formatNumber(info.setCacheOverflows)} set cache overflows<br />
                    <br />
                    {formatBytes(info.nodeCacheSize)} node cache size<br />
                    {info.nodeCacheSizePercentage.toFixed(1)}% node cache used<br />
                    {formatNumber(info.nodeCacheCount)} nodes cached<br />
                    {formatNumber(info.nodeCacheHits)} node cache hits<br />
                    {formatNumber(info.nodeCacheMisses)} node cache misses<br />
                    {formatNumber(info.nodeCacheOverflows)} node cache overflows<br />
                    <Button mt="sm" variant="light" disabled={state != "Open"} onClick={() => app.api.maintenance.clearCache(app.ui.selectedStoreId!)}>Clear Cache</Button>
                </Panel>
                <Panel title="Content" span={3}>
                    {Object.entries(info.typeCounts).map(([type, count]) => (
                        <div key={type}>{type}: {count}</div>
                    ))}

                </Panel>
                <Panel title="Queries per second" span={3}>
                    <SimplePlot logKey="query" color="green.3" />
                </Panel>
                <Panel title="Transactions per second" span={3}>
                    <SimplePlot logKey="transaction" color="blue.3" />
                </Panel>
                <Panel title="Actions per second" span={3}>
                    <SimplePlot logKey="action" color="red.3" />
                </Panel>
            </Grid>
        </>
    )
}
const Status = observer(component);
export default Status;

