import { useRef, useState } from 'react';
import { Button, Group, Modal, Progress, Table, Text } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { formatBytes, formatNumber, sleep } from '../../application/common';
import { UnreferencedFilesProgress } from '../../application/models';

// Count or delete files in the store's file stores that no node references anymore.
// Both actions run as a server side job: the modal polls its progress and can cancel it.
export const UnreferencedFiles = (p: { storeId: string, disabled?: boolean }) => {
    const app = useApp();
    const [opened, setOpened] = useState(false);
    const [countOnly, setCountOnly] = useState(true);
    const [progress, setProgress] = useState<UnreferencedFilesProgress | null>(null);
    const jobId = useRef<string | null>(null);
    const run = async (countOnlyRun: boolean) => {
        if (!countOnlyRun && !window.confirm("Delete all unreferenced files from the file stores? This cannot be undone.")) return;
        setCountOnly(countOnlyRun);
        setProgress(null);
        setOpened(true);
        try {
            const id = await app.api.maintenance.deleteUnreferencedFilesStart(p.storeId, countOnlyRun);
            jobId.current = id;
            while (true) {
                const status = await app.api.maintenance.deleteUnreferencedFilesProgress(p.storeId, id);
                setProgress(status);
                if (status.state !== "running") break;
                await sleep(300);
            }
        } catch (e: any) {
            setProgress({ state: "failed", description: "", percent: 0, error: e.message, totalBytesDeleted: 0, totalFilesDeleted: 0, totalFoldersDeleted: 0 });
        } finally {
            jobId.current = null;
        }
    };
    const cancel = () => { if (jobId.current) app.api.maintenance.deleteUnreferencedFilesCancel(p.storeId, jobId.current); };
    const close = () => { cancel(); setOpened(false); };
    const running = progress == null || progress.state === "running";
    const resultRow = (label: string, value: string) => (
        <Table.Tr><Table.Td>{label}</Table.Td><Table.Td style={{ textAlign: 'right' }}>{value}</Table.Td></Table.Tr>
    );
    return <>
        <Group>
            <Button variant="light" disabled={p.disabled} onClick={() => run(true)}>Count unreferenced files</Button>
            <Button variant="light" color="red" disabled={p.disabled} onClick={() => run(false)}>Delete unreferenced files</Button>
        </Group>
        <Modal opened={opened} onClose={close} title={countOnly ? "Count unreferenced files" : "Delete unreferenced files"} closeOnClickOutside={false}>
            {running ? <>
                <Text size="sm">{progress?.description || "Starting..."}</Text>
                <Progress value={progress?.percent ?? 0} animated mt="sm" />
                <Group justify="flex-end" mt="md">
                    <Button variant="outline" onClick={cancel}>Cancel</Button>
                </Group>
            </> : progress!.state === "done" ? <>
                <Table>
                    <Table.Tbody>
                        {resultRow(countOnly ? "Unreferenced files" : "Files deleted", formatNumber(progress!.totalFilesDeleted))}
                        {resultRow(countOnly ? "Folders left empty" : "Folders deleted", formatNumber(progress!.totalFoldersDeleted))}
                        {resultRow(countOnly ? "Total size" : "Bytes freed", formatBytes(progress!.totalBytesDeleted))}
                    </Table.Tbody>
                </Table>
                <Group justify="flex-end" mt="md">
                    {countOnly && progress!.totalFilesDeleted > 0 ? <Button color="red" onClick={() => run(false)}>Delete these files</Button> : null}
                    <Button variant="outline" onClick={() => setOpened(false)}>Close</Button>
                </Group>
            </> : <>
                <Text size="sm" c={progress!.state === "failed" ? "red" : undefined}>
                    {progress!.state === "failed" ? "Failed: " + (progress!.error ?? "Unknown error") : "The operation was cancelled."}
                </Text>
                <Group justify="flex-end" mt="md">
                    <Button variant="outline" onClick={() => setOpened(false)}>Close</Button>
                </Group>
            </>}
        </Modal>
    </>;
};
export default UnreferencedFiles;
