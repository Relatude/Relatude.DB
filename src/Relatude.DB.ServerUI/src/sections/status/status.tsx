import { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Button, Card, Group, Text, Grid, Flex } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { DataStoreStatus, DataStoreInfo, DataStoreActivityBranch, SystemTraceEntry } from '../../application/models';
import { formatBytes, formatNumber } from '../../application/common';
import { SimplePlot } from './simplePlot';
import { Activity } from './activity';
import { Info } from './info';
import { DataStorage } from './datastorage';
import { Trace } from './trace';


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
    return (
        <>
            <Grid >
                <Panel title="Info" span={3}>
                    <Info info={info} storestate={state} />
                </Panel>
                <Panel title="Activity" span={9}>
                    <div style={{ height: '250px', overflowY: 'auto' }}>
                        <Activity level={0} activities={currentStatus?.activityTree} />
                    </div>
                </Panel>
                <Panel title="Storage" span={3}>
                    <DataStorage info={info} storestate={state} />
                </Panel>
                <Panel title="Trace" span={6}>
                    <Trace currentTrace={currentTrace} />
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

