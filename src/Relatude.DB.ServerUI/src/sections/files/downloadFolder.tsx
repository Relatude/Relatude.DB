import { useEffect, useRef, useState } from 'react';
import { Button, Group, Modal, Progress, Text } from '@mantine/core';
import { useApp } from '../../start/useApp';
import { formatBytes } from '../../application/common';
import { FileMeta, FolderMeta } from '../../application/models';

// Streams every file of a storage folder into a local directory the user picked with the
// File System Access API (the picker itself is opened by the caller, inside the click gesture).
// Locked or failing files are skipped and listed as warnings at the end; the run can be cancelled.
type Phase = "listing" | "downloading" | "done" | "cancelled" | "failed";
export const DownloadFolder = (p: { storeId: string, ioId: string, folderPath: string, dirHandle: FileSystemDirectoryHandle, onClose: () => void }) => {
    const app = useApp();
    const [phase, setPhase] = useState<Phase>("listing");
    const [currentFile, setCurrentFile] = useState("");
    const [fileNo, setFileNo] = useState(0);
    const [fileCount, setFileCount] = useState(0);
    const [filesDownloaded, setFilesDownloaded] = useState(0);
    const [bytesDone, setBytesDone] = useState(0);
    const [bytesTotal, setBytesTotal] = useState(0);
    const [warnings, setWarnings] = useState<string[]>([]);
    const [error, setError] = useState("");
    const cancelled = useRef(false);
    const aborter = useRef(new AbortController());
    useEffect(() => { download(); }, []);
    const cancel = () => { cancelled.current = true; aborter.current.abort(); };
    const close = () => { cancel(); p.onClose(); };
    const download = async () => {
        const warningList: string[] = [];
        try {
            const folder = await app.api.maintenance.getFolderRecursive(p.storeId, p.ioId, p.folderPath);
            // file keys are storage root qualified; local paths are relative to the downloaded
            // folder's parent, so the folder itself is created inside the picked directory
            const rootName = p.folderPath.split('/').pop()!;
            const parentPrefixLength = p.folderPath.length - rootName.length;
            const allFiles: FileMeta[] = [];
            const allFolders: string[] = [];
            const walk = (f: FolderMeta, relative: string) => {
                allFolders.push(relative);
                for (const file of f.files ?? []) allFiles.push(file);
                for (const sub of f.subFolders ?? []) walk(sub, relative + '/' + sub.name);
            };
            walk(folder, rootName);
            setFileCount(allFiles.length);
            setBytesTotal(allFiles.reduce((acc, f) => acc + f.size, 0));
            const dirs = new Map<string, FileSystemDirectoryHandle>();
            dirs.set("", p.dirHandle);
            const getDir = async (relFolder: string): Promise<FileSystemDirectoryHandle> => {
                const existing = dirs.get(relFolder);
                if (existing) return existing;
                const parent = await getDir(relFolder.substring(0, Math.max(relFolder.lastIndexOf('/'), 0)));
                const handle = await parent.getDirectoryHandle(relFolder.split('/').pop()!, { create: true });
                dirs.set(relFolder, handle);
                return handle;
            };
            for (const rel of allFolders) await getDir(rel); // empty folders are recreated too
            setPhase("downloading");
            let done = 0, downloaded = 0, bytes = 0;
            for (const file of allFiles) {
                if (cancelled.current) break;
                done++;
                setFileNo(done);
                setCurrentFile(file.key);
                const relative = file.key.substring(parentPrefixLength);
                let fileBytes = 0;
                let writable: FileSystemWritableFileStream | null = null;
                try {
                    if (file.writers > 0) throw new Error("locked by another writer");
                    const res = await app.api.maintenance.downloadFileResponse(p.storeId, p.ioId, file.key, aborter.current.signal);
                    if (res.status === 423) throw new Error("locked by another process");
                    if (!res.ok) throw new Error("the server responded with status " + res.status);
                    const dir = await getDir(relative.substring(0, relative.lastIndexOf('/')));
                    const fileHandle = await dir.getFileHandle(relative.split('/').pop()!, { create: true });
                    writable = await fileHandle.createWritable();
                    const reader = res.body!.getReader();
                    while (true) {
                        const { done: eof, value } = await reader.read();
                        if (eof) break;
                        await writable.write(value);
                        fileBytes += value.byteLength;
                        bytes += value.byteLength;
                        setBytesDone(bytes);
                    }
                    await writable.close();
                    writable = null;
                    downloaded++;
                    setFilesDownloaded(downloaded);
                } catch (e: any) {
                    if (writable) { try { await writable.abort(); } catch { } } // discards the partial write
                    if (cancelled.current) break;
                    warningList.push(file.key + " - " + (e?.message ?? "download failed"));
                }
                bytes += file.size - fileBytes; // skipped or failed files still advance the progress bar
                setBytesDone(bytes);
            }
            setWarnings(warningList);
            setPhase(cancelled.current ? "cancelled" : "done");
        } catch (e: any) {
            if (cancelled.current) { setWarnings(warningList); setPhase("cancelled"); return; }
            setError(e?.message ?? "Download failed");
            setWarnings(warningList);
            setPhase("failed");
        }
    };
    const running = phase === "listing" || phase === "downloading";
    const warningBlock = warnings.length > 0 && <>
        <Text size="sm" c="orange" mt="md">{warnings.length == 1 ? "1 file" : warnings.length + " files"} could not be downloaded:</Text>
        <div style={{ maxHeight: 200, overflowY: 'auto' }}>
            {warnings.map((w, i) => <Text key={i} size="xs" c="orange">{w}</Text>)}
        </div>
    </>;
    return (
        <Modal opened onClose={close} title={"Download folder \"" + p.folderPath + "\""} closeOnClickOutside={false} size="lg">
            {running ? <>
                <Text size="sm">{phase === "listing" ? "Listing files..." : "Downloading " + currentFile + " (file " + fileNo + " of " + fileCount + ")"}</Text>
                <Progress value={bytesTotal > 0 ? bytesDone * 100 / bytesTotal : 0} animated mt="sm" />
                <Text size="xs" mt="xs">{formatBytes(bytesDone)} of {formatBytes(bytesTotal)}</Text>
                <Group justify="flex-end" mt="md">
                    <Button variant="outline" onClick={cancel}>Cancel</Button>
                </Group>
            </> : <>
                <Text size="sm" c={phase === "failed" ? "red" : undefined}>
                    {phase === "done" && "Downloaded " + filesDownloaded + " of " + fileCount + " files (" + formatBytes(bytesDone) + ")."}
                    {phase === "cancelled" && "The download was cancelled after " + filesDownloaded + " of " + fileCount + " files."}
                    {phase === "failed" && "Failed: " + error}
                </Text>
                {warningBlock}
                <Group justify="flex-end" mt="md">
                    <Button variant="outline" onClick={p.onClose}>Close</Button>
                </Group>
            </>}
        </Modal>
    );
};
export default DownloadFolder;
