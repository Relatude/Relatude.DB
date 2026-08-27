import { useRef, useState } from 'react';
import { Button, Group, Modal, Progress, Table, Text } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { formatBytes, formatNumber, sleep } from '../../application/common';
import { MissingFilesProgress } from '../../application/models';

// Checks every file value in the database against the file store it points at and lists the ones
// whose file is not there. The scan runs as a server side job: only nodes of types that can carry a
// file value are loaded, in batches, and the modal polls its progress and can cancel it.
export const MissingFiles = (p: { storeId: string, disabled?: boolean }) => {
    const app = useApp();
    const [opened, setOpened] = useState(false);
    const [progress, setProgress] = useState<MissingFilesProgress | null>(null);
    const jobId = useRef<string | null>(null);
    const run = async () => {
        setProgress(null);
        setOpened(true);
        try {
            const id = await app.api.maintenance.findMissingFilesStart(p.storeId);
            jobId.current = id;
            while (true) {
                const status = await app.api.maintenance.findMissingFilesProgress(p.storeId, id);
                setProgress(status);
                if (status.state !== "running") break;
                await sleep(300);
            }
        } catch (e: any) {
            setProgress({
                state: "failed", description: "", percent: 0, error: e.message,
                nodesScanned: 0, filesChecked: 0, missingCount: 0, missingBytes: 0, listTruncated: false, missing: [],
            });
        } finally {
            jobId.current = null;
        }
    };
    const cancel = () => { if (jobId.current) app.api.maintenance.findMissingFilesCancel(p.storeId, jobId.current); };
    const close = () => { cancel(); setOpened(false); };
    const copyList = () => {
        const rows = progress!.missing.map(m => [m.nodeType, m.property, m.fileName, m.size, m.nodeId, m.fileId, m.reason].join('\t'));
        navigator.clipboard.writeText(["Type\tProperty\tFile\tSize\tNode\tFileId\tReason", ...rows].join('\n'));
    };
    const running = progress == null || progress.state === "running";
    const resultRow = (label: string, value: string) => (
        <Table.Tr><Table.Td>{label}</Table.Td><Table.Td style={{ textAlign: 'right' }}>{value}</Table.Td></Table.Tr>
    );
    return <>
        <Button variant="light" disabled={p.disabled} onClick={run}
            title="Check every file value in the database against its file store and list the files that are missing. ">Check for missing files</Button>
        <Modal opened={opened} onClose={close} title="Missing files" closeOnClickOutside={false} size={running || progress!.missingCount == 0 ? "md" : "80%"}>
            {running ? <>
                <Text size="sm">{progress?.description || "Starting..."}</Text>
                <Progress value={progress?.percent ?? 0} animated mt="sm" />
                <Text size="xs" mt="xs">
                    {formatNumber(progress?.nodesScanned ?? 0)} nodes scanned, {formatNumber(progress?.filesChecked ?? 0)} files checked
                    {progress && progress.missingCount > 0 ? ", " + formatNumber(progress.missingCount) + " missing" : ""}
                </Text>
                <Group justify="flex-end" mt="md">
                    <Button variant="outline" onClick={cancel}>Cancel</Button>
                </Group>
            </> : progress!.state === "done" ? <>
                <Table>
                    <Table.Tbody>
                        {resultRow("Nodes scanned", formatNumber(progress!.nodesScanned))}
                        {resultRow("Files checked", formatNumber(progress!.filesChecked))}
                        {resultRow("Missing files", formatNumber(progress!.missingCount))}
                        {resultRow("Size of missing files", formatBytes(progress!.missingBytes))}
                    </Table.Tbody>
                </Table>
                {progress!.missingCount == 0
                    ? <Text size="sm" mt="md" c="green">Every file value has its file in the file store.</Text>
                    : <>
                        <Text size="sm" mt="md" c="orange">
                            {progress!.listTruncated
                                ? "The first " + formatNumber(progress!.missing.length) + " missing files:"
                                : "Missing files:"}
                        </Text>
                        <div style={{ maxHeight: 400, overflowY: 'auto' }}>
                            <Table striped highlightOnHover>
                                <Table.Thead>
                                    <Table.Tr>
                                        <Table.Th>Type</Table.Th>
                                        <Table.Th>Property</Table.Th>
                                        <Table.Th>File</Table.Th>
                                        <Table.Th>Size</Table.Th>
                                        <Table.Th>Node</Table.Th>
                                        <Table.Th>Reason</Table.Th>
                                    </Table.Tr>
                                </Table.Thead>
                                <Table.Tbody>
                                    {progress!.missing.map((m, i) => (
                                        <Table.Tr key={i}>
                                            <Table.Td>{m.nodeType.split('.').pop()}</Table.Td>
                                            <Table.Td>{m.property}</Table.Td>
                                            <Table.Td title={"File id: " + m.fileId}>{m.fileName}</Table.Td>
                                            <Table.Td style={{ whiteSpace: 'nowrap' }}>{formatBytes(m.size)}</Table.Td>
                                            <Table.Td style={{ fontSize: '0.78em', opacity: 0.75 }}>{m.nodeId}</Table.Td>
                                            <Table.Td style={{ fontSize: '0.78em' }}>{m.reason}</Table.Td>
                                        </Table.Tr>
                                    ))}
                                </Table.Tbody>
                            </Table>
                        </div>
                    </>}
                <Group justify="flex-end" mt="md">
                    {progress!.missing.length > 0 ? <Button variant="light" onClick={copyList}>Copy list</Button> : null}
                    <Button variant="outline" onClick={() => setOpened(false)}>Close</Button>
                </Group>
            </> : <>
                <Text size="sm" c={progress!.state === "failed" ? "red" : undefined}>
                    {progress!.state === "failed"
                        ? "Failed: " + (progress!.error ?? "Unknown error")
                        : "The check was cancelled after " + formatNumber(progress!.filesChecked) + " files."}
                </Text>
                <Group justify="flex-end" mt="md">
                    <Button variant="outline" onClick={() => setOpened(false)}>Close</Button>
                </Group>
            </>}
        </Modal>
    </>;
};
export default MissingFiles;
