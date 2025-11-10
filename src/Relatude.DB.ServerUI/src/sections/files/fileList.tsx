import React, { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Table, Button, Group, Menu, MenuItem, Divider, ActionIcon, Checkbox } from '@mantine/core';
import { useApp } from '../../start/useApp';
import IoSelector from '../../components/ioSelector';
import { FileMeta, NodeStoreContainer } from '../../application/models';
import Upload, { UploadedFile } from './upload';
import { IconDots } from '@tabler/icons-react';
import { formatBytesString, formatDateToString } from '../../utils/formatting';

export const component = (p: { storeId: string }) => {
    const app = useApp();
    const [store, setStore] = useState<NodeStoreContainer>();
    const [files, setFiles] = useState<FileMeta[]>();
    const [canRename, setCanRename] = useState<boolean>(false);
    const [selectedIo, setSelectedIo] = useState<string>();
    const [dbFile, setDbFile] = useState<string>("");
    const [selectedRows, setSelectedRows] = useState<string[]>([]);
    useEffect(() => { updateSettings(); }, [p.storeId]);
    useEffect(() => { updateFiles(); }, [selectedIo]);
    const updateSettings = async () => {
        if (p.storeId) {
            const store = await app.api.settings.getSettings(p.storeId);
            setStore(store);
            setSelectedIo(store.ioSettings[0]?.id);
        } else {
            setStore(undefined);
            setSelectedIo(undefined);
        }
    }
    const updateFiles = async () => {
        const storeIsLoadedAndIoBelongsToStore = store?.id == p.storeId && store?.ioSettings?.find(io => io.id == selectedIo) != undefined;
        setFiles(storeIsLoadedAndIoBelongsToStore ? await app.api.maintenance.getStoreFiles(p.storeId, selectedIo!) : undefined);
        setCanRename(storeIsLoadedAndIoBelongsToStore ? await app.api.maintenance.canRenameFile(p.storeId, selectedIo!) : false);
        const storeSettings = await app.api.settings.getSettings(p.storeId);
        if (selectedIo) {
            const dbFile = await app.api.maintenance.getFileKeyOfDb(p.storeId, selectedIo, storeSettings.localSettings.filePrefix);
            setDbFile(dbFile);
        } else {
            setDbFile("");
        }
    }
    const getStatus = (file: FileMeta) => {
        if (file.writers > 0) return "Write locked";
        if (file.readers > 0) return "Read locked";
        return "-";
    }
    const downloadFile = async (fileName: string) => {
        try {
            await app.api.maintenance.downloadFile(p.storeId, selectedIo!, fileName);
        } catch (e: any) {
            alert(e.message);
        }
    }
    const closeAllOpenStreams = async () => {
        if (!confirm("Are you sure you want to reset all locks?")) return;
        await app.api.maintenance.closeAllOpenStreams(p.storeId, selectedIo!);
        await updateFiles();
    }
    const renameFile = async (fileName: string) => {
        let oldName = fileName;
        let newName = prompt("Please enter a new name:", oldName);
        while (!(await app.api.maintenance.isFileKeyLegal(newName))) {
            if (!newName) return;
            newName = prompt("Sorry, not a valid filename, try again:", newName);
        }
        if (!newName) return;
        await app.api.maintenance.renameFile(p.storeId, selectedIo!, oldName, newName);
        await updateFiles();
    }
    const copyFile = async (fileName: string) => {
        let oldName = fileName;
        let newName = prompt("Please enter a new name:", oldName);
        while (!(await app.api.maintenance.isFileKeyLegal(newName))) {
            if (!newName) return;
            newName = prompt("Sorry, not a valid filename, try again:", newName);
        }
        if (!newName) return;
        await app.api.maintenance.copyFile(p.storeId, selectedIo!, oldName, selectedIo!, newName);
        await updateFiles();
    }
    const onFileError = (message: string) => {
        alert(message);
        updateFiles();
    }
    const deleteAllButDb = async () => {
        const selectedIOIsDatabase = selectedIo ? app.ui.isIoUsedForCurrentDatabase(selectedIo) : false;
        if (selectedIOIsDatabase) {
            if (!confirm("Are you sure you want to delete all files except the database?")) return;
            await app.api.maintenance.deleteAllButDb(p.storeId);
        } else {
            if (!confirm("Are you sure you want to delete all files?")) return;
            await app.api.maintenance.deleteAllFiles(p.storeId, selectedIo!);
        }
        await updateFiles();
    }
    const deleteSelectedFiles = async () => {
        if (!confirm("Are you sure you want to permanently delete selected files?")) return;
        const storeSettings = await app.api.settings.getSettings(p.storeId);
        try {
            for (const file of selectedRows) {
                if (!confirmInCaseOfDbFile(file)) return;
                await app.api.maintenance.deleteFile(p.storeId, selectedIo!, file);
            }
            setSelectedRows([]);
        } finally {
            await updateFiles();
        }
    }
    const deleteFile = async (file: string) => {
        if (!confirm("Are you sure you want to permanently delete this file?")) return;
        const storeSettings = await app.api.settings.getSettings(p.storeId);
        try {
            if (confirmInCaseOfDbFile(file)) await app.api.maintenance.deleteFile(p.storeId, selectedIo!, file);
        } finally {
            await updateFiles();
        }
    }
    const confirmInCaseOfDbFile = (file: string) => {
        const isDbFile = dbFile === file;
        if (!isDbFile) return true;
        if (!confirm("Are you ABSOLUTELY sure you want to PERMANENTLY delete the CURRENT database file?")) return false
        if (!confirm("ABSOLUTELY sure? LAST chance...")) return false;
        return true;
    }
    const onCompleteUpload = async (uploads: UploadedFile[]) => {
        for (const upload of uploads) {
            await app.api.maintenance.completeUpload(p.storeId, selectedIo!, upload.uploadId, upload.name, false);
        }
        updateFiles();
    }
    const onCompleteDatabaseUpload = async (uploads: UploadedFile[]) => {
        for (const upload of uploads) {
            const dbName = await app.api.maintenance.getFileKeyOfNextDb(p.storeId, selectedIo!, store?.localSettings.filePrefix);
            await app.api.maintenance.completeUpload(p.storeId, selectedIo!, upload.uploadId, dbName, true);
            await restartIfOpen();
        }
        await updateFiles();
    }
    const useFileAsNewDB = async (fileName: string) => {
        const newName = await app.api.maintenance.getFileKeyOfNextDb(p.storeId, selectedIo!, store?.localSettings.filePrefix);
        await app.api.maintenance.copyFile(p.storeId, selectedIo!, fileName, selectedIo!, newName);
        await restartIfOpen();
        await updateFiles();
    }
    const restartIfOpen = async () => {
        if (app.ui.isStoreOpen(p.storeId)) {
            await app.api.maintenance.close(p.storeId);
            await app.api.maintenance.open(p.storeId);
        }
    }
    const totalSize = files?.reduce((acc, file) => acc + file.size, 0) ?? 0;
    const selectedIOIsDatabase = selectedIo ? app.ui.isIoUsedForCurrentDatabase(selectedIo) : false;
    const domainName = location.hostname;
    return (<>
        <IoSelector ioSettings={store?.ioSettings} selectedIo={selectedIo} onChange={(id) => setSelectedIo(id)} />
        {selectedIo && <Upload text="Upload file" storeId={p.storeId} ioId={selectedIo} onComplete={onCompleteUpload} onError={onFileError} onCancel={updateFiles} multiple />}
        {selectedIOIsDatabase && <Upload text="Upload database"
            title="Uploaded file will be renamed correctly and opened. Existing database will remain in the filesystem. "
            storeId={p.storeId} ioId={selectedIo!} onComplete={onCompleteDatabaseUpload} onError={onFileError} onCancel={updateFiles} ignoreSameName />}
        {selectedIOIsDatabase && <Button variant="light" onClick={() => app.api.maintenance.downloadTruncatedDb(p.storeId)}
            title='A copy of current data, without transaction history. This will not block execution. '
        >Download database</Button>}
        {selectedIOIsDatabase && <Button variant="light" onClick={() => app.api.maintenance.downloadFullDb(p.storeId)}
            title='A copy of the current database file with transaction history. This will temporarily block execution. '
        >Download with history</Button>}
        {/* {selectedIOIsDatabase && <Button variant="outline" onClick={() => app.api.maintenance.closeAllOpenStreams(p.storeId, selectedIo!)}
            title='Force close all open read and write streams to files in this IO. '
        >Force close all</Button>} */}
        {(selectedIo && selectedRows.length > 0) && <Button variant="light" color='red' onClick={deleteSelectedFiles}
            title='Permanently delete selected files. '
        >Delete {selectedRows.length == 1 ? " 1 file" : (selectedRows.length + " files")}</Button>}
        <Table>
            <Table.Thead>
                <Table.Tr>
                    <Table.Th><Checkbox
                        checked={selectedRows.length == files?.length}
                        indeterminate={selectedRows.length > 0 && selectedRows.length < files!.length}
                        onChange={(e) => setSelectedRows(e.currentTarget.checked ? files!.map(f => f.key) : [])}
                    /></Table.Th>
                    <Table.Th>Name</Table.Th>
                    <Table.Th>Size</Table.Th>
                    <Table.Th>Status</Table.Th>
                    <Table.Th>Type</Table.Th>
                    <Table.Th>Created</Table.Th>
                    <Table.Th>Modified</Table.Th>
                    <Table.Th></Table.Th>
                </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
                {files?.map((file) => (
                    <Table.Tr key={file.key}
                        bg={selectedRows.includes(file.key) ? 'var(--mantine-color-blue-light)' : undefined}
                    >
                        <Table.Td><Checkbox
                            checked={selectedRows.includes(file.key)}
                            onChange={(e) => setSelectedRows(e.currentTarget.checked ? [...selectedRows, file.key] : selectedRows.filter((k) => k !== file.key))}
                        /></Table.Td>
                        <Table.Td >
                            {file.key == dbFile ? <b title="Current database file" style={{ color: "" }}>{file.key}</b> : file.key}
                        </Table.Td>
                        <Table.Td>{formatBytesString(file.size)}</Table.Td>
                        <Table.Td>{getStatus(file)}</Table.Td>
                        <Table.Td>{file.description}</Table.Td>
                        <Table.Td>{formatDateToString(file.lastModifiedUtc)}</Table.Td>
                        <Table.Td>{formatDateToString(file.creationTimeUtc)}</Table.Td>
                        <Table.Td>
                            <Group gap={"sm"}>
                                <Menu width={200} >
                                    <Menu.Target>
                                        <ActionIcon variant="transparent" ><IconDots></IconDots></ActionIcon>
                                    </Menu.Target>
                                    <Menu.Dropdown>
                                        <Menu.Item onClick={() => downloadFile(domainName + "." + file.key)} disabled={file.writers > 0}  >Download</Menu.Item>
                                        {canRename && <Menu.Item onClick={() => renameFile(file.key)} disabled={file.writers > 0 || file.readers > 0}>Rename</Menu.Item>}
                                        {canRename && <Menu.Item onClick={() => deleteFile(file.key)} disabled={file.writers > 0 || file.readers > 0}>Delete</Menu.Item>}
                                        {canRename && <Menu.Item onClick={() => copyFile(file.key)} disabled={file.writers > 0 || file.readers > 0}>Copy</Menu.Item>}
                                        {canRename && <Menu.Item onClick={() => useFileAsNewDB(file.key)} disabled={file.writers > 0 || file.readers > 0} >Copy and use as new DB</Menu.Item>}
                                    </Menu.Dropdown>
                                </Menu>
                            </Group>

                        </Table.Td>
                    </Table.Tr>
                ))}
                <Table.Tr key={"db"}>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td><b>{formatBytesString(totalSize)}</b></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                </Table.Tr>
            </Table.Tbody>
        </Table>
    </>)
}

const Files = observer(component);
export default Files;
