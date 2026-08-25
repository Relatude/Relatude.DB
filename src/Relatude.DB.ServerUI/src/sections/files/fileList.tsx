import React, { useEffect, useState } from 'react';
import { observer } from 'mobx-react';
import { Table, Button, Group, Menu, ActionIcon, Checkbox, Breadcrumbs, Anchor } from '@mantine/core';
import { useApp } from '../../start/useApp';
import IoSelector from './ioSelector';
import { FileMeta, FolderMeta, FolderSize, NodeStoreContainer } from '../../application/models';
import Upload, { UploadedFile } from './upload';
import DownloadFolder from './downloadFolder';
import { IconDots, IconFolder } from '@tabler/icons-react';
import { formatBytesString, formatDateToString } from '../../utils/formatting';
import { iconSize, iconStroke } from '../../application/common';

type FileOrFolder = {
    key: string;
    isFolder: boolean;
};
export const component = (p: { storeId: string }) => {
    const app = useApp();
    const [store, setStore] = useState<NodeStoreContainer>();
    const [path, setPath] = useState<string[]>([]);
    const [files, setFiles] = useState<FileMeta[]>();
    const [folders, setFolders] = useState<FolderMeta[]>();
    const [folderSizes, setFolderSizes] = useState<Record<string, FolderSize>>({});
    const [calculatingSizes, setCalculatingSizes] = useState<Record<string, boolean>>({});
    const [canRename, setCanRename] = useState<boolean>(false);
    const [selectedIo, setSelectedIo] = useState<string>();
    const [dbFile, setDbFile] = useState<string>("");
    const [selectedRows, setSelectedRows] = useState<FileOrFolder[]>([]);
    const [folderDownload, setFolderDownload] = useState<{ folderPath: string, dirHandle: FileSystemDirectoryHandle } | null>(null);
    useEffect(() => { updateSettings(); }, [p.storeId]);
    useEffect(() => { setPath([]); }, [selectedIo]);
    useEffect(() => { setSelectedRows([]); setFolderSizes({}); updateFilesAndFolders(); }, [selectedIo, path]);
    const folderPrefix = path.length > 0 ? path.join('/') + '/' : '';
    const fullFolderPath = (folderName: string) => folderPrefix + folderName;
    const displayName = (key: string) => key.split('/').pop() ?? key;
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
        if (!storeIsLoadedAndIoBelongsToStore) {
            setFiles(undefined);
            setFolders(undefined);
            setCanRename(false);
            setDbFile("");
            return;
        }
        setCanRename(await app.api.maintenance.canRenameFile(p.storeId, selectedIo!));
        const canHaveSubfolders = await app.api.maintenance.canHaveFolders(p.storeId, selectedIo!);
        if (path.length == 0) {
            // the root shows this store's own files (filtered by its file prefix); folder content is unfiltered browsing
            const storeFiles = await app.api.maintenance.getStoreFiles(p.storeId, selectedIo!);
            setFiles(storeFiles.filter(f => !f.key.includes('/')));
            const rootFolder = canHaveSubfolders ? await app.api.maintenance.getFolder(p.storeId, selectedIo!, "") : undefined;
            setFolders(rootFolder?.subFolders);
        } else {
            const folder = await app.api.maintenance.getFolder(p.storeId, selectedIo!, path.join('/'));
            setFiles(folder.files);
            setFolders(folder.subFolders);
        }
        const storeSettings = await app.api.settings.getSettings(p.storeId, false);
        if (selectedIo) {
            const dbFile = await app.api.maintenance.getFileKeyOfDb(p.storeId, selectedIo, storeSettings.localSettings.filePrefix);
            setDbFile(dbFile);
        } else {
            setDbFile("");
        }
    }
    const calculateFolderSize = async (folderName: string) => {
        const fullPath = fullFolderPath(folderName);
        setCalculatingSizes(s => ({ ...s, [fullPath]: true }));
        try {
            const size = await app.api.maintenance.getFolderSize(p.storeId, selectedIo!, fullPath);
            setFolderSizes(s => ({ ...s, [fullPath]: size }));
        } catch (e: any) {
            alert(e.message);
        } finally {
            setCalculatingSizes(s => ({ ...s, [fullPath]: false }));
        }
    }
    const getStatus = (file: FileMeta) => {
        if (file.writers > 0) return "Write locked";
        if (file.readers > 0) return "Read locked";
        return "-";
    }
    const downloadFile = async (file: FileMeta) => {
        try {
            await app.api.maintenance.downloadFile(p.storeId, selectedIo!, file.key, path.length > 0 ? file : undefined);
        } catch (e: any) {
            alert(e.message);
        }
    }
    const downloadFolder = async (folderName: string) => {
        // the directory picker must be opened inside the click gesture; the download itself runs in the dialog
        if (!(window as any).showDirectoryPicker) {
            alert("Downloading a folder requires a browser with the File System Access API, like Chrome or Edge.");
            return;
        }
        try {
            const dirHandle: FileSystemDirectoryHandle = await (window as any).showDirectoryPicker({ mode: "readwrite", id: "relatude-folder-download" });
            setFolderDownload({ folderPath: fullFolderPath(folderName), dirHandle });
        } catch (e: any) {
            if (e?.name !== "AbortError") alert(e.message); // AbortError = the user closed the picker
        }
    }
    const closeAllOpenStreams = async () => {
        if (!confirm("Are you sure you want to reset all locks?")) return;
        await app.api.maintenance.closeAllOpenStreams(p.storeId, selectedIo!);
        await updateFilesAndFolders();
    }
    const promptForNewName = async (oldKey: string) => {
        // the folder is fixed; only the file name part is edited and validated
        let newName = prompt("Please enter a new name:", displayName(oldKey));
        while (!(await app.api.maintenance.isFileKeyLegal(newName))) {
            if (!newName) return null;
            newName = prompt("Sorry, not a valid filename, try again:", newName);
        }
        if (!newName) return null;
        return folderPrefix + newName;
    }
    const renameFile = async (fileName: string) => {
        const newKey = await promptForNewName(fileName);
        if (!newKey) return;
        await app.api.maintenance.renameFile(p.storeId, selectedIo!, fileName, newKey);
        await updateFilesAndFolders();
    }
    const copyFile = async (fileName: string) => {
        const newKey = await promptForNewName(fileName);
        if (!newKey) return;
        await app.api.maintenance.copyFile(p.storeId, selectedIo!, fileName, selectedIo!, newKey);
        await updateFilesAndFolders();
    }
    const onFileError = (message: string) => {
        alert(message);
        updateFilesAndFolders();
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
        try {
            if (confirmInCaseOfDbFile(file)) await app.api.maintenance.deleteFile(p.storeId, selectedIo!, file);
        } finally {
            await updateFilesAndFolders();
        }
    }
    const deleteFolder = async (folderName: string) => {
        if (!confirm("Are you sure you want to permanently delete the folder \"" + folderName + "\" and everything in it?")) return;
        try {
            await app.api.maintenance.deleteFolder(p.storeId, selectedIo!, fullFolderPath(folderName));
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
            await app.api.maintenance.completeUpload(p.storeId, selectedIo!, upload.uploadId, folderPrefix + upload.name, false);
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
        if (!checked) {
            setSelectedRows([]);
        } else {
            const allFiles = files!.map(f => ({ key: f.key, isFolder: false }));
            const allFolders = folders!.map(f => ({ key: fullFolderPath(f.name), isFolder: true }));
            setSelectedRows([...allFiles, ...allFolders]);
        }
    }
    let totalSize = files?.reduce((acc, file) => acc + file.size, 0) ?? 0;
    let allFolderSizesKnown = true;
    for (const folder of folders ?? []) {
        const known = folderSizes[fullFolderPath(folder.name)];
        if (known) totalSize += known.size;
        else allFolderSizesKnown = false;
    }
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
        {selectedIo && <Breadcrumbs mt="md" mb="xs">
            {path.length > 0 ? <Anchor onClick={() => setPath([])}>Storage root</Anchor> : <span>Storage root</span>}
            {path.map((segment, i) => i < path.length - 1
                ? <Anchor key={i} onClick={() => setPath(path.slice(0, i + 1))}>{segment}</Anchor>
                : <span key={i}>{segment}</span>)}
        </Breadcrumbs>}
        <Table>
            <Table.Thead>
                <Table.Tr>
                    <Table.Th><Checkbox
                        checked={selectedRows.length > 0 && selectedRows.length == (files?.length ?? 0) + (folders?.length ?? 0)}
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
                {folders?.map((folder) => {
                    const fullPath = fullFolderPath(folder.name);
                    const knownSize = folderSizes[fullPath];
                    return (<Table.Tr key={"folder:" + folder.name}
                        bg={selectedRows.filter(f => f.key === fullPath).length > 0 ? 'var(--mantine-color-blue-light)' : undefined}
                    >
                        <Table.Td>
                            <Checkbox
                                checked={selectedRows.filter(f => f.key === fullPath).length > 0}
                                onChange={(e) => setSelectedRows(e.currentTarget.checked ? [...selectedRows, { key: fullPath, isFolder: true }] : selectedRows.filter((k) => k.key !== fullPath))}
                            />
                        </Table.Td>
                        <Table.Td >
                            <Anchor onClick={() => setPath([...path, folder.name])} title="Open folder">
                                <IconFolder size={iconSize} stroke={iconStroke} style={{ verticalAlign: 'middle', marginRight: 5 }} />
                                {folder.name}
                            </Anchor>
                        </Table.Td>
                        <Table.Td>
                            {knownSize
                                ? <>
                                    {formatBytesString(knownSize.size)}
                                    <div style={{ fontSize: '0.78em', opacity: 0.65, whiteSpace: 'nowrap' }}>
                                        {formatCount(knownSize.fileCount, "file")}, {formatCount(knownSize.folderCount, "subfolder")}
                                    </div>
                                </>
                                : <Button variant="subtle" size="compact-xs" loading={calculatingSizes[fullPath]} onClick={() => calculateFolderSize(folder.name)}
                                    title="Calculate the total size of the folder and count its files and subfolders. This can take a while for folders with many files. ">Calculate</Button>}
                        </Table.Td>
                        <Table.Td>{ }</Table.Td>
                        <Table.Td>{folder.description && folder.description != "-" ? folder.description : "[Folder]"}</Table.Td>
                        <Table.Td>{formatDateToString(folder.lastModifiedUtc)}</Table.Td>
                        <Table.Td>{formatDateToString(folder.creationTimeUtc)}</Table.Td>
                        <Table.Td>
                            <Group gap={"sm"}>
                                <Menu width={200} >
                                    <Menu.Target>
                                        <ActionIcon variant="transparent" ><IconDots></IconDots></ActionIcon>
                                    </Menu.Target>
                                    <Menu.Dropdown>
                                        <Menu.Item onClick={() => setPath([...path, folder.name])}>Open</Menu.Item>
                                        <Menu.Item onClick={() => calculateFolderSize(folder.name)}>Calculate size</Menu.Item>
                                        <Menu.Item onClick={() => downloadFolder(folder.name)}>Download folder</Menu.Item>
                                        <Menu.Item onClick={() => deleteFolder(folder.name)}>Delete</Menu.Item>
                                    </Menu.Dropdown>
                                </Menu>
                            </Group>
                        </Table.Td>
                    </Table.Tr>);
                })}
                {files?.map((file) => (
                    <Table.Tr key={file.key}
                        bg={selectedRows.filter(f => f.key === file.key).length > 0 ? 'var(--mantine-color-blue-light)' : undefined}
                    >
                        <Table.Td><Checkbox
                            checked={selectedRows.filter(f => f.key === file.key).length > 0}
                            onChange={(e) => setSelectedRows(e.currentTarget.checked ? [...selectedRows, { key: file.key, isFolder: false }] : selectedRows.filter((k) => k.key !== file.key))}
                        /></Table.Td>
                        <Table.Td >
                            {file.key == dbFile ? <b title="Current database file" style={{ color: "" }}>{displayName(file.key)}</b> : displayName(file.key)}
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
                                        <Menu.Item onClick={() => downloadFile(file)} disabled={file.writers > 0}>Download</Menu.Item>
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
                    <Table.Td><b title={allFolderSizesKnown ? undefined : "Excluding folders without a calculated size"}>
                        {formatBytesString(totalSize) + (allFolderSizesKnown ? "" : " +")}
                    </b></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                    <Table.Td></Table.Td>
                </Table.Tr>
            </Table.Tbody>
        </Table>
        {folderDownload && <DownloadFolder storeId={p.storeId} ioId={selectedIo!} folderPath={folderDownload.folderPath} dirHandle={folderDownload.dirHandle} onClose={() => setFolderDownload(null)} />}
    </>)
}

const formatCount = (count: number, word: string) => count + " " + (count == 1 ? word : word + "s");

const Files = observer(component);
export default Files;
