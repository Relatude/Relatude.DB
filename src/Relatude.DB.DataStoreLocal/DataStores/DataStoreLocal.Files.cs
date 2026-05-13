using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Globalization;
using System.Security.Cryptography;
using static System.Collections.Specialized.BitVector32;
using static System.Runtime.InteropServices.JavaScript.JSType;
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
    public async Task<FileValue> FileUploadAsync(PropertyPath propertyPath, IIOProvider source, string sourceFileKey, string? fileName = null, bool noNodeUpdate = false, QueryContext? ctx = null) {
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
        if (!noNodeUpdate) {
            var t = new TransactionData();
            t.ForceUpdateProperty(propertyPath, fileValue);
            execute_outer(t, false, true, ctx, out _);
        }
        return fileValue;
    }
    public async Task<FileValue> FileUploadAsync(PropertyPath propertyPath, Stream source, string fileName, bool noNodeUpdate = false, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var newFileId = Guid.NewGuid();
        var r = await fileStore.InsertAsync(newFileId, source, fileName);
        var fileValue = FileValue.CreateNew(fileName, r.Length, r.FileHash, fileStore.Id, newFileId, r.StoreKey, propertyPath);
        if (!noNodeUpdate) {
            var t = new TransactionData();
            t.ForceUpdateProperty(propertyPath, fileValue);
            execute_outer(t, false, true, ctx, out _);
        }

        return fileValue;
    }
    public async Task FileDeleteAsync(PropertyPath propertyPath, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        if (!TryGetValue<FileValue>(propertyPath, out var fileValue, ctx)) throw new Exception("File property not found");
        if (fileValue.IsEmpty) return;
        var fileStore = getFileStore(fileValue.StorageId);
        await fileStore.DeleteAsync(fileValue);
        var t = new TransactionData();
        t.ForceUpdateProperty(propertyPath, FileValue.Empty);
        execute_outer(t, false, true, ctx, out _);
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

    Dictionary<Guid, uploadSession> _uploadSessions = [];
    public async Task<Guid> InitiatePartialUploadAsync(PropertyPath propertyPath, string fileName, QueryContext? ctx = null) {
        await removeOldSessions();
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        if (getFileStore(fileProp.FileStorageProviderId) is not IFileStoreMultiPartSupport fileStore)
            throw new Exception("File store does not support multipart upload");
        var newFileId = Guid.NewGuid();
        var storeKey = await fileStore.InitiatePartialUpload(newFileId, fileName);
        var fileValue = FileValue.CreateNew(fileName, 0, string.Empty, fileStore.Id, newFileId, storeKey, propertyPath);
        lock (_uploadSessions) {
            _uploadSessions[newFileId] = new uploadSession(fileValue);
        }
        return fileValue.FileId;
    }
    public async Task AppendPartialUploadAsync(Guid fileId, byte[] data, int length) {
        var session = getSession(fileId);
        var fileKey = FileValue.GetFileKeyData(session.FileValue);
        await getMultiPartStore(session).AppendDataAsync(fileId, fileKey, data, length);
        session.Hash.AppendData(data, 0, length);
        var f = session.FileValue;
        var key = FileValue.GetFileKeyData(f);
        var newFileValue = FileValue.CreateNew(f.Name, f.Size + length, f.Hash, f.StorageId, f.FileId, key, f.PropertyPath!);
        session.FileValue = newFileValue;
    }
    public async Task<FileValue> FinalizePartialUploadAsync(Guid fileId, bool noNodeUpdate = false, QueryContext? ctx = null) {
        ctx ??= _defaultQueryCtx;
        var session = getSession(fileId);
        FileValue newFileValue;
        var propertyPath = session.FileValue.PropertyPath;
        if (propertyPath == null) throw new Exception("File value does not have a property path");
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileHash = Convert.ToHexString(session.Hash.GetHashAndReset());
        removeSession(fileId);
        var f = session.FileValue;
        var key = FileValue.GetFileKeyData(f);
        newFileValue = FileValue.CreateNew(f.Name, f.Size, fileHash, f.StorageId, f.FileId, key, propertyPath);
        if (!noNodeUpdate) {
            var t = new TransactionData();
            t.ForceUpdateProperty(propertyPath, newFileValue);
            execute_outer(t, false, true, ctx, out _);
        }
        return newFileValue;
    }
    public async Task CancelPartialUpload(Guid fileId) {
        var session = getSession(fileId);
        removeSession(fileId);
        var fileStore = getMultiPartStore(session);
        await fileStore.DeleteAsync(session.FileValue);
    }

    IFileStoreMultiPartSupport getMultiPartStore(uploadSession session) {
        if (getFileStore(session.FileValue.StorageId) is not IFileStoreMultiPartSupport fileStore)
            throw new Exception("File store does not support multipart upload");
        return fileStore;
    }
    void removeSession(Guid fileId) {
        lock (_uploadSessions) {
            if (!_uploadSessions.TryGetValue(fileId, out var session)) return;
            session.Hash.Dispose();
            _uploadSessions.Remove(fileId);
        }
    }
    readonly static TimeSpan _maxStaleAgeForUploadSessions = TimeSpan.FromMinutes(10);
    uploadSession getSession(Guid fileId) {
        lock (_uploadSessions) {
            if (!_uploadSessions.TryGetValue(fileId, out var session)) throw new Exception("Upload session not found");
            session.Touch();
            return session;
        }
    }
    async Task removeOldSessions() {
        List<uploadSession> toRemove;
        lock (_uploadSessions) {
            toRemove = _uploadSessions.Values.Where(s => DateTime.UtcNow - s.LastAccessed > _maxStaleAgeForUploadSessions).ToList();
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
