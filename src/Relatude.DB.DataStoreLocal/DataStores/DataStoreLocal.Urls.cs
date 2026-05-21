using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.Web;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    Guid _startUpGuid= Guid.NewGuid();
    string getFileVersionId(FileValue fileValue) {
        return (fileValue.Hash + _startUpGuid).GenerateHashInt().ToString();
    }
    public string GetUrl(PropertyPath propertyPath, FileAdjustmentBase adj, bool absolute, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty || fileValue.PropertyPath == null) {
            throw new Exception($"Property at path {propertyPath} does not contain a file.");
        }
        var internalUrl = _urlProvider.GetInternalUrl(fileValue.PropertyPath, adj, getFileVersionId(fileValue));
        return _urlProvider.GetExternalUrl(internalUrl, absolute);
    }
    public async Task<Stream> GetFile(string url, int maxWait, QueryContext? ctx = null) { 
        return (await GetFileAndConversionState(url, maxWait, ctx)).Stream;
    }
    public async Task<StreamAndConversionState> GetFileAndConversionState(string url, int maxWait, QueryContext? ctx = null) {
        var internalUrl = _urlProvider.GetInternalUrl(url);
        if (!_urlProvider.TryParseInternalForUrlType(internalUrl, out var type)) throw new Exception("URL is not a valid local URL");
        if (type == UrlType.LocalProperty) {
            if (!_urlProvider.TryParseInternalUrlForPropertyPath(internalUrl, out var path)) throw new Exception("URL does not point to a file property");
            return await GetFile(path, ctx);
        } else if (type == UrlType.LocalAdjusted) {
            if (!_urlProvider.TryParseInternalUrlForPathWithFileAdjustments(internalUrl, out var path, out var adj)) throw new Exception("URL does not point to an adjusted file property");
            return await GetConvertedFile(path, adj, maxWait, ctx);
        if (!_urlProvider.TryParseLocalUrlType(internalUrl, out var type)) throw new Exception("URL is not a valid local URL");
        if (type == UrlTargetType.LocalProperty) {
            if (!_urlProvider.TryParsePropertyPath(internalUrl, out var path)) throw new Exception("URL does not point to a file property");
            return new(await GetFile(path, ctx), true);
        } else if (type == UrlTargetType.LocalAdjusted) {
            if (!_urlProvider.TryParseAdjusted(internalUrl, out var path, out var adj)) throw new Exception("URL does not point to an adjusted file property");
            return await GetConvertedFileAndConversionState(path, adj, maxWait, ctx);
        }
        throw new Exception("URL does not point to a file property");
    }
    public async Task<Stream> GetFile(PropertyPath propertyPath, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(fileValue.StorageId);
        return await fileStore.GetFileStream(fileValue);
    }
    public async Task<Stream> GetConvertedFile(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait, QueryContext? ctx = null) {
        return (await GetConvertedFileAndConversionState(propertyPath, adj, maxWait, ctx)).Stream;
    }
    public async Task<StreamAndConversionState> GetConvertedFileAndConversionState(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        var idWithAdj = new FileIdWithAdjustment(fileValue.FileId, adj);
        var info = new FileConversionInfo(idWithAdj, fileValue.Name, fileValue.Hash, fileValue.Format);
        var result = await _fileConversionEngine.TryGetFormatAsync(info, maxWait, () => {
            if (fileValue.IsEmpty) throw new Exception("File value is empty");
            var fileStore = getFileStore(fileValue.StorageId);
            return fileStore.GetFileStream(fileValue);
        });
        Stream stream;
        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
            if (result.Output == null) throw new Exception("File conversion output is null");
            stream = result.Output;
        } else {
            stream = _fileConversionEngine.GetStatus(fileValue, adj, result.ProgressInfo);
        }
        return new StreamAndConversionState(stream, result.ProgressInfo.Status == FileConversionStatus.Ready);
    }
}
