using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.Web;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
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
        var idWithAdj = new FileIdWithAdjustment(fileValue.FileId, adj, propertyPath);
        var info = new FileConversionInfo(idWithAdj, fileValue.Name, fileValue.Hash, fileValue.Format);
        var fileStore = getFileStore(fileValue.StorageId);
        InputFileSource source;
        var conversionId = idWithAdj.GetKey();
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
        var result = await _fileConversionEngine.TryGetFormatAndStreamAsync(info, maxWait, source);
        Stream stream;
        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
            if (result.Output == null) throw new Exception("File conversion output is null"); // should never happen, but just in case
            stream = result.Output;
        } else {
            stream = _fileConversionEngine.GetStatusDataStream(fileValue, adj, result.ProgressInfo);
        }
        return new StateAndStream(stream, result.ProgressInfo.Status == FileConversionStatus.Ready, fileValue, adj.RequestedFormat, conversionId);
    }
    public bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        var idWithAdj = new FileIdWithAdjustment(fileValue.FileId, adj, propertyPath);
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
    public FileConversions GetConversions(QueryContext? ctx = null) => _fileConversionEngine.GetConversions();
    public async Task CancelAllConversions(bool permanently, QueryContext? ctx = null) {
        foreach (var conversion in _fileConversionEngine.GetConversions().Current) {
            if (conversion.Status == ConversionStatus.Running || conversion.Status == ConversionStatus.Queued) {
                await _fileConversionEngine.CancelRunning(conversion.Id, permanently);
            }
        }
    }
    public Task CancelConversion(Guid conversionId, bool permanently, QueryContext? ctx = null) => _fileConversionEngine.CancelRunning(conversionId, permanently);
    public void ClearAllCachedConversions(QueryContext? ctx = null) => _fileConversionEngine.ClearAllCache();
    public void ClearAllCachedConversionsErrors(QueryContext? ctx = null) => _fileConversionEngine.ClearAllErrors();
}
