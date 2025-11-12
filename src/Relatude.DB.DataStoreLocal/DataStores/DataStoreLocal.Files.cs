using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
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
    public async Task FileDeleteAsync(Guid nodeId, Guid propertyId) {
        var node = Get(nodeId);
        if (!node.TryGetValue(propertyId, out var value)) return;
        var fileValue = FilePropertyModel.ForceValueType(value, out _);
        if (fileValue.IsEmpty) return;
        var fileStore = getFileStore(FileValue.GetStorageId(fileValue));
        await fileStore.DeleteAsync(fileValue);
        var t = new TransactionData();
        t.ForceUpdateProperty(nodeId, propertyId, FileValue.Empty);
        execute_outer(t, false, true, out _);
    }
    public async Task FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string fileKey, string fileName) {
        if (!Datamodel.Properties.TryGetValue(propertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;    
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        using var inputStream = source.OpenRead(fileKey, 0);
        var fileValue = await fileStore.InsertAsync(inputStream, fileName);
        var t = new TransactionData();
        t.ForceUpdateProperty(nodeId, propertyId, fileValue);
        execute_outer(t, false, true, out _);
    }
    public async Task FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileKey, string fileName) {
        if (!Datamodel.Properties.TryGetValue(propertyId, out var prop)) throw new Exception("Property not found");
        if (prop.PropertyType != PropertyType.File) throw new Exception("Property is not a file");
        var fileProp = (FilePropertyModel)prop;
        var fileStore = getFileStore(fileProp.FileStorageProviderId);
        var fileValue = await fileStore.InsertAsync(source, fileName);
        var t = new TransactionData();
        t.ForceUpdateProperty(nodeId, propertyId, fileValue);
        execute_outer(t, false, true, out _);
    }
    public Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream) {
        var node = Get(nodeId);
        if (!node.TryGetValue(propertyId, out var value)) throw new Exception("Property not found");
        var fileValue = FilePropertyModel.ForceValueType(value, out _);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(FileValue.GetStorageId(fileValue));
        return fileStore.ExtractAsync(fileValue, outStream);
    }
    public async Task<bool> FileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId) {
        var node = Get(nodeId);
        if (!node.TryGetValue(propertyId, out var value)) throw new Exception("Property not found");
        var fileValue = FilePropertyModel.ForceValueType(value, out _);
        if (fileValue.IsEmpty) return false;
        var fileStore = getFileStore(FileValue.GetStorageId(fileValue));
        return await fileStore.ContainsFileAsync(fileValue);
    }
}
