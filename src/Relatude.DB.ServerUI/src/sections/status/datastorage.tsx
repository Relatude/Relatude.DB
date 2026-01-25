import { Button, Group } from "@mantine/core";
import { formatBytes, formatNumber } from "../../application/common";
import { DataStoreInfo, StoreStates } from "../../application/models";
import { useApp } from "../../start/useApp";
import { useEffect, useState } from "react";

const EmptyGuid = "00000000-0000-0000-0000-000000000000";
export const DataStorage = (P: { info: DataStoreInfo, storestate: StoreStates }) => {
    const app = useApp();
    const [hasSecLog, setHasSecLog] = useState<boolean>(false);
    const storeId = app.ui.selectedStoreId;
    if (!storeId) return;
    const truncateLog = () => {
        if (!storeId) return;
        const deleteOld = window.confirm("Do you want to delete old log entries? (OK = Yes, Cancel = No)");
        app.api.maintenance.truncateLog(storeId, deleteOld);
    }
    useEffect(() => {
        const checkSecLog = async () => {
            const settings = await app.api.settings.getSettings(storeId, false);
            const hasSec = settings.localSettings.secondaryBackupLog;
            setHasSecLog(hasSec);
        }
        checkSecLog();
    }, [storeId]);

    return (<>
        <div style={{ height: '278px', overflowY: 'auto' }}>
            {formatBytes(P.info.totalFileSize)} total file size<br />
            {formatBytes(P.info.logFileSize)} bytes in main log file<br />
            {hasSecLog ? <>{formatBytes(P.info.secondaryLogFileSize)} bytes in secondary log<br /></> : null}
            {formatBytes(P.info.logStateFileSize)} bytes in state log file<br />
            {formatBytes(P.info.indexFileSize)} bytes in indexes<br />
            {formatBytes(P.info.fileStoreSize)} bytes in filestore<br />
            {formatBytes(P.info.backupFileSize)} bytes in backups<br />
            {formatBytes(P.info.loggingFileSize)} bytes in logging<br />
            <span style={{ color: P.info.logActionsNotItInStatefile > 0 ? 'orange' : 'inherit' }}>
                {formatNumber(P.info.logActionsNotItInStatefile)} actions not in state file<br />
                {/* {formatNumber(currentInfo.logTransactionsNotItInStatefile)} transactions not in state file<br /> */}
            </span>
            <span style={{ color: P.info.noIndexesOutOfSync > 0 ? 'orange' : 'inherit' }}>
                {formatNumber(P.info.noIndexesOutOfSync)} out-of-sync indexes<br />
            </span>
            <span style={{ color: P.info.logTruncatableActions > 0 ? 'orange' : 'inherit' }}>
                {formatNumber(P.info.logTruncatableActions)} truncatable actions<br />
            </span>
            <span style={{ color: P.info.logWritesQueuedActions > 0 ? 'orange' : 'inherit' }}>
                {formatNumber(P.info.logWritesQueuedActions)} unflushed actions<br />
                {/* {formatNumber(currentInfo.logWritesQueuedTransactions)} queued transaction writes<br /> */}
            </span>
        </div>
        <Group>
            <Button variant="light" disabled={P.storestate != "Open" || (P.info.logActionsNotItInStatefile == 0 && P.info.noIndexesOutOfSync == 0)} onClick={() => app.api.maintenance.saveIndexStates(storeId, true, false)}>Save state</Button>
            {hasSecLog && <Button variant="light" disabled={P.storestate != "Open"} onClick={() => app.api.maintenance.resetSecondaryLogFile(storeId)}>Reset second log</Button>}
            <Button variant="light" disabled={P.storestate != "Open"} onClick={() => app.api.maintenance.resetStateAndIndexes(storeId)}>Reset all states</Button>
            <Button variant="light" disabled={P.storestate != "Closed"} onClick={() => app.api.maintenance.deleteStateAndIndexes(storeId)}>Delete all states</Button>
            <Button variant="light" disabled={P.storestate != "Open"} onClick={() => app.api.maintenance.backUpNow(storeId, EmptyGuid, true, true)}>Backup now</Button>
            {P.info.runningRewriteFile ?
                <Button variant="light" color="red" title={P.info.runningRewriteFile} onClick={() => app.api.maintenance.cancelRewriteIfAny(app.ui.selectedStoreId!)}>Cancel rewrite</Button>
                :
                <Button variant="light" disabled={P.storestate != "Open" || (P.info.logTruncatableActions == 0)} onClick={truncateLog}>Truncate</Button>}
        </Group>
    </>
    );
}