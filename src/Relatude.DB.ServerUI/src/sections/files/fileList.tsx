import React, { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Table, Button, Group, Menu, MenuItem, Divider, ActionIcon, Checkbox } from '@mantine/core';
import { useApp } from '../../start/useApp';
import IoSelector from './ioSelector';
import { FileMeta, FolderMeta, NodeStoreContainer } from '../../application/models';
import Upload, { UploadedFile } from './upload';
import { IconDots, IconFile, IconFolder } from '@tabler/icons-react';
import { formatBytesString, formatDateToString } from '../../utils/formatting';
import { iconSize, iconStroke } from '../../application/common';

type FileOrFolder = {
    key: string;
    isFolder: boolean;
};
export const component = (p: { storeId: string }) => {
    const app = useApp();
    const [store, setStore] = useState<NodeStoreContainer>();
    const [files, setFiles] = useState<FileMeta[]>();
    const [folders, setFolders] = useState<FolderMeta[]>();
    const [canRename, setCanRename] = useState<boolean>(false);
    const [selectedIo, setSelectedIo] = useState<string>();
    const [dbFile, setDbFile] = useState<string>("");
    const [selectedRows, setSelectedRows] = useState<FileOrFolder[]>([]);
    useEffect(() => { updateSettings(); }, [p.storeId]);
    useEffect(() => { updateFilesAndFolders(); }, [selectedIo]);
    const updateSettings = async () => {
        if (p.storeId) {
            const store = await app.api.settings.getSettings(p.storeId, false);
            setStore(store);
            setSelectedIo(store.ioSettings[0]?.id);
        } else {
            setStore(undefined);
            setSelectedIo(undefined);
        }
    }
    const updateFilesAndFolders = async () => {
        const storeIsLoadedAndIoBelongsToStore = store?.id == p.storeId && store?.ioSettings?.find(io => io.id == selectedIo) != undefined;
        setFiles(storeIsLoadedAndIoBelongsToStore ? await app.api.maintenance.getStoreFiles(p.storeId, selectedIo!) : undefined);
        setCanRename(storeIsLoadedAndIoBelongsToStore ? await app.api.maintenance.canRenameFile(p.storeId, selectedIo!) : false);
        const canHaveSubfolders = storeIsLoadedAndIoBelongsToStore ? await app.api.maintenance.canHaveFolders(p.storeId, selectedIo!) : false;
        const folders = canHaveSubfolders ? await app.api.maintenance.getFolders(p.storeId, selectedIo!, "") : undefined;
        setFolders(folders);
        const storeSettings = await app.api.settings.getSettings(p.storeId, false);
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
        await updateFilesAndFolders();
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
        await updateFilesAndFolders();
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
        await updateFilesAndFolders();
    }
    const onFileError = (message: string) => {
        alert(message);
        updateFilesAndFolders();
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
        await updateFilesAndFolders();
    }
    const getSelectionText = () => {
        const nofiles = selectedRows.filter(f => !f.isFolder).length;
        const noFolders = selectedRows.filter(f => f.isFolder).length;
        const s = (word: string, count: number) => count == 1 ? word : word + "s";
        if (nofiles > 0 && noFolders > 0) {
            return `${nofiles} ${s("file", nofiles)} and ${noFolders} ${s("folder", noFolders)}`;
        } else if (nofiles > 0) {
            return `${nofiles} ${s("file", nofiles)}`;
        } else if (noFolders > 0) {
            return `${noFolders} ${s("folder", noFolders)}`;
        }
        return "0 files or folders";
    }
    const deleteSelectedFilesAndFolders = async () => {
        const confirmMessage = "Are you sure you want to permanently delete " + getSelectionText() + "?";
        if (!confirm(confirmMessage)) return;
        try {
            for (const file of selectedRows) {
                if (file.isFolder) {
                    await app.api.maintenance.deleteFolder(p.storeId, selectedIo!, file.key);
                } else {
                    if (!confirmInCaseOfDbFile(file.key)) return;
                    await app.api.maintenance.deleteFile(p.storeId, selectedIo!, file.key);
                }
            }
            setSelectedRows([]);
        } finally {
            await updateFilesAndFolders();
        }
    }
    const deleteFile = async (file: string) => {
        if (!confirm("Are you sure you want to permanently delete this file?")) return;
        const storeSettings = await app.api.settings.getSettings(p.storeId, false);
        try {
            if (confirmInCaseOfDbFile(file)) await app.api.maintenance.deleteFile(p.storeId, selectedIo!, file);
        } finally {
            await updateFilesAndFolders();
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
        updateFilesAndFolders();
    }
    const onCompleteDatabaseUpload = async (uploads: UploadedFile[]) => {
        for (const upload of uploads) {
            const dbName = await app.api.maintenance.getFileKeyOfNextDb(p.storeId, selectedIo!, store?.localSettings.filePrefix);
            await app.api.maintenance.completeUpload(p.storeId, selectedIo!, upload.uploadId, dbName, true);
            await restartIfOpen();
        }
        await updateFilesAndFolders();
    }
    const useFileAsNewDB = async (fileName: string) => {
        const newName = await app.api.maintenance.getFileKeyOfNextDb(p.storeId, selectedIo!, store?.localSettings.filePrefix);
        await app.api.maintenance.copyFile(p.storeId, selectedIo!, fileName, selectedIo!, newName);
        await restartIfOpen();
        await updateFilesAndFolders();
    }
    const restartIfOpen = async () => {
        if (app.ui.isStoreOpen(p.storeId)) {
            await app.api.maintenance.close(p.storeId);
            await app.api.maintenance.open(p.storeId);
        }
    }
    const selectAll = (checked: boolean) => {
        // const allFiles = files!.map(f => ({ key: f.key, isFolder: false }));
        // const allFolders = folders!.map(f => ({ key: f.name, isFolder: true }));
        // setSelectedRows([...allFiles, ...allFolders]);
        // return
        const coundFilesAndFolders = (files?.length ?? 0) + (folders?.length ?? 0);
        if (false && selectedRows.length === coundFilesAndFolders || !checked) {
            setSelectedRows([]);
        } else {
            const allFiles = files!.map(f => ({ key: f.key, isFolder: false }));
            const allFolders = folders!.map(f => ({ key: f.name, isFolder: true }));
            setSelectedRows([...allFiles, ...allFolders]);
        }
    }
    let totalSize = files?.reduce((acc, file) => acc + file.size, 0) ?? 0;
    totalSize += getFoldersRecursiveSize(folders);
    const selectedIOIsDatabase = selectedIo ? app.ui.isIoUsedForCurrentDatabase(selectedIo) : false;
    const domainName = location.hostname;
    return (<>
        <IoSelector ioSettings={store?.ioSettings} selectedIo={selectedIo} onChange={(id) => setSelectedIo(id)} />
        {selectedIo && <Button variant="light" onClick={updateFilesAndFolders}>Refresh</Button>}
        {selectedIo && <Upload text="Upload file" storeId={p.storeId} ioId={selectedIo} onComplete={onCompleteUpload} onError={onFileError} onCancel={updateFilesAndFolders} multiple />}
        {selectedIOIsDatabase && <Upload text="Upload database"
            title="Uploaded file will be renamed correctly and opened. Existing database will remain in the filesystem. "
            storeId={p.storeId} ioId={selectedIo!} onComplete={onCompleteDatabaseUpload} onError={onFileError} onCancel={updateFilesAndFolders} ignoreSameName />}
        {(selectedIOIsDatabase && app.ui.getStoreState(p.storeId) == "Open") && <Button variant="light" onClick={() => app.api.maintenance.downloadTruncatedDb(p.storeId, domainName)}
            title='A copy of current data, without transaction history. This will not block execution. '
        >Download database</Button>}
        {(selectedIOIsDatabase && app.ui.getStoreState(p.storeId) == "Open") && <Button variant="light" onClick={() => app.api.maintenance.downloadFullDb(p.storeId, domainName)}
            title='A copy of the current database file with transaction history. This will temporarily block execution. '
        >Download with history</Button>}
        {(app.ui.getStoreState(p.storeId) == "Open") && <Button variant="light" onClick={() => app.api.maintenance.backUpNow(p.storeId, selectedIo!, true, true)}
            title='Create a backup of the current database to this IO. '
        >Backup database</Button>}
        {(selectedIo && selectedRows.length > 0) && <Button variant="light" color='red' onClick={deleteSelectedFilesAndFolders}
            title={'Permanently delete ' + getSelectionText()}
        >Delete {getSelectionText()}</Button>}
        <Table>
            <Table.Thead>
                <Table.Tr>
                    <Table.Th><Checkbox
                        checked={selectedRows.length == (files?.length ?? 0) + (folders?.length ?? 0)}
                        indeterminate={selectedRows.length > 0 && selectedRows.length < (files?.length ?? 0) + (folders?.length ?? 0)}
                        onChange={(e) => selectAll(e.currentTarget.checked)}
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
                        bg={selectedRows.filter(f => f.key === file.key).length > 0 ? 'var(--mantine-color-blue-light)' : undefined}
                    >
                        <Table.Td><Checkbox
                            checked={selectedRows.filter(f => f.key === file.key).length > 0}
                            onChange={(e) => setSelectedRows(e.currentTarget.checked ? [...selectedRows, { key: file.key, isFolder: false }] : selectedRows.filter((k) => k.key !== file.key))}
                        /></Table.Td>
                        <Table.Td >
                            {/* {<IconFile size={iconSize} stroke={iconStroke} style={{ verticalAlign: 'middle', marginRight: 5 }} />} */}
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
                                        <ActionIcon variant="transparent"><IconDots></IconDots></ActionIcon>
                                    </Menu.Target>
                                    <Menu.Dropdown>
                                        <Menu.Item onClick={() => downloadFile(file.key)} disabled={file.writers > 0}>Download</Menu.Item>
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
                {folders?.map((folder) => (
                    <Table.Tr key={folder.name}
                        bg={selectedRows.filter(f => f.key === folder.name).length > 0 ? 'var(--mantine-color-blue-light)' : undefined}
                    >
                        <Table.Td>
                            <Checkbox
                                checked={selectedRows.filter(f => f.key === folder.name).length > 0}
                                onChange={(e) => setSelectedRows(e.currentTarget.checked ? [...selectedRows, { key: folder.name, isFolder: true }] : selectedRows.filter((k) => k.key !== folder.name))}
                            />
                        </Table.Td>
                        <Table.Td >
                            <IconFolder size={iconSize} stroke={iconStroke} style={{ verticalAlign: 'middle', marginRight: 5 }} />
                            {folder.name}
                        </Table.Td>
                        <Table.Td>{formatBytesString(getFoldersRecursiveSize([folder]))}</Table.Td>
                        <Table.Td>{ }</Table.Td>
                        <Table.Td>[Folder]{ }</Table.Td>
                        <Table.Td>{formatDateToString(folder.lastModifiedUtc)}</Table.Td>
                        <Table.Td>{formatDateToString(folder.creationTimeUtc)}</Table.Td>
                        <Table.Td>
                            <Group gap={"sm"}>
                                <Menu width={200} >
                                    <Menu.Target>
                                        <ActionIcon variant="transparent" ><IconDots></IconDots></ActionIcon>
                                    </Menu.Target>
                                    <Menu.Dropdown>
                                        {canRename && <Menu.Item onClick={() => deleteFile(folder.name)} disabled={false}>Delete</Menu.Item>}
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
const getFoldersRecursiveSize = (folders?: FolderMeta[]): number => {
    if (!folders) return 0;
    let total = 0;
    for (const folder of folders) {
        total += folder.files.reduce((acc, file) => acc + file.size, 0);
        total += folder.subFolders.reduce((acc, subFolder) => acc + getFoldersRecursiveSize([subFolder]), 0);
    }
    return total;
}

const Files = observer(component);
export default Files;
