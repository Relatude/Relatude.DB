import { Button, Group } from "@mantine/core";
import { formatBytes, formatNumber, formatTimeSpan, formatTimeSpanShort } from "../../application/common";
import { DataStoreInfo, StoreStates } from "../../application/models";
import { useApp } from "../../start/useApp";

export const Info = (P: { info: DataStoreInfo, storestate: StoreStates }) => {
    const taskCount = (P.info.queuedTaskStateCounts.Pending ?? 0) + (P.info.queuedTaskStateCounts.Running ?? 0);
    const taskCountPersisted = (P.info.queuedTaskStateCountsPersisted.Pending ?? 0) + (P.info.queuedTaskStateCountsPersisted.Running ?? 0);
    const app = useApp();
    const emptyIn = (ms: number) => {
        if (ms === undefined || ms === null || ms <= 0 || ms > 1000*60*60*50 ) return ""; // more than 50 hour
        return " (" + formatTimeSpanShort(ms) + ")";
    }
    return (
        <div style={{ height: '250px', overflowY: 'auto' }}>
            <Group>
                <Button variant="light" disabled={P.storestate != "Closed"} onClick={() => app.api.maintenance.open(app.ui.selectedStoreId!)}>Open</Button>
                <Button variant="light" disabled={P.storestate != "Opening"} onClick={() => app.api.maintenance.cancelOpening(app.ui.selectedStoreId!)}>Cancel</Button>
                <Button variant="light" disabled={P.storestate != "Open" && P.storestate != "Error"} onClick={() => app.api.maintenance.close(app.ui.selectedStoreId!)}>Close</Button>
            </Group>
            <div>State: {P.storestate}</div>
            <div>Uptime: {formatTimeSpan(P.info.uptimeMs)}</div>
            <div>Memory: {formatBytes(P.info.processWorkingMemory)}</div>
            <div style={{ color: taskCount > 0 ? 'orange' : '' }}>Transient tasks: {formatNumber(taskCount) + emptyIn(P.info.queuedTaskEstimatedMsUntilEmpty)}</div>
            <div style={{ color: taskCountPersisted > 0 ? 'orange' : '' }}>Persisted tasks: {formatNumber(taskCountPersisted) + emptyIn(P.info.queuedTaskEstimatedMsUntilEmptyPersisted)}</div>
            <div>CPU: {P.info.cpuUsagePercentage.toFixed(1)}%</div>
            <div>CPU (1 min avg): {P.info.cpuUsagePercentageLastMinute.toFixed(1)}%</div>
            <div>Startup: {formatTimeSpan(P.info.startUpMs)}</div>
        </div>
    )
};