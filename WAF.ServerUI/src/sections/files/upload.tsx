import React, { useState } from 'react';
import { observer } from "mobx-react-lite";
import { useApp } from "../../start/useApp";
import { Button, Group, MultiSelect, Progress } from "@mantine/core";

let cancelling = false;
export type UploadedFile = {
    uploadId: string;
    name: string;
    size: number;
}
const component = (p: {title?:string, text: string, storeId: string, ioId: string, ignoreSameName?: boolean, multiple?: boolean, onComplete: (uploads: UploadedFile[]) => void, onCancel: () => void, onError: (msg: string) => void }) => {
    const app = useApp();
    const [uploadProgress, setUploadProgress] = useState<{ progress: number, info: string } | undefined>(undefined);
    if (p.storeId == null || p.ioId == null) return null;
    const getFileReaderFromUser = async () => {
        const input = document.createElement("input");
        input.type = "file";
        input.multiple = p.multiple ?? false;
        input.click();
        return new Promise<FileList>((resolve, reject) => {
            input.onchange = (e) => {
                const files = (e.target as HTMLInputElement).files;
                if (files && files.length > 0) {
                    resolve(files);
                } else {
                    reject("No file selected");
                }
            }
        });
    }
    const formatKB = (size: number) => Math.round(size / 1024).toLocaleString() + " KB";
    const upload = async () => {
        if (!p.ioId) return;
        const files = await getFileReaderFromUser();
        let total = 0;
        for (const file of files) {
            if (!p.ignoreSameName) {
                const exists = await app.api.maintenance.fileExist(p.storeId, p.ioId, file.name);
                if (exists) {
                    alert("File " + file.name + " already exists. Please rename name before uploading. ");
                    return;
                }
            }
            total += file.size;
        }
        const uploads: UploadedFile[] = [];
        let uploaded = 0;
        try {
            setUploadProgress({ progress: 0, info: " 0KB of " + formatKB(total) + " - 0 MB/sec" });
            for (const file of files) {
                const uploadId = await app.api.maintenance.initiateUpload(p.storeId);
                const partSize = 1024 * 1024; // 1MB
                const parts = Math.ceil(file.size / partSize);
                const startTime = Date.now();
                for (let i = 0; i < parts; i++) {
                    const start = i * partSize;
                    const end = Math.min((i + 1) * partSize, file.size);
                    const data = file.slice(start, end);
                    const elapsed = (Date.now() - startTime) / 1000;
                    const speed = Math.round(end / elapsed / 1024 / 1024);
                    const progress = Math.round(((uploaded + end) / total) * 100);
                    setUploadProgress({ progress, info: formatKB(end + uploaded) + " of " + formatKB(total) + " - " + speed + " MB/sec" });
                    await app.api.maintenance.uploadPart(uploadId, await data.arrayBuffer());
                    if (cancelling) {
                        await app.api.maintenance.cancelUpload(uploadId);
                        setUploadProgress(undefined);
                        p.onCancel();
                        return;
                    }
                }
                uploaded += file.size;
                uploads.push({ uploadId, name: file.name, size: file.size });
            }
            p.onComplete(uploads);
        } catch (e) {
            for (const upload of uploads) await app.api.maintenance.cancelUpload(upload.uploadId);
            p.onError(e.message);
        } finally {
            cancelling = false;
            setUploadProgress(undefined);
        }
    }
    return (
        <Group>
            {uploadProgress ?
                <Group>
                    <Button variant="light" onClick={() => cancelling = true} color="orange">Cancel</Button>
                    <Progress size="xl" w={200} style={{ margin: "auto" }} value={uploadProgress?.progress} />
                    <div>{uploadProgress?.info}</div>
                </Group>
                :
                <Button title={p.title} variant="light" onClick={upload}>{p.text}</Button>}
        </Group >
    );
}
const componentObserver = observer(component);
export default componentObserver;