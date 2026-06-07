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
    public async Task<FileValue> FileUploadAsync(PropertyPath propertyPath, IIOProvider source, string sourceFileKey, string? fileName = null, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var newFileId = Guid.NewGuid();
        using var inputStream = source.OpenRead(sourceFileKey, 0);
        fileName ??= sourceFileKey;
        var r = await fileStore.InsertAsync(newFileId, inputStream, fileName);
        var fileValue = FileValue.CreateNew(fileName, r.Length, r.FileHash, fileStore.Id, newFileId, r.StoreKey, propertyPath);
        var t = new TransactionData();
        t.ForceUpdateProperty(propertyPath, fileValue);
        Execute(t, false, false, ctx);
        enqueueUpdateFileMeta(propertyPath, fileValue.FileId, ctx);
        return fileValue;
    }
    public async Task<FileValue> FileUploadAsync(PropertyPath propertyPath, Stream source, string fileName, QueryContext? ctx = null) {
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
        enqueueUpdateFileMeta(propertyPath, fileValue.FileId, ctx);
        return fileValue;
    }
    public void UpdateFileMetaIfNotSet(PropertyPath propertyPath, Guid fileId, BasicFileMeta meta, QueryContext? ctx = null) {
        // Console.WriteLine("Possible file meta update for fileId: " + fileId);
        ctx ??= _defaultQueryCtx;
        var nodeGuid = _guids.ValidateAndReturnIntId(propertyPath.NodePath.NodeKey);
        var lockId = RequestLockAsync(nodeGuid, 1000, 1000).GetAwaiter().GetResult();
        try {
            if (!TryGetValue<FileValue>(propertyPath, out var fileValue, ctx)) return; // file is deleted
            if (fileValue.FileId != fileId) return; // file have changed
            if (fileValue.Width > 0) return; // meta is already set
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
        } finally {
            ReleaseLock(lockId);
        }
    }

    void enqueueUpdateFileMeta(PropertyPath propertyPath, Guid fileId, QueryContext? ctx = null) {
        ThreadPool.QueueUserWorkItem(async _ => {
            try {
                await Task.Delay(5000);
                await updateFileMeta(propertyPath, fileId, ctx);
            } catch (Exception ex) {
                LogError("Error updating file meta: " + ex.ToString(), ex);
            }
        });
    }
    async Task updateFileMeta(PropertyPath propertyPath, Guid fileId, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        FileAdjustmentMeta adj = new();
        var conversionResult = await GetFileStreamAndState(propertyPath, adj, 10000, ctx);
        if (!conversionResult.IsReady) {
            await _fileConversionEngine.CancelRunning(conversionResult.ConversionId, false);
            return;
        }
        var meta = BasicFileMeta.FromBytes(conversionResult.GetBytes());
        if (meta == null) return;
        UpdateFileMetaIfNotSet(propertyPath, fileId, meta, ctx);
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
        return fileStore.SupportsMultipartUploads();
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
    public async Task<FileValue> FinalizeMultipartUploadAsync(Guid fileId, QueryContext? ctx = null) {
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
        enqueueUpdateFileMeta(propertyPath, fileValue.FileId, ctx);
        return fileValue;
    }
    public async Task CancelMultipartUpload(Guid fileId) {
        var session = _uploads.getSession(fileId);
        _uploads.removeSession(fileId);
        var fileStore = _uploads.getMultiPartStore(session);
        await fileStore.DeleteAsync(session.FileValue);
    }
}
