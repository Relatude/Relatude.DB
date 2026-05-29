using Microsoft.CodeAnalysis.CSharp;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.Web;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    Guid _startUpGuid = Guid.NewGuid();
    string getFileVersionId(FileValue fileValue) {
        return (fileValue.Hash + _startUpGuid).GenerateHashInt().ToString();
    }
    public bool CanConvert(FileFormat from, FileFormat to) {
        return _fileConversionEngine.CanConvert(from, to);
    }
    public bool CanConvert(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty || fileValue.PropertyPath == null) {
            return false;
        }
        return _fileConversionEngine.CanConvert(fileValue.Format, adj.RequestedFormat);
    }
    public string GetUrl(NodePath nodePath, bool absolute, QueryContext? ctx = null) {
        return _urlProvider.GetExternalUrl(nodePath, absolute);
    }
    public string GetUrl(PropertyPath propertyPath, FileAdjustment adj, bool absolute, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty || fileValue.PropertyPath == null) {
            throw new Exception($"Property at path {propertyPath} does not contain a file.");
        }
        var internalUrl = _urlProvider.GetInternalUrl(fileValue.PropertyPath, adj, getFileVersionId(fileValue));
        return _urlProvider.GetExternalUrl(internalUrl, absolute);
    }
    public async Task<Stream> GetFileStream(string url, int maxWait = -1, QueryContext? ctx = null) {
        return (await GetFileStreamAndState(url, maxWait, ctx)).Stream;
    }
    public async Task<StateAndStream> GetFileStreamAndState(string url, int maxWait = -1, QueryContext? ctx = null) {
        var internalUrl = _urlProvider.GetInternalUrl(url);
        if (!_urlProvider.TryParseInternalForUrlType(internalUrl, out var type)) throw new Exception("URL is not a valid local URL");
        if (type == UrlType.LocalProperty) {
            if (!_urlProvider.TryParseInternalUrlForPropertyPath(internalUrl, out var path)) throw new Exception("URL does not point to a file property");
            var fileValue = GetValue<FileValue>(path, ctx);
            var fileStore = getFileStore(fileValue.StorageId);
            var stream = await fileStore.GetFileStream(fileValue);
            return new StateAndStream(stream, false, fileValue, fileValue.Format);
        } else if (type == UrlType.LocalAdjusted) {
            if (!_urlProvider.TryParseInternalUrlForPathWithFileAdjustments(internalUrl, out var path, out var adj)) throw new Exception("URL does not point to an adjusted file property");
            return await GetFileStreamAndState(path, adj, maxWait, ctx);
        }
        throw new Exception("URL does not point to a file property");
    }
    public async Task<Stream> GetFileStream(PropertyPath propertyPath, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(fileValue.StorageId);
        return await fileStore.GetFileStream(fileValue);
    }
    public async Task<Stream> GetFileStream(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null) {
        return (await GetFileStreamAndState(propertyPath, adj, maxWait, ctx)).Stream;
    }
    public async Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        var idWithAdj = new FileIdWithAdjustment(fileValue.FileId, adj);
        var info = new FileConversionInfo(idWithAdj, fileValue.Name, fileValue.Hash, fileValue.Format);
        var fileStore = getFileStore(fileValue.StorageId);
        InputFileSource source;
        if (fileStore.TryGetLocalFilePath(fileValue, out var localFilePath)) {
            var getInputStreamFunc = new Func<Task<Stream>>(async () => {
                return File.OpenRead(localFilePath);
            });
            source = new InputFileSource(getInputStreamFunc, localFilePath);
        } else {
            var getInputStreamFunc = new Func<Task<Stream>>(async () => {
                if (fileValue.IsEmpty) throw new Exception("File value is empty");
                return await fileStore.GetFileStream(fileValue);
            });
            source = new InputFileSource(getInputStreamFunc, null);
        }
        var result = await _fileConversionEngine.TryGetFormatAsync(info, maxWait, source);
        Stream stream;
        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
            if (result.Output == null) throw new Exception("File conversion output is null");
            stream = result.Output;
        } else {
            stream = _fileConversionEngine.GetStatusDataStream(fileValue, adj, result.ProgressInfo);
        }
        return new StateAndStream(stream, result.ProgressInfo.Status != FileConversionStatus.Ready, fileValue, adj.RequestedFormat);
    }
    public bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        var idWithAdj = new FileIdWithAdjustment(fileValue.FileId, adj);
        var fileStore = getFileStore(fileValue.StorageId);
        var fileConversionInfo = new FileConversionInfo(idWithAdj, fileValue.Name, fileValue.Hash, fileValue.Format);
        return _fileConversionEngine.TryGetProgressInfo(fileConversionInfo, requestIfNot, new InputFileSource(() => fileStore.GetFileStream(fileValue), null), out progressInfo);
    }
    public bool IsFileReady(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, QueryContext? ctx = null) {
        return TryGetConversionInfo(propertyPath, adj, requestIfNot, out var progressInfo, ctx) && progressInfo.Status != FileConversionStatus.InProgress;
    }
    public void EnsureConversionRequested(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null) {
        TryGetConversionInfo(propertyPath, adj, true, out _, ctx);
    }
    public ConversionInfo[] GetRunningConversions(QueryContext? ctx = null) => _fileConversionEngine.GetRunning();
    public void CancelAllConversions(QueryContext? ctx = null) => _fileConversionEngine.CancelAllRunning();
    public void CancelConversion(Guid conversionId, QueryContext? ctx = null) => _fileConversionEngine.CancelRunning(conversionId);
    public void ClearAllCachedConversions(QueryContext? ctx = null) => _fileConversionEngine.ClearAllCache();
    public void ClearAllCachedConversionsErrors(QueryContext? ctx = null) => _fileConversionEngine.ClearAllErrors();
}
