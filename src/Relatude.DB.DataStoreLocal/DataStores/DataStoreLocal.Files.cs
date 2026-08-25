using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    internal IFileStore getFileStore(Guid fileStoreId) {
        IFileStore fileStore;
        if (fileStoreId == Guid.Empty) {
            fileStore = _defaultFileStore;
        } else {
            if (!_fileStores.TryGetValue(fileStoreId, out fileStore!)) throw new Exception("File store not found");
        }
        return fileStore;
    }
    async Task<FileValue> updateMetaIfRelevant(FileValue fileValue, int? maxWaitForMetaUpdate, QueryContext ctx) {
        if (fileValue.PropertyPath == null) return fileValue;
        if (maxWaitForMetaUpdate == null) {
            if (fileValue.FileType == FileType.Image) {
                maxWaitForMetaUpdate = 500;
            } else {
                maxWaitForMetaUpdate = 0;
            }
        }
        if (maxWaitForMetaUpdate.Value > 0) {
            var newFileValue = await updateFileMeta(fileValue.PropertyPath, fileValue.FileId, maxWaitForMetaUpdate.Value, ctx);
            if (newFileValue != null) fileValue = newFileValue;
        } else {
            enqueueUpdateFileMeta(fileValue.PropertyPath, fileValue.FileId, ctx);
        }
        return fileValue;
    }
    public async Task<FileValue> FileUploadAsync(PropertyPath propertyPath, IIOProvider source, string[] sourceFileKey, string? fileName = null, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var newFileId = Guid.NewGuid();
        using var inputStream = source.OpenRead(sourceFileKey, 0);
        fileName ??= sourceFileKey.FileName();
        var r = await fileStore.InsertAsync(newFileId, inputStream, fileName);
        var fileValue = FileValue.CreateNew(fileName, r.Length, r.FileHash, fileStore.Id, newFileId, r.StoreKey, propertyPath);
        var t = new TransactionData();
        t.ForceUpdateProperty(propertyPath, fileValue);
        Execute(t, false, false, ctx);
        return await updateMetaIfRelevant(fileValue, maxWaitForMetaUpdate, ctx);
    }
    public async Task<FileValue> FileUploadAsync(PropertyPath propertyPath, Stream source, string fileName, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var newFileId = Guid.NewGuid();
        var r = await fileStore.InsertAsync(newFileId, source, fileName);
        var fileValue = FileValue.CreateNew(fileName, r.Length, r.FileHash, fileStore.Id, newFileId, r.StoreKey, propertyPath);
        var t = new TransactionData();
        t.ForceUpdateProperty(propertyPath, fileValue);
        Execute(t, false, false, ctx);
        return await updateMetaIfRelevant(fileValue, maxWaitForMetaUpdate, ctx);
    }
    public FileValue? UpdateFileMetaIfNotSet(PropertyPath propertyPath, Guid fileId, BasicFileMeta meta, QueryContext? ctx = null) {
        // Console.WriteLine("Possible file meta update for fileId: " + fileId);
        ctx ??= _defaultQueryCtx;
        var nodeGuid = _guids.ValidateAndReturnIntId(propertyPath.NodePath.NodeKey);
        var lockId = RequestLockAsync(nodeGuid, 1000, 1000).GetAwaiter().GetResult();
        try {
            if (!TryGetValue<FileValue>(propertyPath, out var fileValue, ctx)) return null; // file is deleted
            if (fileValue.FileId != fileId) return null; // file have changed
            if (fileValue.Width > 0) return fileValue; // meta is already set
            // Console.WriteLine("File meta is not set... " + fileId);
            var t = new TransactionData();
            t.LockExcemptions = [lockId];
            var isDifferent =
                fileValue.MetaJSON != (meta.AllMetaJson ?? string.Empty) ||
                fileValue.Width != meta.Width ||
                fileValue.Height != meta.Height;
            if (isDifferent) {
                fileValue.Width = meta.Width;
                fileValue.Height = meta.Height;
                fileValue.MetaJSON = meta.AllMetaJson ?? string.Empty;
                t.ForceUpdateProperty(propertyPath, fileValue);
                Execute(t, false, false, ctx);
                // Console.WriteLine("File meta updated for fileId: " + fileId);
            } else {
                // Console.WriteLine("File meta is already up to date for fileId: " + fileId);
            }
            return fileValue;
        } finally {
            ReleaseLock(lockId);
        }
    }
    void enqueueUpdateFileMeta(PropertyPath propertyPath, Guid fileId, QueryContext? ctx = null) {
        ThreadPool.QueueUserWorkItem(async _ => {
            try {
                await Task.Delay(5000); // giving time for other image reads to update meta first, so we can avoid loading image twice
                await updateFileMeta(propertyPath, fileId, 10000, ctx);
            } catch (Exception ex) {
                LogError("Error updating file meta: " + ex.ToString(), ex);
            }
        });
    }
    async Task<FileValue?> updateFileMeta(PropertyPath propertyPath, Guid fileId, int maxWaitForMetaUpdate, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!TryGetValue<FileValue>(propertyPath, out var fileValue, ctx)) return null;
        if (fileValue.Width > 0) return fileValue; // meta is already set
        FileAdjustmentMeta adj = new();
        var conversionResult = await GetFileStreamAndState(propertyPath, adj, maxWaitForMetaUpdate, ctx);
        if (!conversionResult.IsReady) {
            await _fileConversionEngine.CancelRunning(conversionResult.ConversionId, false);
            return null;
        }
        var meta = BasicFileMeta.FromBytes(conversionResult.GetBytes());
        if (meta == null) return null;
        return UpdateFileMetaIfNotSet(propertyPath, fileId, meta, ctx);
    }

    public async Task FileDeleteAsync(PropertyPath propertyPath, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!TryGetValue<FileValue>(propertyPath, out var fileValue, ctx)) throw new Exception("File property not found");
        if (fileValue.IsEmpty) return;
        var fileStore = getFileStore(fileValue.StorageId);
        await fileStore.DeleteAsync(fileValue);
        var t = new TransactionData();
        t.ForceUpdateProperty(propertyPath, FileValue.Empty);
        Execute(t, false, true, ctx);
    }
    public async Task<FileValue> FileDownloadAsync(PropertyPath propertyPath, Stream outStream, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(fileValue.StorageId);
        await fileStore.ExtractAsync(fileValue, outStream);
        return fileValue;
    }
    public async Task<bool> IsFileUploadedAndAvailableAsync(PropertyPath propertyPath, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty) return false;
        var fileStore = getFileStore(fileValue.StorageId);
        return await fileStore.ContainsFileAsync(fileValue);
    }
    public bool FileStoreSupportsMultipartUploads(PropertyPath propertyPath) {
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        return fileStore is IFileStoreMultiPartSupport;
    }
    public async Task<Guid> InitiateMultipartUploadAsync(PropertyPath propertyPath, string fileName, QueryContext? ctx = null) {
        await _uploads.removeOldSessions();
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        if (getFileStore(fileProp.FileStorageProviderId) is not IFileStoreMultiPartSupport fileStore)
            throw new Exception("File store does not support multipart upload");
        var newFileId = Guid.NewGuid();
        var storeKey = await fileStore.InitiatePartialUpload(newFileId, fileName);
        var fileValue = FileValue.CreateNew(fileName, 0, string.Empty, fileStore.Id, newFileId, storeKey, propertyPath);
        _uploads.AddSession(fileValue);
        return fileValue.FileId;
    }
    public async Task AppendMultipartUploadAsync(Guid fileId, byte[] data, int length) {
        var session = _uploads.getSession(fileId);
        var fileKey = FileValue.GetFileKeyData(session.FileValue);
        await _uploads.getMultiPartStore(session).AppendDataAsync(fileId, fileKey, data, length);
        session.Hash.AppendData(data, 0, length);
        var f = session.FileValue;
        var key = FileValue.GetFileKeyData(f);
        var newFileValue = FileValue.CreateNew(f.Name, f.Size + length, f.Hash, f.StorageId, f.FileId, key, f.PropertyPath!);
        session.FileValue = newFileValue;
    }
    public async Task<FileValue> FinalizeMultipartUploadAsync(Guid fileId, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        var session = _uploads.getSession(fileId);
        FileValue fileValue;
        var propertyPath = session.FileValue.PropertyPath;
        if (propertyPath == null) throw new Exception("File value does not have a property path");
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileHash = Convert.ToHexString(session.Hash.GetHashAndReset());
        _uploads.removeSession(fileId);
        var f = session.FileValue;
        var key = FileValue.GetFileKeyData(f);
        fileValue = FileValue.CreateNew(f.Name, f.Size, fileHash, f.StorageId, f.FileId, key, propertyPath);
        var t = new TransactionData();
        t.ForceUpdateProperty(propertyPath, fileValue);
        Execute(t, false, false, ctx);
        return await updateMetaIfRelevant(fileValue, maxWaitForMetaUpdate, ctx);
    }
    public async Task CancelMultipartUpload(Guid fileId) {
        var session = _uploads.getSession(fileId);
        _uploads.removeSession(fileId);
        var fileStore = _uploads.getMultiPartStore(session);
        await fileStore.DeleteAsync(session.FileValue);
    }

    // uploads insert into the file store before the transaction referencing them executes, so a file
    // this young may look unreferenced while it is being linked up; such files are always kept
    static readonly TimeSpan _unreferencedFileGracePeriod = TimeSpan.FromMinutes(15);
    /// <summary>
    /// Deletes every file in the container's file stores that no current node references, along with
    /// folders left empty. Only stores implementing <see cref="IFileStoreDeleteUnreferenced"/> are
    /// cleaned. References are collected from every node including all revisions and embedded
    /// objects, plus in-flight uploads, and files younger than a grace period are always kept, so
    /// the call is safe while the store is in use. With <paramref name="countOnly"/> nothing is
    /// deleted and the result reports what a real run would have deleted. <paramref name="onProgress"/>
    /// is called with a phase description and a 0-100 percentage.
    /// </summary>
    public async Task<DeleteUnReferenceResult> DeleteUnreferencedFilesAsync(bool countOnly, Action<string, int>? onProgress = null, CancellationToken cancellationToken = default) {
        validateDatabaseState();
        var activityId = RegisterActvity(DataStoreActivityCategory.RunningTask, countOnly ? "Counting unreferenced files" : "Deleting unreferenced files");
        try {
            var lastPct = -1;
            void report(string description, int pct) {
                if (pct == lastPct) return;
                lastPct = pct;
                UpdateActivity(activityId, description, pct);
                onProgress?.Invoke(description, pct);
            }
            var cutoffUtc = DateTime.UtcNow - _unreferencedFileGracePeriod;
            var stores = _fileStores.Values.Append(_defaultFileStore).DistinctBy(s => s.Id).OfType<IFileStoreDeleteUnreferenced>().ToArray();
            if (stores.Length == 0) return new DeleteUnReferenceResult(0, 0, 0);
            var validByStore = stores.ToDictionary(s => s.Id, _ => new HashSet<string>());
            async Task addReference(FileValue fileValue) {
                var storageId = fileValue.StorageId == Guid.Empty ? _defaultFileStore.Id : fileValue.StorageId;
                if (!validByStore.TryGetValue(storageId, out var references)) return; // store missing or not cleanable
                references.Add(await ((IFileStoreDeleteUnreferenced)getFileStore(storageId)).GetInternalReference(fileValue));
            }
            var fileValues = new List<FileValue>();
            var ids = _nodes.Snapshot().Select(s => s.nodeId).ToArray();
            const int batchSize = 1000;
            for (var offset = 0; offset < ids.Length; offset += batchSize) {
                cancellationToken.ThrowIfCancellationRequested();
                fileValues.Clear();
                var end = Math.Min(offset + batchSize, ids.Length);
                _lock.EnterReadLock();
                try {
                    for (var i = offset; i < end; i++) {
                        if (_nodes.TryGet(ids[i], out var node, out _)) collectFileValues(node, fileValues);
                    }
                } finally {
                    _lock.ExitReadLock();
                }
                foreach (var fileValue in fileValues) await addReference(fileValue);
                report("Collecting file references", (int)(end * 50L / ids.Length));
            }
            foreach (var fileValue in _uploads.GetActiveFileValues()) await addReference(fileValue);
            long bytes = 0; int files = 0, folders = 0;
            for (var i = 0; i < stores.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                var store = stores[i];
                var storeNo = i; // captured by the progress callback below
                var description = (countOnly ? "Scanning file store " : "Cleaning file store ") + (i + 1) + " of " + stores.Length;
                var result = await store.DeleteUnreferenced(validByStore[store.Id], countOnly, cutoffUtc,
                    (processed, total) => report(description, 50 + (int)((storeNo + (total == 0 ? 1.0 : (double)processed / total)) * 50 / stores.Length)),
                    cancellationToken);
                bytes += result.TotalBytesDeleted;
                files += result.TotalFilesDeleted;
                folders += result.TotalFoldersDeleted;
            }
            report(countOnly ? "Count completed" : "Cleanup completed", 100);
            return new DeleteUnReferenceResult(bytes, files, folders);
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    static void collectFileValues(INodeData node, List<FileValue> into) {
        if (node is NodeDataRevisions revisions) { // revision containers hold no values of their own
            foreach (var revision in revisions.Revisions) collectFileValues(revision, into);
            return;
        }
        foreach (var entry in node.Values) {
            if (entry.Value is FileValue fileValue) {
                if (!fileValue.IsEmpty) into.Add(fileValue);
            } else if (entry.Value is IInnerNodeDataMap innerNodes) {
                foreach (var inner in innerNodes) collectFileValues(inner, into);
            }
        }
    }
}
