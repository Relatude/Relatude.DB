using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Security.Cryptography;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    IFileStore getFileStore(Guid fileStoreId) {
        IFileStore fileStore;
        if (fileStoreId == Guid.Empty) {
            fileStore = _defaultFileStore;
        } else {
            if (!_fileStores.TryGetValue(fileStoreId, out fileStore!)) throw new Exception("File store not found");
        }
        return fileStore;
    }
    public async Task<FileValue> FileUploadAsync(PropertyPath target, IIOProvider source, string sourceFileKey, string? fileName = null, bool noNodeUpdate = false, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(target.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var newFileId = Guid.NewGuid();
        using var inputStream = source.OpenRead(sourceFileKey, 0);
        fileName ??= sourceFileKey;
        var r = await fileStore.InsertAsync(newFileId, inputStream, fileName);
        var fileValue = FileValue.CreateNew(fileName, r.Length, r.FileHash, fileStore.Id, newFileId, r.StoreKey, target);
        if (!noNodeUpdate) {
            var t = new TransactionData();
            t.ForceUpdateProperty(target, fileValue);
            execute_outer(t, false, true, ctx, out _);
        }
        return fileValue;
    }
    public async Task<FileValue> FileUploadAsync(PropertyPath target, Stream source, string fileName, bool noNodeUpdate = false, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(target.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var newFileId = Guid.NewGuid();
        var r = await fileStore.InsertAsync(newFileId, source, fileName);
        var fileValue = FileValue.CreateNew(fileName, r.Length, r.FileHash, fileStore.Id, newFileId, r.StoreKey, target);
        if (!noNodeUpdate) {
            var t = new TransactionData();
            t.ForceUpdateProperty(target, fileValue);
            execute_outer(t, false, true, ctx, out _);
        }
        return fileValue;
    }
    public async Task FileDeleteAsync(PropertyPath target, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!TryGetValue<FileValue>(target, out var fileValue, ctx)) throw new Exception("File property not found");
        if (fileValue.IsEmpty) return;
        var fileStore = getFileStore(fileValue.StorageId);
        await fileStore.DeleteAsync(fileValue);
        var t = new TransactionData();
        t.ForceUpdateProperty(target, FileValue.Empty);
        execute_outer(t, false, true, ctx, out _);
    }
    public async Task<FileValue> FileDownloadAsync(PropertyPath target, Stream outStream, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(target, ctx);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(fileValue.StorageId);
        await fileStore.ExtractAsync(fileValue, outStream);
        return fileValue;
    }
    public async Task<bool> IsFileUploadedAndAvailableAsync(PropertyPath target, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(target, ctx);
        if (fileValue.IsEmpty) return false;
        var fileStore = getFileStore(fileValue.StorageId);
        return await fileStore.ContainsFileAsync(fileValue);
    }

    Dictionary<Guid, uploadSession> _uploadSessions = [];
    public async Task<Guid> InitiatePartialUpload(PropertyPath target, string fileName, QueryContext? ctx = null) {
        await removeOldSessions();
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(target.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId) as IFileStoreMultiPartSupport;
        if (fileStore == null) throw new Exception("File store does not support multipart upload");
        var newFileId = Guid.NewGuid();
        var storeKey = await fileStore.InitiatePartialUpload(newFileId, fileName);
        var fileValue = FileValue.CreateNew(fileName, 0, string.Empty, fileStore.Id, newFileId, storeKey, target);
        lock (_uploadSessions) {
            _uploadSessions[newFileId] = new uploadSession(fileValue);
        }
        return fileValue.FileId;
    }
    public async Task AppendPartialUploadAsync(Guid fileId, byte[] data) {
    }
    public async Task<FileValue> FinalizePartialUpload(Guid fileId, bool noNodeUpdate = false) {
    }
    public async Task<FileValue> CancelPartialUpload(Guid fileId) {
    }
    async Task removeOldSessions() {
        List<uploadSession> toRemove;
        lock (_uploadSessions) {
            toRemove = _uploadSessions.Values.Where(s => DateTime.UtcNow - s.LastAccessed > TimeSpan.FromMinutes(10)).ToList();
            foreach (var s in toRemove) _uploadSessions.Remove(s.FileValue.FileId);
        }
        foreach (var s in toRemove) {
            try {
                var fileStore = getFileStore(s.FileValue.StorageId);
                await fileStore.DeleteAsync(s.FileValue);
            } catch (Exception e) {
                logError($"Failed to remove old upload session for file {s.FileValue.FileId}: {e}");
            }
        }
    }
    class uploadSession(FileValue fileValue) {
        public DateTime LastAccessed = DateTime.MinValue;
        public void Touch() => LastAccessed = DateTime.UtcNow;
        public FileValue FileValue { get; set; } = fileValue;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    }
}
